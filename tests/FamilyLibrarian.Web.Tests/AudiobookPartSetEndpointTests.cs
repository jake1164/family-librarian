using System.Buffers.Binary;
using System.Net.Http.Json;
using System.Text;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Contracts.Acquisition;
using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Contracts.Providers;
using FamilyLibrarian.Contracts.Requests;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Domain.Security;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FamilyLibrarian.Web.Tests;

// Each scenario has its own [TestClass] and Postgres: the recheck service looks
// at every enabled provider and pending request in the database, so scenarios
// sharing one database would claim each other's requests (see
// ExternalProviderAutomaticAcquisitionEndpointTests).

/// <summary>
/// A provider that lists one audiobook as "Part 1 of 2" and "Part 2 of 2" gets
/// both parts fetched, scanned and identity-checked on their own, and the book
/// is trusted only once the second part has arrived.
/// </summary>
[TestClass]
public sealed class AudiobookPartSetCompleteSetEndpointTests
{
    private static readonly int[] PartNumbers = [1, 2];
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
    public async Task ACompleteSetIsFetchedAndTrustedOnlyOnceEveryPartHasArrived()
    {
        var provider = new PartSetExternalProviderClient { RunningRefs = { "part-2" } };
        await using var scenario = await PartSetScenario.StartAsync(WebTestFixture.Require(_fixture), provider);

        await scenario.RecheckAsync();

        var attempt = (await scenario.AttemptsAsync()).Single();
        Assert.AreEqual("Submitted", attempt.Outcome);
        StringAssert.Contains(attempt.Summary, "2-part audiobook set");
        await using (var scope = scenario.Factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var jobs = await database.ProviderAcquisitionJobs.Where(job => job.RequestId == scenario.RequestId)
                .OrderBy(job => job.PartNumber).ToArrayAsync();
            Assert.HasCount(2, jobs);
            Assert.AreEqual(1, jobs.Select(job => job.PartSetId).Distinct().Count());
            Assert.IsNotNull(jobs[0].PartSetId);
            CollectionAssert.AreEqual(PartNumbers, jobs.Select(job => job.PartNumber!.Value).ToArray());
            Assert.IsTrue(jobs.All(job => job.PartTotal == 2 && job.IsAutomaticAcquisition));
        }

        // Part 1 arrives first, while part 2 is still downloading.
        await scenario.PollAsync();
        await using (var scope = scenario.Factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var first = await database.MediaAssets.SingleAsync(asset => asset.AssociatedRequestFormatId == scenario.FormatId);
            Assert.AreEqual(1, first.BundleSequence);
            Assert.AreEqual(2, first.BundleTrackCount);
            Assert.AreEqual(
                MediaAssetStorageState.Processing, first.StorageState,
                "A part that passed its scan must wait for its sibling instead of being trusted alone.");
        }

        provider.RunningRefs.Clear();
        await scenario.PollAsync();

        await using var verification = scenario.Factory.Services.CreateAsyncScope();
        var db = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        var assets = await db.MediaAssets.Where(asset => asset.AssociatedRequestFormatId == scenario.FormatId)
            .OrderBy(asset => asset.BundleSequence).ToArrayAsync();
        Assert.HasCount(2, assets);
        Assert.AreEqual(1, assets.Select(asset => asset.BundleId).Distinct().Count());
        CollectionAssert.AreEqual(PartNumbers, assets.Select(asset => asset.BundleSequence!.Value).ToArray());
        Assert.IsTrue(
            assets.All(asset => asset.StorageState is MediaAssetStorageState.Trusted or MediaAssetStorageState.Archived),
            "Both parts are trusted together once the last one has been staged and every part passed.");
        foreach (var asset in assets)
        {
            var approval = await db.SecurityEvaluations.Where(evaluation => evaluation.AssetId == asset.Id)
                .SelectMany(evaluation => evaluation.Approvals).SingleAsync();
            Assert.AreEqual(ApprovalActorType.Policy, approval.ActorType);
        }

        var request = await db.BookRequests.SingleAsync(bookRequest => bookRequest.Id == scenario.RequestId);
        Assert.AreNotEqual(RequestStatus.NeedsReview, request.Status);
    }

}

/// <summary>One part failing abandons the whole set: nothing is kept or published.</summary>
[TestClass]
public sealed class AudiobookPartSetFailedPartEndpointTests
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
    public async Task AFailedPartDestroysTheStagedPartsAndSendsTheRequestToAReviewer()
    {
        var provider = new PartSetExternalProviderClient { FailingRefs = { "part-2" } };
        await using var scenario = await PartSetScenario.StartAsync(WebTestFixture.Require(_fixture), provider);

        await scenario.RecheckAsync();
        await scenario.PollAsync();

        await using var scope = scenario.Factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var jobs = await database.ProviderAcquisitionJobs.Where(job => job.RequestId == scenario.RequestId).ToArrayAsync();
        Assert.IsTrue(jobs.All(job => job.LifecycleState is
            ProviderAcquisitionJobLifecycleState.Failed or ProviderAcquisitionJobLifecycleState.Cancelled or
            ProviderAcquisitionJobLifecycleState.Completed));
        Assert.IsTrue(jobs.All(job => job.NextPollAtUtc is null), "No part of an abandoned set keeps downloading.");

        var assets = await database.MediaAssets.Where(asset => asset.AssociatedRequestFormatId == scenario.FormatId).ToArrayAsync();
        Assert.IsTrue(
            assets.All(asset => asset.StorageState == MediaAssetStorageState.Destroyed),
            "A part that arrived before its sibling failed must not be left behind.");

        var request = await database.BookRequests.SingleAsync(bookRequest => bookRequest.Id == scenario.RequestId);
        Assert.AreEqual(RequestStatus.NeedsReview, request.Status);

        var attempts = await scenario.AttemptsAsync();
        Assert.IsTrue(attempts.Any(attempt => attempt.Outcome == "Failed" && attempt.Summary.Contains("Part 2 of 2", StringComparison.Ordinal)));
    }
}

/// <summary>A set with a missing part is never downloaded; it goes to ordinary review.</summary>
[TestClass]
public sealed class AudiobookPartSetIncompleteSetEndpointTests
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
    public async Task ASetMissingAPartIsReviewedNotDownloaded()
    {
        var provider = new PartSetExternalProviderClient();
        provider.OfferedRefs.Remove("part-2");
        await using var scenario = await PartSetScenario.StartAsync(WebTestFixture.Require(_fixture), provider);

        await scenario.RecheckAsync();

        var onlyAttempt = (await scenario.AttemptsAsync()).Single();
        Assert.AreEqual("CandidatesFound", onlyAttempt.Outcome, onlyAttempt.Summary);
        await using var scope = scenario.Factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.AreEqual(0, await database.ProviderAcquisitionJobs.CountAsync(job => job.RequestId == scenario.RequestId));
        Assert.AreEqual(0, provider.Submissions);
    }
}

file sealed class PartSetScenario : IAsyncDisposable
{
    private PartSetScenario(FamilyLibrarianAppFactory factory, HttpClient admin, Guid requestId, Guid formatId)
    {
        Factory = factory;
        Admin = admin;
        RequestId = requestId;
        FormatId = formatId;
    }

    public FamilyLibrarianAppFactory Factory { get; }

    public HttpClient Admin { get; }

    public Guid RequestId { get; }

    public Guid FormatId { get; }

    public static async Task<PartSetScenario> StartAsync(WebTestFixture fixture, PartSetExternalProviderClient client)
    {
        var factory = new FamilyLibrarianAppFactory(
            fixture.ConnectionString,
            services =>
            {
                services.RemoveAll<IExternalProviderClient>();
                services.AddSingleton<IExternalProviderClient>(client);
            });
        var admin = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var login = await admin.PostAsJsonAsync(
            "/api/auth/login",
            new FamilyLibrarian.Contracts.Authentication.LoginRequest
            {
                Email = FamilyLibrarianAppFactory.AdminEmail,
                Password = FamilyLibrarianAppFactory.AdminPassword
            });
        Assert.AreEqual(System.Net.HttpStatusCode.NoContent, login.StatusCode);
        admin.DefaultRequestHeaders.Add(
            AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(admin));

        var create = await admin.PostAsJsonAsync(
            "/api/v1/admin/external-providers/",
            new CreateExternalProviderRequest("part-set-external", "Part Set External", "http://fake-external.test"));
        var provider = await create.Content.ReadFromJsonAsync<ExternalProviderResponse>();
        Assert.IsNotNull(provider);
        (await admin.PutAsJsonAsync(
            $"/api/v1/admin/external-providers/{provider.Id}/enabled", new SetExternalProviderEnabledRequest(true)))
            .EnsureSuccessStatusCode();
        (await admin.PutAsJsonAsync(
            $"/api/v1/admin/external-providers/{provider.Id}/recheck-schedule",
            new SetExternalProviderRecheckScheduleRequest("Daily"))).EnsureSuccessStatusCode();
        (await admin.PutAsJsonAsync(
            $"/api/v1/admin/external-providers/{provider.Id}/auto-acquire",
            new SetExternalProviderAutoAcquireEnabledRequest(true))).EnsureSuccessStatusCode();

        var resolve = await admin.PostAsync("/api/v1/catalog/candidates/demo/the-hobbit/resolve", content: null);
        var work = await resolve.Content.ReadFromJsonAsync<CatalogWorkResponse>();
        Assert.IsNotNull(work);
        var created = await admin.PostAsJsonAsync(
            "/api/v1/requests/", new CreateBookRequestRequest(work.Id, ["Audiobook"], null, false, false));
        var request = await created.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(request);
        var format = request.Formats.Single(candidate => candidate.MediaType == "Audiobook");
        return new PartSetScenario(factory, admin, request.Id, format.FormatId);
    }

    public async Task RecheckAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var rechecks = scope.ServiceProvider.GetRequiredService<ExternalProviderRecheckService>();
        Assert.IsTrue(await rechecks.ProcessDueAsync(CancellationToken.None) >= 1);
    }

    public async Task PollAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var polling = scope.ServiceProvider.GetRequiredService<AcquisitionJobPollingService>();
        await polling.ProcessDueAsync(CancellationToken.None);
    }

    public async Task<ProviderAttemptResponse[]> AttemptsAsync() =>
        await Admin.GetFromJsonAsync<ProviderAttemptResponse[]>(
            $"/api/v1/admin/requests/{RequestId}/provider-attempts") ?? [];

    public async ValueTask DisposeAsync()
    {
        Admin.Dispose();
        await Factory.DisposeAsync();
    }
}

/// <summary>
/// Offers "The Hobbit" as numbered audiobook fragments and serves each part as a
/// small, valid M4B whose own tags name the book -- so the real security and
/// identity pipeline has something genuine to check.
/// </summary>
file sealed class PartSetExternalProviderClient : IExternalProviderClient
{
    public HashSet<string> OfferedRefs { get; } = ["part-1", "part-2"];

    public HashSet<string> RunningRefs { get; } = [];

    public HashSet<string> FailingRefs { get; } = [];

    public int Submissions { get; private set; }

    public Task<ExternalProviderManifest> GetManifestAsync(
        string baseUrl, string? apiKey, CancellationToken cancellationToken) =>
        Task.FromResult(new ExternalProviderManifest(
            ["2"], "2", null, "part-set-external", "Part Set External", "1.0.0",
            new ProviderCapabilities(["audiobook"], ["search", "acquire"], []), null, null, null));

    public Task<ExternalProviderHealth> GetHealthAsync(
        string baseUrl, string? apiKey, CancellationToken cancellationToken) =>
        Task.FromResult(new ExternalProviderHealth(
            ProviderHealthStatus.Healthy, ProviderOperationalStatus.Available, ProviderOperationalStatus.Available));

    public Task<IReadOnlyList<ExternalProviderCandidate>> SearchAsync(
        string baseUrl, string? apiKey, ExternalProviderSearchRequest request,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ExternalProviderCandidate> candidates = request.MediaType == RequestMediaType.Audiobook
            ? OfferedRefs.Order(StringComparer.Ordinal).Select(reference => new ExternalProviderCandidate(
                reference,
                new ExternalProviderWorkEvidence(
                    "The Hobbit", null, [new BookAuthor("J. R. R. Tolkien", "author")], [], []),
                Release: new ExternalProviderReleaseEvidence(
                    $"The.Hobbit.{reference[^1]}.of.2", "m4b", 1_000, false, 1, false, null, null, [], null,
                    ExternalProviderDrmStatus.None))).ToArray()
            : [];
        return Task.FromResult(candidates);
    }

    public Task<ExternalProviderArtifact> AcquireAsync(
        string baseUrl, string? apiKey, string candidateReference, RequestMediaType mediaType,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<ExternalProviderAcquireSubmission> SubmitAcquireAsync(
        string baseUrl, string? apiKey, ExternalAcquireRequest request, string idempotencyKey,
        CancellationToken cancellationToken)
    {
        Submissions++;
        return Task.FromResult(ExternalProviderAcquireSubmission.Accepted(
            $"job-{request.CandidateReference}", ProviderAcquisitionJobLifecycleState.Queued, phase: null, pollAfterSeconds: 0));
    }

    public Task<ExternalProviderJobStatus> GetAcquireStatusAsync(
        string baseUrl, string? apiKey, string jobId, CancellationToken cancellationToken)
    {
        var reference = jobId["job-".Length..];
        if (FailingRefs.Contains(reference))
        {
            return Task.FromResult(new ExternalProviderJobStatus(
                jobId, ProviderAcquisitionJobLifecycleState.Failed, Phase: null, Interaction: null, Progress: null,
                Error: new ProviderJobError("TRANSFER_FAILED", "The source could not deliver this part.", false, null, null),
                PollAfterSeconds: null));
        }

        return Task.FromResult(new ExternalProviderJobStatus(
            jobId,
            RunningRefs.Contains(reference) ? ProviderAcquisitionJobLifecycleState.Running : ProviderAcquisitionJobLifecycleState.Completed,
            Phase: null, Interaction: null, Progress: null, Error: null, PollAfterSeconds: 0));
    }

    public Task<IReadOnlyList<ExternalProviderOutput>> ListOutputsAsync(
        string baseUrl, string? apiKey, string jobId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ExternalProviderOutput>>(
        [
            new ExternalProviderOutput(
                "primary", ProviderOutputKind.File, "primary", $"{jobId}.m4b", "audio/mp4",
                null, null, null, null, null)
        ]);

    public Task<ExternalProviderArtifact> GetOutputAsync(
        string baseUrl, string? apiKey, string jobId, string outputId,
        CancellationToken cancellationToken) =>
        Task.FromResult(new ExternalProviderArtifact(
            new MemoryStream(M4bFixture.Build("The Hobbit", "J. R. R. Tolkien", seed: jobId[^1])),
            $"{jobId}.m4b"));

    public Task CancelAcquireAsync(
        string baseUrl, string? apiKey, string jobId, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task DeleteAcquireAsync(
        string baseUrl, string? apiKey, string jobId, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

/// <summary>A minimal valid M4B: ftyp, a moov carrying iTunes-style title/artist tags, and a distinct mdat.</summary>
file static class M4bFixture
{
    public static byte[] Build(string title, string author, char seed)
    {
        var tags = Concat(Tag("nam", title), Tag("ART", author));
        var moov = Box("moov", Box("udta", Box("meta", Concat(new byte[4], Box("ilst", tags)))));
        var ftyp = Box("ftyp", [(byte)'M', (byte)'4', (byte)'B', (byte)' ', 0, 0, 0, 0, (byte)'i', (byte)'s', (byte)'o', (byte)'m']);
        // Distinct content per part: identical bytes would be refused as a duplicate.
        var mdat = Box("mdat", Enumerable.Repeat((byte)seed, 2_048).ToArray());
        return Concat(ftyp, moov, mdat);
    }

    private static byte[] Tag(string name, string value)
    {
        var data = Box("data", Concat([0, 0, 0, 1], new byte[4], Encoding.UTF8.GetBytes(value)));
        return Box("\u00A9" + name, data, latin1: true);
    }

    private static byte[] Box(string type, byte[] payload, bool latin1 = false)
    {
        var size = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(size, (uint)(payload.Length + 8));
        return Concat(size, (latin1 ? Encoding.Latin1 : Encoding.ASCII).GetBytes(type), payload);
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(part => part).ToArray();
}
