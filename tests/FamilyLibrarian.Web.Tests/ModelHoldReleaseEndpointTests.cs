using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.ModelTesting;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Contracts.ModelGateway;
using FamilyLibrarian.Contracts.Acquisition;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Audit;
using FamilyLibrarian.Domain.Catalog;
using FamilyLibrarian.Domain.Evidence;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Domain.Security;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FamilyLibrarian.Web.Tests;

/// <summary>Real PostgreSQL, browser authorization, gateway/parser, identity/storage and approval persistence.</summary>
[TestClass]
public sealed class ModelHoldReleaseEndpointTests
{
    [TestMethod]
    [DataRow("release", true, 1)]
    [DataRow("off", false, 0)]
    [DataRow("role-off", false, 0)]
    [DataRow("negative", false, 0)]
    [DataRow("concern", false, 0)]
    [DataRow("race", false, 1)]
    [DataRow("final-race", false, 1)]
    [DataRow("invalid", false, 1)]
    [DataRow("transport", false, 1)]
    [DataRow("bundle", true, 1)]
    [DataRow("audit-failure", false, 1)]
    public async Task OnlyTheGuardedServiceTrustsAndAuditsARelease(string mode, bool released, int calls)
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        var model = new ScriptedModel(mode);
        await using var factory = new FamilyLibrarianAppFactory(fixture.ConnectionString, services =>
        {
            services.RemoveAll<IModelClient>(); services.AddSingleton<IModelClient>(model);
            services.RemoveAll<IAssetIdentityVerifier>(); services.AddSingleton<IAssetIdentityVerifier>(new HeldVerifier(mode));
            if (mode == "audit-failure")
                services.AddScoped<IAuditWriter>(p => new FailingReleaseAudit(new FamilyLibrarian.Infrastructure.Integrations.AuditWriter(
                    p.GetRequiredService<AppDbContext>(), p.GetRequiredService<FamilyLibrarian.Application.Abstractions.ICurrentUser>(),
                    p.GetRequiredService<FamilyLibrarian.Application.Abstractions.IClock>())));
        });
        using var admin = await SignInAsync(factory, true);
        using var member = await SignInAsync(factory, false);
        var command = new SaveModelGatewaySettingsRequest("http://example.test", false,
            [new("ArtifactValidationDecision", mode != "role-off", "Local", "test/release", null)], AllowModelHoldRelease: mode != "off");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await member.PutAsJsonAsync("/api/v1/admin/model-gateway/", command)).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/v1/admin/model-gateway/", command)).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/v1/admin/model-gateway/enabled", new SetModelGatewayEnabledRequest(true))).StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;
        var user = await database.Users.Select(u => u.Id).FirstAsync();
        var work = new Work("Cinder Gate", null, null, null, PublicationStatus.Published, now);
        database.Works.Add(work);
        var request = new BookRequest(user, work.Id, [RequestMediaType.Ebook], null, now);
        database.BookRequests.Add(request);
        var staging = scope.ServiceProvider.GetRequiredService<IAssetStagingStore>();
        await using var content = new MemoryStream("test release bytes"u8.ToArray());
        var file = await staging.WriteToQuarantineAsync(content, "book.epub", 1024, CancellationToken.None);
        var bundleId = mode == "bundle" ? Guid.NewGuid() : (Guid?)null;
        var asset = new MediaAsset(work.Id, null, RequestMediaType.Ebook, ".epub", "book.epub", file.StoredFilename,
            file.SizeBytes, file.Sha256, file.DetectedMimeType, request.Formats.Single().Id, null, now,
            bundleId, bundleId is null ? null : 1, bundleId is null ? null : 2, origin: AssetOrigin.ExternalProvider);
        asset.TransitionStorageState(MediaAssetStorageState.Processing, now);
        await staging.MoveAsync(MediaAssetStorageState.Quarantine, MediaAssetStorageState.Processing, asset.StoredFilename, CancellationToken.None);
        database.MediaAssets.Add(asset);
        var evaluation = new SecurityEvaluation(asset.Id, "test", now);
        evaluation.RecordScanResult("test", true, ScanResultStatus.Clean, null, null, now);
        evaluation.Evaluate(now); database.SecurityEvaluations.Add(evaluation);
        MediaAsset? sibling = null;
        if (bundleId is not null)
        {
            sibling = new MediaAsset(work.Id, null, RequestMediaType.Ebook, ".epub", "sibling.epub", "sibling.epub", 10,
                new string('b', 64), "application/epub+zip", asset.AssociatedRequestFormatId, null, now, bundleId, 2, 2,
                origin: AssetOrigin.ExternalProvider);
            sibling.TransitionStorageState(MediaAssetStorageState.Processing, now);
            sibling.TransitionStorageState(MediaAssetStorageState.Unmatched, now);
            sibling.SetIdentityHoldCode(EvidenceReasonCodes.IdentityHold.LanguageConflict);
            database.MediaAssets.Add(sibling);
        }
        await database.SaveChangesAsync();
        model.AfterCall = mode == "race" ? () => AddLateNegativeAsync(factory, asset) : null;
        var approval = scope.ServiceProvider.GetRequiredService<ApprovalService>();
        if (mode == "final-race")
        {
            var identity = await scope.ServiceProvider.GetRequiredService<IAssetIdentityVerificationService>().VerifyAsync(asset.Id, CancellationToken.None);
            Assert.IsNotNull(identity.ModelReleaseFindingId);
            await AddLateNegativeAsync(factory, asset);
            Assert.AreEqual(ApprovalOutcome.IdentityUnmatched,
                (await approval.ReleaseHoldByModelAsync(asset.Id, identity.ModelReleaseFindingId.Value, CancellationToken.None)).Outcome);
        }
        else
        {
            var result = await approval.ApproveByPolicyAsync(asset.Id, "clean-security-evaluation-v1", CancellationToken.None);
            Assert.AreEqual(released ? ApprovalOutcome.Success : ApprovalOutcome.IdentityUnmatched, result.Outcome, result.Error);
        }
        database.ChangeTracker.Clear();
        Assert.AreEqual(released ? MediaAssetStorageState.Trusted : MediaAssetStorageState.Unmatched,
            (await database.MediaAssets.SingleAsync(a => a.Id == asset.Id)).StorageState);
        Assert.AreEqual(calls, model.Calls);
        if (mode == "concern")
            Assert.AreEqual(HttpStatusCode.BadRequest,
                (await admin.PostAsync($"/api/v1/admin/media-assets/{asset.Id}/retry-identity", null)).StatusCode);
        await using var persistedBytes = await staging.OpenAsync(released ? MediaAssetStorageState.Trusted : MediaAssetStorageState.Unmatched,
            asset.StoredFilename, CancellationToken.None);
        Assert.IsTrue(persistedBytes.Length > 0);
        var audits = await database.AuditEvents.Where(a => a.SubjectId == asset.Id.ToString() && a.Action == AuditActions.AssetIdentityReleasedByModel).ToListAsync();
        Assert.AreEqual(released ? 1 : 0, audits.Count);
        Assert.AreEqual(released, await database.ArtifactEvidenceItems.AnyAsync(e => e.MediaAssetId == asset.Id && e.Source == "model"));
        var releaseEffects = await database.ModelUsageRecords.Where(row => row.WorkflowEffects != null &&
            (row.WorkflowEffects.Value & (int)FamilyLibrarian.Domain.ModelGateway.ModelWorkflowEffect.FileReleaseApplied) != 0).CountAsync();
        Assert.AreEqual(released ? 1 : 0, releaseEffects, "A proposal or rolled-back/audit-failed release must not claim applied impact.");
        if (released)
        {
            Assert.IsNull(audits.Single().ActorUserId);
            StringAssert.Contains(audits.Single().Detail!, "test/release");
            StringAssert.Contains(audits.Single().Detail!, "FindingId");
            Assert.IsTrue(await database.Set<Approval>().AnyAsync(a => a.SecurityEvaluationId == evaluation.Id && a.PolicyName == "model-hold-release-v1"));
            // A later advisory shadow must not hide the applied release's citations in the administrator API/UI.
            database.SemanticFindings.Add(new SemanticFinding(SemanticSubjectKind.Artifact, asset.Id.ToString("N"), null, null,
                "artifact-assessment/1", new string('c', 64), "Match", FamilyLibrarian.Domain.ModelGateway.ModelRole.ArtifactValidationAssessment,
                "shadow-test", false, FamilyLibrarian.Domain.ModelGateway.ModelCallOutcome.Succeeded,
                "Consistent", "[]", null, DateTimeOffset.UtcNow));
            await database.SaveChangesAsync();
            var recent = await admin.GetFromJsonAsync<MediaAssetAdminListResponse>("/api/v1/admin/media-assets/recent?limit=100");
            var shown = recent!.Assets.Single(a => a.AssetId == asset.Id).SemanticFinding;
            Assert.IsTrue(shown!.IsDecision);
            Assert.AreEqual("release", shown.Verdict);
            Assert.IsTrue(shown.Claims.Any(c => c.EvidenceIds.Contains("idv.ev-title")));
        }
        if (sibling is not null) Assert.AreEqual(MediaAssetStorageState.Unmatched, (await database.MediaAssets.SingleAsync(a => a.Id == sibling.Id)).StorageState);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync($"/api/v1/admin/media-assets/{asset.Id}/override-identity", new { reason = "Bypass" })).StatusCode);
    }

    private static async Task<HttpClient> SignInAsync(FamilyLibrarianAppFactory factory, bool admin)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new FamilyLibrarian.Contracts.Authentication.LoginRequest
        { Email = admin ? FamilyLibrarianAppFactory.AdminEmail : WebTestFixture.UserEmail, Password = admin ? FamilyLibrarianAppFactory.AdminPassword : WebTestFixture.UserPassword });
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(client));
        return client;
    }

    private static async Task AddLateNegativeAsync(FamilyLibrarianAppFactory factory, MediaAsset asset)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.ArtifactEvidenceItems.Add(new ArtifactEvidence(asset.Id, asset.Sha256, ArtifactEvidenceRecordKinds.TechnicalInspection,
            Guid.NewGuid(), "late-title", EvidenceDimension.IdentityCorroboration, EvidenceReasonCodes.ArtifactIdentityCorroboration.TitleDiffers,
            EvidencePolarity.Negative, "test", null, null, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
    }

    private sealed class HeldVerifier(string mode) : IAssetIdentityVerifier
    {
        public string Id => "test";
        public bool Supports(MediaAsset asset) => true;
        public Task<AssetIdentityVerificationResult> VerifyAsync(MediaAsset asset, Stream content, CancellationToken cancellationToken)
        {
            IdentityFact[] facts = [new("ev-title", EvidenceDimension.IdentityCorroboration, EvidenceReasonCodes.ArtifactIdentityCorroboration.TitleAgrees, EvidencePolarity.Positive),
                new("ev-author", EvidenceDimension.IdentityCorroboration, EvidenceReasonCodes.ArtifactIdentityCorroboration.AuthorDiffers, EvidencePolarity.Negative),
                new("ev-language", EvidenceDimension.Language, EvidenceReasonCodes.ArtifactLanguage.DeclaredMatchesRequest, EvidencePolarity.Positive),
                new("ev-series", EvidenceDimension.Profile, EvidenceReasonCodes.ProfileComparison.SeriesPositionAgrees, EvidencePolarity.Positive)];
            if (mode == "negative") facts = [.. facts, new("ev-wrong-title", EvidenceDimension.IdentityCorroboration, EvidenceReasonCodes.ArtifactIdentityCorroboration.TitleDiffers, EvidencePolarity.Negative)];
            return Task.FromResult(AssetIdentityVerificationResult.Unmatched(Id, "Byline differs.", facts) with
            { HoldCode = mode == "concern" ? EvidenceReasonCodes.IdentityHold.ModelConcern : EvidenceReasonCodes.IdentityHold.ContentContradicted });
        }
    }

    private sealed class ScriptedModel(string mode) : IModelClient
    {
        public int Calls { get; private set; }
        public Func<Task>? AfterCall { get; set; }
        public async Task<ModelResponse> CompleteAsync(ModelConnection connection, ModelRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            if (AfterCall is not null) await AfterCall();
            if (mode == "transport") return ModelResponse.Failed(ModelFailureKind.ServerError, "Test unavailable.", 1);
            var reply = mode == "invalid" ? "not JSON" : JsonSerializer.Serialize(new { schema = "artifact-decision/1", verdict = "release",
                claims = new[] { new { kind = "supports_declared_identity", evidence_ids = EvidenceIds, note = "The byline variant is corroborated." } } });
            return new(true, ModelFailureKind.None, null, reply, [], "stop", "test/release", new(10, 10, 20), 1);
        }
        private static readonly string[] EvidenceIds = ["idv.ev-title", "idv.ev-language", "idv.ev-series"];
    }

    private sealed class FailingReleaseAudit(IAuditWriter inner) : IAuditWriter
    {
        public Task WriteAsync(string action, string subjectType, string? subjectId, object? detail, CancellationToken cancellationToken) =>
            inner.WriteAsync(action, subjectType, subjectId, detail, cancellationToken);
        public Task WriteSystemAsync(string action, string subjectType, string? subjectId, object? detail, CancellationToken cancellationToken) =>
            action == AuditActions.AssetIdentityReleasedByModel ? throw new InvalidOperationException("Injected audit outage.") :
                inner.WriteSystemAsync(action, subjectType, subjectId, detail, cancellationToken);
    }
}
