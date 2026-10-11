using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Contracts.Acquisition;
using FamilyLibrarian.Contracts.Authentication;
using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Contracts.Requests;
using FamilyLibrarian.Contracts.Realtime;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using System.Threading.Channels;
using FamilyLibrarian.Domain.Security;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// Covers the admin acquisition/security queue: the read surface an
/// administrator needs to discover what's awaiting approval or a retry, since
/// the manual-import and approve/reject endpoints only ever address an asset
/// the caller already knows the id of.
/// </summary>
[TestClass]
public sealed class MediaAssetQueueEndpointTests
{
    private static WebTestFixture? _fixture;

    [ClassInitialize]
    public static async Task InitializeAsync(TestContext testContext)
    {
        ArgumentNullException.ThrowIfNull(testContext);
        _fixture = await WebTestFixture.CreateAsync();
    }

    [ClassCleanup]
    public static async Task CleanupAsync()
    {
        if (_fixture is not null)
        {
            await _fixture.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task AnUploadIsAutomaticallyEvaluatedBeforeAppearingInTheQueue()
    {
        // Title/author deliberately don't match the-hobbit's catalog metadata
        // (see EpubAssetIdentityVerifier): a clean scan alone is not enough to
        // leave the queue automatically — it also has to be held for identity
        // review, which is what keeps this asset visible here to assert on.
        // A clean scan that also matches is covered by
        // SecurityGateEndpointTests.ACleanFileIsApprovedByPolicyAndBecomesTrusted.
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = CreateFactory(fixture, new DeterministicFakeMalwareScanner(ScanResultStatus.Clean));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await SignInAsAdminAsync(client);
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(client);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);
        var (requestId, formatId) = await CreateEbookRequestAsync(client);

        var upload = await client.PostAsync(
            $"/api/v1/admin/requests/{requestId}/formats/{formatId}/manual-import",
            BuildUpload(EpubTestFixture.BuildMinimalEpubBytes("A Different Book", "Someone Else"), "book.epub"));
        Assert.AreEqual(HttpStatusCode.OK, upload.StatusCode);
        var imported = await upload.Content.ReadFromJsonAsync<ManualImportResultResponse>();
        Assert.IsNotNull(imported);

        var queue = await client.GetFromJsonAsync<MediaAssetAdminListResponse>("/api/v1/admin/media-assets/");
        Assert.IsNotNull(queue);

        var entry = queue.Assets.SingleOrDefault(asset => asset.AssetId == imported.MediaAssetId);
        Assert.IsNotNull(entry, "The freshly staged asset should be in the queue.");
        Assert.AreEqual(requestId, entry.RequestId);
        Assert.AreEqual("Unmatched", entry.StorageState);
        Assert.IsNotNull(entry.LatestEvaluation);
        Assert.AreEqual(nameof(SecurityEvaluationStatus.Passed), entry.LatestEvaluation.Status);

        // M1-2: evidence of what the existing checks observed, kept in separate dimensions.
        Assert.IsNotNull(entry.Evidence);
        Assert.AreEqual("Recorded", entry.Evidence.Status);
        Assert.IsTrue(entry.Evidence.Items.All(item => item.FindingId.Length > 0 && item.RecordId != Guid.Empty));
        Assert.HasCount(entry.Evidence.Items.Count, entry.Evidence.Items.Select(item => (item.RecordId, item.FindingId)).Distinct().ToArray());
        Assert.IsTrue(entry.Evidence.Items.Any(item => item.Code == "artifact.security.scan_clean"));
        Assert.IsTrue(entry.Evidence.Items.Any(item => item.Code == "artifact.completeness.not_assessed" && item.Polarity == "Unknown"));
        var attestation = entry.Evidence.Items.Single(item => item.Dimension == "IdentityAttestation");
        Assert.AreEqual("artifact.identity_attestation.manual_upload", attestation.Code);
        Assert.AreEqual("Weak", attestation.Polarity, "Attestation is a statement about the source, never content verification.");
        Assert.AreEqual("artifact.identity_corroboration.contradicted",
            entry.Evidence.Items.Single(item => item.FindingId == "ev-corroboration").Code,
            "An explicit content contradiction is recorded even though the upload was attested.");
        Assert.IsTrue(entry.Evidence.Items.Any(item => item.Code == "artifact.identity_title.differs"));
    }

    [TestMethod]
    public async Task AnApprovedAssetLeavesTheQueue()
    {
        // The default fixture's title/author match the-hobbit's catalog
        // metadata, so a clean scan is approved by policy and published
        // immediately during the upload itself — there is no separate admin
        // approval step to exercise here anymore.
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = CreateFactory(fixture, new DeterministicFakeMalwareScanner(ScanResultStatus.Clean));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await SignInAsAdminAsync(client);
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(client);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);
        var (requestId, formatId) = await CreateEbookRequestAsync(client);

        var upload = await client.PostAsync(
            $"/api/v1/admin/requests/{requestId}/formats/{formatId}/manual-import",
            BuildUpload(BuildMinimalEpubBytes(), "book.epub"));
        var imported = await upload.Content.ReadFromJsonAsync<ManualImportResultResponse>();
        Assert.IsNotNull(imported);

        var queue = await client.GetFromJsonAsync<MediaAssetAdminListResponse>("/api/v1/admin/media-assets/");
        Assert.IsNotNull(queue);
        Assert.IsFalse(queue.Assets.Any(asset => asset.AssetId == imported.MediaAssetId));

        var recent = await client.GetFromJsonAsync<MediaAssetAdminListResponse>("/api/v1/admin/media-assets/recent?limit=100");
        Assert.IsNotNull(recent);
        var retained = recent.Assets.Single(asset => asset.AssetId == imported.MediaAssetId);
        Assert.IsNotNull(retained.LatestEvaluation);
        Assert.IsNotNull(retained.LatestEvaluation.CompletedAtUtc);

        // Recording evidence did not change the approval: still trusted/published, and the evidence
        // shows the independent content corroboration beside the (weaker) attestation.
        Assert.IsNotNull(retained.Evidence);
        Assert.AreEqual("Recorded", retained.Evidence.Status);
        Assert.AreEqual("artifact.identity_corroboration.corroborated",
            retained.Evidence.Items.Single(item => item.FindingId == "ev-corroboration").Code);
        Assert.AreEqual("artifact.identity_attestation.manual_upload",
            retained.Evidence.Items.Single(item => item.Dimension == "IdentityAttestation").Code);
        Assert.IsTrue(retained.Evidence.Items.Any(item => item.Dimension == "Language"));
    }

    [TestMethod]
    public async Task AnAssetProcessedBeforeEvidenceExistedShowsNotRecordedAndABadWriteCannotBreakLaterSaves()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = CreateFactory(fixture, new DeterministicFakeMalwareScanner(ScanResultStatus.Clean));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await SignInAsAdminAsync(client);
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(client);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);
        var (requestId, formatId) = await CreateEbookRequestAsync(client);
        var upload = await client.PostAsync(
            $"/api/v1/admin/requests/{requestId}/formats/{formatId}/manual-import",
            BuildUpload(EpubTestFixture.BuildMinimalEpubBytes("A Different Book", "Someone Else"), "book.epub"));
        var imported = await upload.Content.ReadFromJsonAsync<ManualImportResultResponse>();
        Assert.IsNotNull(imported);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<FamilyLibrarian.Infrastructure.Persistence.AppDbContext>();
            var recorded = await database.ArtifactEvidenceItems.Where(item => item.MediaAssetId == imported.MediaAssetId).ToListAsync();
            Assert.IsNotEmpty(recorded);

            // A duplicate (asset, record, finding) violates the unique index: the store reports failure,
            // detaches the bad rows, and an unrelated later save on the same context still succeeds.
            var store = scope.ServiceProvider.GetRequiredService<IArtifactEvidenceStore>();
            var first = recorded[0];
            store.Add([new FamilyLibrarian.Domain.Evidence.ArtifactEvidence(
                first.MediaAssetId, first.Sha256, first.RecordKind, first.RecordId, first.FindingId, first.Dimension,
                first.Code, first.Polarity, first.Source, first.Value, first.ToolVersion, DateTimeOffset.UtcNow)]);
            Assert.IsFalse(await store.TrySaveAsync(CancellationToken.None));
            Assert.IsEmpty(database.ChangeTracker.Entries<FamilyLibrarian.Domain.Evidence.ArtifactEvidence>()
                .Where(entry => entry.State == EntityState.Added).ToArray());
            await database.SaveChangesAsync();

            // Model a file processed before evidence recording existed.
            await database.ArtifactEvidenceItems.Where(item => item.MediaAssetId == imported.MediaAssetId).ExecuteDeleteAsync();
        }

        var queue = await client.GetFromJsonAsync<MediaAssetAdminListResponse>("/api/v1/admin/media-assets/");
        var legacy = queue!.Assets.Single(asset => asset.AssetId == imported.MediaAssetId);
        Assert.IsNotNull(legacy.Evidence);
        Assert.AreEqual("NotRecorded", legacy.Evidence.Status);
        Assert.IsEmpty(legacy.Evidence.Items);
        Assert.AreEqual("Unmatched", legacy.StorageState, "Absent evidence must not change the existing hold.");
    }

    [TestMethod]
    public async Task ARejectedAssetLeavesTheQueue()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = CreateFactory(
            fixture, new DeterministicFakeMalwareScanner(ScanResultStatus.Detected, "Eicar-Test-Signature"));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await SignInAsAdminAsync(client);
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(client);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);
        var (requestId, formatId) = await CreateEbookRequestAsync(client);

        var upload = await client.PostAsync(
            $"/api/v1/admin/requests/{requestId}/formats/{formatId}/manual-import",
            BuildUpload(BuildMinimalEpubBytes(), "book.epub"));
        var imported = await upload.Content.ReadFromJsonAsync<ManualImportResultResponse>();
        Assert.IsNotNull(imported);

        // The fail-closed policy already moved a Failed evaluation's asset to
        // Rejected on its own — it should have left the queue without any
        // further admin action.
        var queue = await client.GetFromJsonAsync<MediaAssetAdminListResponse>("/api/v1/admin/media-assets/");
        Assert.IsNotNull(queue);
        Assert.IsFalse(queue.Assets.Any(asset => asset.AssetId == imported.MediaAssetId));

        var recent = await client.GetFromJsonAsync<MediaAssetAdminListResponse>("/api/v1/admin/media-assets/recent?limit=100");
        Assert.IsNotNull(recent);
        var retained = recent.Assets.Single(asset => asset.AssetId == imported.MediaAssetId);
        Assert.IsNotNull(retained.LatestEvaluation);
        Assert.IsNotNull(retained.LatestEvaluation.CompletedAtUtc);
    }

    [TestMethod]
    public async Task ASampleMarkedEpubIsStillApprovedAsBeforeButTheCompletenessCueIsRecorded()
    {
        // M1-3b is evidence only: identity matches, the scan is clean, so the decision is unchanged
        // (approved and published). The pre-approval hold on this cue arrives in M1-5.
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = CreateFactory(fixture, new DeterministicFakeMalwareScanner(ScanResultStatus.Clean));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await SignInAsAdminAsync(client);
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(client);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);
        var (requestId, formatId) = await CreateEbookRequestAsync(client);

        var upload = await client.PostAsync(
            $"/api/v1/admin/requests/{requestId}/formats/{formatId}/manual-import",
            BuildUpload(EpubTestFixture.BuildMinimalEpubBytes(subject: "Free Sample"), "book.epub"));
        var imported = await upload.Content.ReadFromJsonAsync<ManualImportResultResponse>();
        Assert.IsNotNull(imported);

        var queue = await client.GetFromJsonAsync<MediaAssetAdminListResponse>("/api/v1/admin/media-assets/");
        Assert.IsFalse(queue!.Assets.Any(asset => asset.AssetId == imported.MediaAssetId), "Decision unchanged: still approved.");

        var recent = await client.GetFromJsonAsync<MediaAssetAdminListResponse>("/api/v1/admin/media-assets/recent?limit=100");
        var asset = recent!.Assets.Single(entry => entry.AssetId == imported.MediaAssetId);
        Assert.IsNotNull(asset.Evidence);
        var cue = asset.Evidence.Items.Single(item => item.Code == "artifact.completeness.sample_marker_in_metadata");
        Assert.AreEqual("Completeness", cue.Dimension);
        Assert.AreEqual("Condition", cue.Polarity);
        Assert.AreEqual("subject:sample", cue.Value);
        Assert.AreEqual("artifact.identity_corroboration.corroborated",
            asset.Evidence.Items.Single(item => item.FindingId == "ev-corroboration").Code,
            "A completeness cue is not an identity contradiction.");
        Assert.IsTrue(asset.Evidence.Items.Any(item => item.Code == "artifact.completeness.reading_order_counted"));
    }

    [TestMethod]
    public async Task AnAudiobookWhoseAudioTrackDeclaresAnotherLanguageIsHeldBeforeItCanBeTrusted()
    {
        // A02: title and author match the catalog and the scan is clean, so before M1-4a this reached Trusted
        // and was published. Now the declared audio language holds it through the existing Unmatched path.
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = CreateFactory(fixture, new DeterministicFakeMalwareScanner(ScanResultStatus.Clean));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await SignInAsAdminAsync(client);
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(client);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);
        var (requestId, formatId) = await CreateRequestAsync(client, "Audiobook");

        var upload = await client.PostAsync(
            $"/api/v1/admin/requests/{requestId}/formats/{formatId}/manual-import",
            BuildUpload(M4bTestFixture.Build("The Hobbit", "J. R. R. Tolkien", "deu"), "book.m4b"));
        Assert.AreEqual(HttpStatusCode.OK, upload.StatusCode);
        var imported = await upload.Content.ReadFromJsonAsync<ManualImportResultResponse>();
        Assert.IsNotNull(imported);

        var queue = await client.GetFromJsonAsync<MediaAssetAdminListResponse>("/api/v1/admin/media-assets/");
        var held = queue!.Assets.Single(asset => asset.AssetId == imported.MediaAssetId);
        Assert.AreEqual("Unmatched", held.StorageState, "Held before Trusted; never published.");
        Assert.AreEqual(nameof(SecurityEvaluationStatus.Passed), held.LatestEvaluation!.Status, "Safety checks are unchanged.");
        Assert.IsEmpty(held.LatestEvaluation.Approvals);
        StringAssert.Contains(held.IdentityMismatchReason, "declared language");

        var language = held.Evidence!.Items.Single(item => item.Dimension == "Language");
        Assert.AreEqual("artifact.language.declared_conflicts", language.Code);
        Assert.AreEqual("Negative", language.Polarity);
        Assert.AreEqual("deu", language.Value);
        Assert.AreEqual("artifact.identity_corroboration.corroborated",
            held.Evidence.Items.Single(item => item.FindingId == "ev-corroboration").Code,
            "Title and author were corroborated; the hold is for language alone.");
    }

    [TestMethod]
    public async Task AnAudiobookWhoseAudioTrackDeclaresTheAcceptedLanguageIsStillApproved()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = CreateFactory(fixture, new DeterministicFakeMalwareScanner(ScanResultStatus.Clean));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await SignInAsAdminAsync(client);
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(client);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);
        var (requestId, formatId) = await CreateRequestAsync(client, "Audiobook");

        var upload = await client.PostAsync(
            $"/api/v1/admin/requests/{requestId}/formats/{formatId}/manual-import",
            BuildUpload(M4bTestFixture.Build("The Hobbit", "J. R. R. Tolkien", "eng"), "book.m4b"));
        var imported = await upload.Content.ReadFromJsonAsync<ManualImportResultResponse>();
        Assert.IsNotNull(imported);

        var queue = await client.GetFromJsonAsync<MediaAssetAdminListResponse>("/api/v1/admin/media-assets/");
        Assert.IsFalse(queue!.Assets.Any(asset => asset.AssetId == imported.MediaAssetId), "Existing behavior: approved.");
        var recent = await client.GetFromJsonAsync<MediaAssetAdminListResponse>("/api/v1/admin/media-assets/recent?limit=100");
        var language = recent!.Assets.Single(asset => asset.AssetId == imported.MediaAssetId)
            .Evidence!.Items.Single(item => item.Dimension == "Language");
        Assert.AreEqual("artifact.language.declared_matches_request", language.Code);
        Assert.AreEqual("Positive", language.Polarity);
    }

    [TestMethod]
    public async Task AnAudiobookWithNoDeclaredLanguageIsStillApprovedAndTheLanguageStaysUnknown()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = CreateFactory(fixture, new DeterministicFakeMalwareScanner(ScanResultStatus.Clean));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await SignInAsAdminAsync(client);
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(client);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);
        var (requestId, formatId) = await CreateRequestAsync(client, "Audiobook");

        var upload = await client.PostAsync(
            $"/api/v1/admin/requests/{requestId}/formats/{formatId}/manual-import",
            BuildUpload(M4bTestFixture.Build("The Hobbit", "J. R. R. Tolkien", audioLanguage: null), "book.m4b"));
        var imported = await upload.Content.ReadFromJsonAsync<ManualImportResultResponse>();
        Assert.IsNotNull(imported);

        var recent = await client.GetFromJsonAsync<MediaAssetAdminListResponse>("/api/v1/admin/media-assets/recent?limit=100");
        var asset = recent!.Assets.Single(entry => entry.AssetId == imported.MediaAssetId);
        Assert.AreNotEqual("Unmatched", asset.StorageState, "Absent language never blocks, exactly as before.");
        var language = asset.Evidence!.Items.Single(item => item.Dimension == "Language");
        Assert.AreEqual("artifact.language.not_declared", language.Code);
        Assert.AreEqual("Unknown", language.Polarity, "Absence is Unknown, never Positive.");
    }

    [TestMethod]
    public async Task AnEpubWithAnEmptySpineIsRejectedBeforeApprovalAndTheDefectIsRecorded()
    {
        // E03: the title and author match the catalog and the scan is clean, so before M1-3a this
        // reached Trusted and was published. Now the structural check fails the evaluation.
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = CreateFactory(fixture, new DeterministicFakeMalwareScanner(ScanResultStatus.Clean));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await SignInAsAdminAsync(client);
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(client);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);
        var (requestId, formatId) = await CreateEbookRequestAsync(client);

        var upload = await client.PostAsync(
            $"/api/v1/admin/requests/{requestId}/formats/{formatId}/manual-import",
            BuildUpload(EpubTestFixture.BuildMinimalEpubBytes(withReadingOrder: false), "book.epub"));
        var imported = await upload.Content.ReadFromJsonAsync<ManualImportResultResponse>();
        Assert.IsNotNull(imported);

        var queue = await client.GetFromJsonAsync<MediaAssetAdminListResponse>("/api/v1/admin/media-assets/");
        Assert.IsTrue(queue!.Assets.Any(asset => asset.AssetId == imported.MediaAssetId && asset.StorageState == "Rejected"),
            "Existing behavior: a structurally invalid, non-malware file is retained as Rejected for the admin.");

        var recent = await client.GetFromJsonAsync<MediaAssetAdminListResponse>("/api/v1/admin/media-assets/recent?limit=100");
        var rejected = recent!.Assets.Single(asset => asset.AssetId == imported.MediaAssetId);
        Assert.AreEqual("Rejected", rejected.StorageState, "Never Trusted, never published.");
        Assert.IsNotNull(rejected.LatestEvaluation);
        Assert.AreEqual(nameof(SecurityEvaluationStatus.Failed), rejected.LatestEvaluation.Status);
        Assert.IsEmpty(rejected.LatestEvaluation.Approvals, "Nothing, including policy approval, ran on a failed evaluation.");
        var structure = rejected.LatestEvaluation.ValidationResults.Single(result => result.ValidatorId == "epub-structure");
        Assert.IsFalse(structure.IsValid);
        StringAssert.Contains(structure.Message, "reading order");

        Assert.IsNotNull(rejected.Evidence);
        Assert.AreEqual("Recorded", rejected.Evidence.Status);
        var defect = rejected.Evidence.Items.Single(item => item.Code == "artifact.structure.defect_reported");
        Assert.AreEqual("Structure", defect.Dimension);
        Assert.AreEqual("Negative", defect.Polarity);
        Assert.AreEqual("validator:epub-structure", defect.Source);
        StringAssert.Contains(defect.Value, "reading order");
        Assert.IsFalse(rejected.Evidence.Items.Any(item => item.Code == "artifact.structure.no_defect_reported"));
        Assert.AreEqual("Unknown", rejected.Evidence.Items.Single(item => item.Dimension == "Completeness").Polarity,
            "Completeness stays not assessed; the defect is a structural finding.");
        Assert.IsFalse(rejected.Evidence.Items.Any(item => item.RecordKind == "identity-verification"),
            "Identity verification is never reached for a failed evaluation.");
    }

    [TestMethod]
    public async Task RecentFilesRequireAdminAndLiveConnectionRequiresSignIn()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var anonymous = fixture.CreateAnonymousClient();
        using var reader = await fixture.CreateUserClientAsync();
        using var admin = await fixture.CreateAdminClientAsync();
        foreach (var (client, expected) in new[]
        {
            (anonymous, HttpStatusCode.Unauthorized), (reader, HttpStatusCode.Forbidden), (admin, HttpStatusCode.OK)
        })
        {
            using var list = await client.GetAsync("/api/v1/admin/media-assets/recent");
            Assert.AreEqual(expected, list.StatusCode);
            using var negotiate = await client.PostAsync(LiveUpdates.HubPath + "/negotiate?negotiateVersion=1", null);
            Assert.AreEqual(expected == HttpStatusCode.Unauthorized ? expected : HttpStatusCode.OK, negotiate.StatusCode);
        }
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(101)]
    public async Task RecentFilesRejectsAnUnboundedLimit(int limit)
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await fixture.CreateAdminClientAsync();
        using var response = await client.GetAsync($"/api/v1/admin/media-assets/recent?limit={limit}");
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task LiveUpdatesExposePendingThenCompletedScansAndRecentFilesAreBounded()
    {
        var fixture = WebTestFixture.Require(_fixture);
        var scanner = new PausedScanner();
        await using var factory = CreateFactory(fixture, scanner);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = FamilyLibrarianAppFactory.AdminEmail,
            Password = FamilyLibrarianAppFactory.AdminPassword
        });
        Assert.AreEqual(HttpStatusCode.NoContent, login.StatusCode);
        var cookies = string.Join("; ", login.Headers.GetValues("Set-Cookie").Select(value => value.Split(';', 2)[0]));
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName,
            await WebTestFixture.GetAntiforgeryTokenAsync(client));
        var (requestId, formatId) = await CreateEbookRequestAsync(client);

        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(client.BaseAddress!, LiveUpdates.HubPath), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Headers["Cookie"] = cookies;
            }).Build();
        var signals = Channel.CreateUnbounded<bool>();
        connection.On<LiveUpdateTopics>(LiveUpdates.Changed, topics =>
        {
            if (topics.HasFlag(LiveUpdateTopics.Security)) signals.Writer.TryWrite(true);
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await connection.StartAsync(timeout.Token);

        using var content = BuildUpload(BuildMinimalEpubBytes(), "live.epub");
        var uploadTask = client.PostAsync(
            $"/api/v1/admin/requests/{requestId}/formats/{formatId}/manual-import", content, timeout.Token);
        try
        {
            await scanner.Started.Task.WaitAsync(timeout.Token);
            await signals.Reader.ReadAsync(timeout.Token);
            var pendingList = await client.GetFromJsonAsync<MediaAssetAdminListResponse>(
                "/api/v1/admin/media-assets/recent?limit=1", timeout.Token);
            Assert.IsNotNull(pendingList);
            Assert.HasCount(1, pendingList.Assets);
            var pending = pendingList.Assets.Single();
            Assert.AreEqual("Processing", pending.StorageState);
            Assert.AreEqual(requestId, pending.RequestId);
            Assert.IsNotNull(pending.LatestEvaluation);
            Assert.AreEqual("Pending", pending.LatestEvaluation.Status);
            Assert.IsNull(pending.LatestEvaluation.CompletedAtUtc);
            while (signals.Reader.TryRead(out _)) { }

            scanner.Release.TrySetResult();
            using var upload = await uploadTask;
            Assert.AreEqual(HttpStatusCode.OK, upload.StatusCode);
            await signals.Reader.ReadAsync(timeout.Token);
            var completedList = await client.GetFromJsonAsync<MediaAssetAdminListResponse>(
                "/api/v1/admin/media-assets/recent?limit=1", timeout.Token);
            Assert.IsNotNull(completedList);
            var completed = completedList.Assets.Single();
            Assert.AreEqual(pending.AssetId, completed.AssetId);
            Assert.IsNotNull(completed.LatestEvaluation);
            Assert.AreEqual("Passed", completed.LatestEvaluation.Status);
            Assert.IsTrue(completed.LatestEvaluation.CompletedAtUtc > completed.LatestEvaluation.CreatedAtUtc);
            Assert.IsTrue(completed.LatestEvaluation.ScanResults.Single().ScannedAtUtc > completed.LatestEvaluation.CreatedAtUtc);
        }
        finally
        {
            scanner.Release.TrySetResult();
            await uploadTask;
        }
    }

    private sealed class PausedScanner : IMalwareScanner
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Id => "paused";
        public bool IsRequired => true;
        public Task<ScannerHealth> CheckHealthAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ScannerHealth(true, "test", null));
        public async Task<ScanOutcome> ScanAsync(Stream content, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new ScanOutcome(ScanResultStatus.Clean, null);
        }
    }

    private static FamilyLibrarianAppFactory CreateFactory(WebTestFixture fixture, IMalwareScanner scanner) =>
        new(
            fixture.ConnectionString,
            services =>
            {
                services.RemoveAll<IMalwareScanner>();
                services.AddSingleton(scanner);
            });

    private static async Task SignInAsAdminAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest
            {
                Email = FamilyLibrarianAppFactory.AdminEmail,
                Password = FamilyLibrarianAppFactory.AdminPassword
            });
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static MultipartFormDataContent BuildUpload(byte[] bytes, string fileName)
    {
        var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "file", fileName);
        return form;
    }

    private static Task<(Guid RequestId, Guid FormatId)> CreateEbookRequestAsync(HttpClient client) =>
        CreateRequestAsync(client, "Ebook");

    private static async Task<(Guid RequestId, Guid FormatId)> CreateRequestAsync(HttpClient client, string mediaType)
    {
        var resolve = await client.PostAsync(
            "/api/v1/catalog/candidates/demo/the-hobbit/resolve", content: null);
        resolve.EnsureSuccessStatusCode();
        var work = await resolve.Content.ReadFromJsonAsync<CatalogWorkResponse>();
        Assert.IsNotNull(work);

        var created = await client.PostAsJsonAsync(
            "/api/v1/requests/",
            new CreateBookRequestRequest(await WebTestFixture.Require(_fixture).CopyWorkForTestAsync(work.Id), [mediaType], null, false, false));
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
        var request = await created.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(request);

        var format = request.Formats.Single(format => format.MediaType == mediaType);
        return (request.Id, format.FormatId);
    }

    private static byte[] BuildMinimalEpubBytes() => EpubTestFixture.BuildMinimalEpubBytes();

    private sealed class DeterministicFakeMalwareScanner(ScanResultStatus status, string? threatName = null)
        : IMalwareScanner
    {
        public string Id => "clamav";

        public bool IsRequired => true;

        public Task<ScannerHealth> CheckHealthAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ScannerHealth(true, "fake-1.0", null));

        public Task<ScanOutcome> ScanAsync(Stream content, CancellationToken cancellationToken) =>
            Task.FromResult(new ScanOutcome(status, threatName));
    }
}
