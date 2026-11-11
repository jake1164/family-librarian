using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.DecisionCapture;
using FamilyLibrarian.Contracts.Authentication;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Catalog;
using FamilyLibrarian.Domain.DecisionCapture;
using FamilyLibrarian.Domain.ModelGateway;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Domain.Security;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// C1-c over the real host and PostgreSQL: facts attach to the right saved decisions exactly once, silence records nothing, and the
/// real approval, rejection, discard and failure paths report what happened.
/// </summary>
[TestClass]
public sealed class DecisionOutcomeTests
{
    private static WebTestFixture? fixture;

    [ClassInitialize]
    public static async Task InitializeAsync(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        fixture = await WebTestFixture.CreateAsync();
    }

    [ClassCleanup]
    public static async Task CleanupAsync()
    {
        if (fixture is not null) await fixture.DisposeAsync();
    }

    private static FamilyLibrarianAppFactory Factory() => new(WebTestFixture.Require(fixture).ConnectionString);

    private static DecisionSnapshot Snapshot(ModelRole role, string subject, Guid? requestId = null, Guid? assetId = null, DateTimeOffset? seen = null) =>
        new(role, 1, subject, "digest-" + Guid.NewGuid().ToString("N"), "{}", requestId, null, assetId, false, seen ?? DateTimeOffset.UtcNow);

    private static DecisionCall Call(Guid snapshotId, ModelCallOutcome outcome) =>
        new(snapshotId, null, null, null, ModelPlacement.Local, "local/alias", null, ModelReasoningMode.Inherit, "rendered-messages/1:x", "parser/1", outcome, null, null, DateTimeOffset.UtcNow);

    private static async Task<(Guid UserId, Guid RequestId, Guid FormatId)> SeedRequestAsync(AppDbContext database)
    {
        var userId = await database.Users.Select(u => u.Id).FirstAsync();
        var work = new Work("Outcome Book " + Guid.NewGuid().ToString("N"), null, null, null, PublicationStatus.Unknown, DateTimeOffset.UtcNow);
        database.Works.Add(work);
        await database.SaveChangesAsync();
        var request = new BookRequest(userId, work.Id, [RequestMediaType.Ebook], null, DateTimeOffset.UtcNow);
        database.BookRequests.Add(request);
        await database.SaveChangesAsync();
        return (userId, request.Id, request.Formats.Single().Id);
    }

    private static async Task<List<DecisionOutcome>> OutcomesAsync(AppDbContext database, params Guid[] snapshotIds) =>
        await database.DecisionOutcomes.AsNoTracking().Where(outcome => snapshotIds.Contains(outcome.SnapshotId)).ToListAsync();

    [TestMethod]
    public async Task AFileFactAttachesToThatFilesAssessmentAndHoldDecisionsOnlyAndInfluenceNeedsAnActualNote()
    {
        await using var factory = Factory();
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var asset = Guid.NewGuid();
        var assessment = Snapshot(ModelRole.ArtifactValidationAssessment, asset.ToString("N"), assetId: asset);
        var hold = Snapshot(ModelRole.ArtifactValidationDecision, asset.ToString("N"), assetId: asset);
        var other = Snapshot(ModelRole.ArtifactValidationAssessment, "other", assetId: Guid.NewGuid());
        database.DecisionSnapshots.AddRange(assessment, hold, other);
        database.DecisionCalls.AddRange(Call(assessment.Id, ModelCallOutcome.Succeeded), Call(hold.Id, ModelCallOutcome.SchemaRejected));
        await database.SaveChangesAsync();
        var writer = scope.ServiceProvider.GetRequiredService<IDecisionOutcomeWriter>();

        var attached = await writer.WriteAsync(new DecisionOutcomeFact(
            DecisionOutcomeKind.FileRejected, "admin-rejection", DecisionOutcomeStrength.Strong, DecisionOutcomeTarget.File(asset), true), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.AreEqual(2, attached);
        var rows = await OutcomesAsync(database, assessment.Id, hold.Id, other.Id);
        Assert.HasCount(2, rows);
        Assert.IsTrue(rows.All(row => row.Kind == DecisionOutcomeKind.FileRejected && row.Strength == DecisionOutcomeStrength.Strong && row.CandidateKey is null));
        Assert.IsTrue(rows.Single(row => row.SnapshotId == assessment.Id).InfluencedByModelNote, "The administrator could see a note that the model produced.");
        Assert.IsFalse(rows.Single(row => row.SnapshotId == hold.Id).InfluencedByModelNote, "That decision's reply was rejected, so there was no note to see.");
        Assert.AreEqual(0, await database.DecisionOutcomes.CountAsync(outcome => outcome.SnapshotId == other.Id));
    }

    [TestMethod]
    public async Task ACandidateFactAttachesThroughItsLinkWithTheLocalKeyAndOnlyForThatRequest()
    {
        await using var factory = Factory();
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (_, requestId, _) = await SeedRequestAsync(database);
        var (_, otherRequestId, _) = await SeedRequestAsync(database);
        var triage = Snapshot(ModelRole.CandidateTriage, "t", requestId);
        var ambiguity = Snapshot(ModelRole.CandidateAmbiguityAssessment, "a", requestId);
        var otherRequest = Snapshot(ModelRole.CandidateTriage, "t2", otherRequestId);
        var search = Snapshot(ModelRole.SearchGuidance, "s", requestId);
        database.DecisionSnapshots.AddRange(triage, ambiguity, otherRequest, search);
        database.CandidateLinks.AddRange(
            new CandidateLink(triage.Id, "c1", "example-source", "result-1"), new CandidateLink(triage.Id, "c2", "example-source", "result-2"),
            new CandidateLink(ambiguity.Id, "c1", "unknown", "result-1"), new CandidateLink(otherRequest.Id, "c1", "example-source", "result-1"),
            new CandidateLink(search.Id, "c1", "example-source", "result-1"));
        await database.SaveChangesAsync();
        var writer = scope.ServiceProvider.GetRequiredService<IDecisionOutcomeWriter>();

        var attached = await writer.WriteAsync(new DecisionOutcomeFact(
            DecisionOutcomeKind.VerificationFailed, "automatic-verification", DecisionOutcomeStrength.Strong,
            DecisionOutcomeTarget.Candidate(requestId, "example-source", "result-1")), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.AreEqual(2, attached, "The triage link and the ambiguity link (whose provider was unknown) both match.");
        var rows = await OutcomesAsync(database, triage.Id, ambiguity.Id, otherRequest.Id, search.Id);
        Assert.HasCount(2, rows);
        Assert.IsTrue(rows.All(row => row.CandidateKey == "c1" && row.Kind == DecisionOutcomeKind.VerificationFailed));
        Assert.IsFalse(rows.Any(row => row.SnapshotId == otherRequest.Id), "Another request's decision is never touched.");
        Assert.IsFalse(rows.Any(row => row.SnapshotId == search.Id), "Only the candidate roles take candidate facts.");
    }

    [TestMethod]
    public async Task ASuggestedSearchFactAttachesToTheLatestDecisionForThatFormat()
    {
        await using var factory = Factory();
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var format = Guid.NewGuid();
        var older = Snapshot(ModelRole.SearchGuidance, format.ToString("N"), seen: DateTimeOffset.UtcNow.AddDays(-3));
        var newer = Snapshot(ModelRole.SearchGuidance, format.ToString("N"), seen: DateTimeOffset.UtcNow);
        database.DecisionSnapshots.AddRange(older, newer);
        await database.SaveChangesAsync();

        await scope.ServiceProvider.GetRequiredService<IDecisionOutcomeWriter>().WriteAsync(new DecisionOutcomeFact(
            DecisionOutcomeKind.SearchReturnedNoResults, "ai-search-empty", DecisionOutcomeStrength.Weak, DecisionOutcomeTarget.SuggestedSearches(format)),
            DateTimeOffset.UtcNow, CancellationToken.None);

        var rows = await OutcomesAsync(database, older.Id, newer.Id);
        Assert.AreEqual(newer.Id, rows.Single().SnapshotId);
    }

    [TestMethod]
    public async Task AFactIsRecordedOnceEvenWhenReportedAgainOrConcurrently()
    {
        await using var factory = Factory();
        var asset = Guid.NewGuid();
        Guid snapshotId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var snapshot = Snapshot(ModelRole.ArtifactValidationAssessment, asset.ToString("N"), assetId: asset);
            database.DecisionSnapshots.Add(snapshot);
            await database.SaveChangesAsync();
            snapshotId = snapshot.Id;
        }

        var fact = new DecisionOutcomeFact(DecisionOutcomeKind.FileApproved, "admin-approval", DecisionOutcomeStrength.Strong, DecisionOutcomeTarget.File(asset), true);
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            await using var scope = factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IDecisionOutcomeWriter>().WriteAsync(fact, DateTimeOffset.UtcNow, CancellationToken.None);
        }));
        await using var again = factory.Services.CreateAsyncScope();
        var repeat = await again.ServiceProvider.GetRequiredService<IDecisionOutcomeWriter>().WriteAsync(fact, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.AreEqual(1, results.Sum(), "Exactly one of the concurrent reports attached the fact.");
        Assert.AreEqual(0, repeat);
        var db = again.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.AreEqual(1, await db.DecisionOutcomes.CountAsync(outcome => outcome.SnapshotId == snapshotId));
    }

    [TestMethod]
    public async Task SilenceIsNeverARecordedFactAndNothingIsCreatedForAnUnsavedSubject()
    {
        await using var factory = Factory();
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var before = await database.DecisionOutcomes.CountAsync();

        var attached = await scope.ServiceProvider.GetRequiredService<IDecisionOutcomeWriter>().WriteAsync(new DecisionOutcomeFact(
            DecisionOutcomeKind.FileApproved, "admin-approval", DecisionOutcomeStrength.Strong, DecisionOutcomeTarget.File(Guid.NewGuid()), true),
            DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.AreEqual(0, attached);
        Assert.AreEqual(before, await database.DecisionOutcomes.CountAsync());
        Assert.AreEqual(0, await scope.ServiceProvider.GetRequiredService<IDecisionOutcomeWriter>().WriteAsync(new DecisionOutcomeFact(
            DecisionOutcomeKind.FileApproved, "x", DecisionOutcomeStrength.Strong, new DecisionOutcomeTarget()), DateTimeOffset.UtcNow, CancellationToken.None),
            "A fact with no target attaches to nothing.");
    }

    [TestMethod]
    public async Task TheDatabaseItselfRefusesADuplicateFactIncludingOneWithNoCandidateKey()
    {
        await using var factory = Factory();
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var snapshot = Snapshot(ModelRole.ArtifactValidationAssessment, "dup", assetId: Guid.NewGuid());
        database.DecisionSnapshots.Add(snapshot);
        database.DecisionOutcomes.Add(new DecisionOutcome(snapshot.Id, null, DecisionOutcomeKind.FileApproved, "a", false, DecisionOutcomeStrength.Strong, DateTimeOffset.UtcNow));
        database.DecisionOutcomes.Add(new DecisionOutcome(snapshot.Id, null, DecisionOutcomeKind.FileRejected, "a", false, DecisionOutcomeStrength.Strong, DateTimeOffset.UtcNow));
        await database.SaveChangesAsync();

        database.DecisionOutcomes.Add(new DecisionOutcome(snapshot.Id, null, DecisionOutcomeKind.FileApproved, "b", false, DecisionOutcomeStrength.Weak, DateTimeOffset.UtcNow));
        await Assert.ThrowsExactlyAsync<DbUpdateException>(() => database.SaveChangesAsync());
    }

    [TestMethod]
    public async Task RetentionRunsAfterAWriteAtMostOncePerIntervalAndKeepsStorageAtTheCap()
    {
        await using var factory = Factory();
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await database.DecisionSnapshots.ExecuteDeleteAsync();
        var old = DateTimeOffset.UtcNow.AddDays(-20);
        database.DecisionSnapshots.AddRange(Enumerable.Range(0, 205).Select(i => Snapshot(ModelRole.CandidateTriage, "bulk-" + i, seen: old.AddMinutes(i))));
        await database.SaveChangesAsync();
        var maintenance = scope.ServiceProvider.GetRequiredService<IDecisionCaptureMaintenance>();

        await maintenance.RunIfDueAsync(CancellationToken.None);

        Assert.AreEqual(DecisionCaptureLimits.DefaultSnapshotsPerRole, await database.DecisionSnapshots.CountAsync(), "Over the cap of 200, the five oldest went.");
        Assert.IsFalse(await database.DecisionSnapshots.AnyAsync(s => s.SubjectReference == "bulk-0"));
        database.DecisionSnapshots.AddRange(Enumerable.Range(0, 10).Select(i => Snapshot(ModelRole.CandidateTriage, "more-" + i, seen: DateTimeOffset.UtcNow)));
        await database.SaveChangesAsync();

        await maintenance.RunIfDueAsync(CancellationToken.None);

        Assert.AreEqual(210, await database.DecisionSnapshots.CountAsync(), "A second run inside the interval does nothing.");
        await database.DecisionSnapshots.ExecuteDeleteAsync();
    }

    [TestMethod]
    public async Task RejectingAndThenDiscardingAFileThroughTheRealEndpointsRecordsBothFacts()
    {
        await using var factory = Factory();
        using var admin = await SignedInAdminAsync(factory);
        Guid assetId;
        Guid assessmentId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var asset = await SeedAssetAsync(scope, database, MediaAssetStorageState.Processing);
            assetId = asset.Id;
            var assessment = Snapshot(ModelRole.ArtifactValidationAssessment, asset.Id.ToString("N"), assetId: asset.Id);
            database.DecisionSnapshots.Add(assessment);
            database.DecisionCalls.Add(Call(assessment.Id, ModelCallOutcome.Succeeded));
            await database.SaveChangesAsync();
            assessmentId = assessment.Id;
        }

        var rejected = await admin.PostAsJsonAsync($"/api/v1/admin/media-assets/{assetId}/reject", new { reason = "Wrong book." });
        Assert.AreEqual(HttpStatusCode.NoContent, rejected.StatusCode, await rejected.Content.ReadAsStringAsync());
        var discarded = await admin.DeleteAsync($"/api/v1/admin/media-assets/{assetId}");
        Assert.AreEqual(HttpStatusCode.NoContent, discarded.StatusCode, await discarded.Content.ReadAsStringAsync());

        await using var verify = factory.Services.CreateAsyncScope();
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await OutcomesAsync(db, assessmentId);
        var rejection = rows.Single(row => row.Kind == DecisionOutcomeKind.FileRejected);
        Assert.AreEqual(DecisionOutcomeStrength.Strong, rejection.Strength);
        Assert.IsTrue(rejection.InfluencedByModelNote);
        Assert.AreEqual("admin-rejection", rejection.Source);
        var discard = rows.Single(row => row.Kind == DecisionOutcomeKind.FileDiscarded);
        Assert.AreEqual(DecisionOutcomeStrength.Weak, discard.Strength, "Deleting a rejected file is a weaker statement than the rejection itself.");
    }

    [TestMethod]
    public async Task AnAdministratorsIdentityOverrideThroughTheRealEndpointIsAStrongFact()
    {
        await using var factory = Factory();
        using var admin = await SignedInAdminAsync(factory);
        Guid assetId;
        Guid snapshotId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var asset = await SeedAssetAsync(scope, database, MediaAssetStorageState.Unmatched);
            assetId = asset.Id;
            var hold = Snapshot(ModelRole.ArtifactValidationDecision, asset.Id.ToString("N"), assetId: asset.Id);
            database.DecisionSnapshots.Add(hold);
            await database.SaveChangesAsync();
            snapshotId = hold.Id;
        }

        var response = await admin.PostAsJsonAsync($"/api/v1/admin/media-assets/{assetId}/override-identity", new { reason = "Byline formatting only." });
        Assert.IsTrue(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        await using var verify = factory.Services.CreateAsyncScope();
        var row = (await OutcomesAsync(verify.ServiceProvider.GetRequiredService<AppDbContext>(), snapshotId)).Single();
        Assert.AreEqual(DecisionOutcomeKind.IdentityOverridden, row.Kind);
        Assert.AreEqual(DecisionOutcomeStrength.Strong, row.Strength);
    }

    [TestMethod]
    public async Task AnAutomaticVerificationFailureThroughTheRealServiceMarksThatCandidateFailed()
    {
        await using var factory = Factory();
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (_, requestId, formatId) = await SeedRequestAsync(database);
        var triage = Snapshot(ModelRole.CandidateTriage, formatId.ToString("N") + ":example-source", requestId);
        database.DecisionSnapshots.Add(triage);
        database.CandidateLinks.Add(new CandidateLink(triage.Id, "c1", "example-source", "result-9"));
        await database.SaveChangesAsync();

        var outcome = await scope.ServiceProvider.GetRequiredService<AutomaticRequestFulfillmentService>().RecordExternalAcquisitionFailureAsync(
            requestId, formatId, "example-source", "result-9", null, 3, "The file was a different book.", CancellationToken.None);

        Assert.IsTrue(outcome.AdvancesToNextCandidate, "One failure is within the attempt budget.");
        var row = (await OutcomesAsync(database, triage.Id)).Single();
        Assert.AreEqual(DecisionOutcomeKind.VerificationFailed, row.Kind);
        Assert.AreEqual("c1", row.CandidateKey);
        Assert.IsFalse(row.InfluencedByModelNote, "An automatic check is not an administrator reading the model's note.");
        Assert.IsFalse((row.Source + row.CandidateKey).Contains("different book", StringComparison.OrdinalIgnoreCase), "The free-text reason is never recorded.");
    }

    private static async Task<HttpClient> SignedInAdminAsync(FamilyLibrarianAppFactory factory)
    {
        var admin = factory.CreateClient();
        using var login = await admin.PostAsJsonAsync("/api/auth/login",
            new LoginRequest { Email = FamilyLibrarianAppFactory.AdminEmail, Password = FamilyLibrarianAppFactory.AdminPassword });
        Assert.AreEqual(HttpStatusCode.NoContent, login.StatusCode);
        admin.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(admin));
        return admin;
    }

    private static async Task<MediaAsset> SeedAssetAsync(AsyncServiceScope scope, AppDbContext database, MediaAssetStorageState state)
    {
        var (_, _, formatId) = await SeedRequestAsync(database);
        var work = await database.Works.OrderByDescending(w => w.CreatedAtUtc).FirstAsync();
        var staging = scope.ServiceProvider.GetRequiredService<IAssetStagingStore>();
        using var bytes = new MemoryStream("not really a book"u8.ToArray());
        var file = await staging.WriteToQuarantineAsync(bytes, "book.epub", 100_000, CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        var asset = new MediaAsset(work.Id, null, RequestMediaType.Ebook, ".epub", "book.epub", file.StoredFilename, file.SizeBytes, file.Sha256,
            file.DetectedMimeType, formatId, null, now, origin: AssetOrigin.ExternalProvider);
        asset.TransitionStorageState(MediaAssetStorageState.Processing, now);
        await staging.MoveAsync(MediaAssetStorageState.Quarantine, MediaAssetStorageState.Processing, asset.StoredFilename, CancellationToken.None);
        if (state == MediaAssetStorageState.Unmatched)
        {
            asset.TransitionStorageState(MediaAssetStorageState.Unmatched, now);
            await staging.MoveAsync(MediaAssetStorageState.Processing, MediaAssetStorageState.Unmatched, asset.StoredFilename, CancellationToken.None);
        }

        database.MediaAssets.Add(asset);
        var evaluation = new SecurityEvaluation(asset.Id, "test", now);
        evaluation.RecordScanResult("test", true, ScanResultStatus.Clean, null, null, now);
        evaluation.Evaluate(now);
        database.SecurityEvaluations.Add(evaluation);
        await database.SaveChangesAsync();
        return asset;
    }
}
