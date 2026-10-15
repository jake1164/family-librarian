using System.Net.Http.Json;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Contracts.Acquisition;
using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Contracts.Providers;
using FamilyLibrarian.Contracts.Requests;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FamilyLibrarian.Web.Tests;

internal sealed class ReviewRecheckClock : FamilyLibrarian.Application.Abstractions.IClock
{
    public DateTimeOffset UtcNow { get; } = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
}

[TestClass]
public sealed class ExternalProviderWaitingVariantCycleEndpointTests
{
    [TestMethod]
    public async Task VariantsRunOnceInARecheckAndRunAgainOnTheNextScheduledRecheck()
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        await using var harness = await WaitingHarness.CreateAsync(fixture);
        var request = await harness.RequestHobbitAsync();

        await harness.RecheckAsync();
        Assert.AreEqual(4, harness.Client.SearchCount);
        var first = await harness.ReadAsync(database => database.SearchVariantAttempts.AsNoTracking()
            .Where(row => row.RequestFormatId == request.Formats.Single().FormatId).ToArrayAsync());
        Assert.HasCount(4, first);
        Assert.IsTrue(first.All(row => row.TimesTried == 1));

        await harness.RecheckAsync();
        Assert.AreEqual(4, harness.Client.SearchCount, "No variant is searched again inside the same scheduled cycle.");

        await harness.MakeEverythingDueAsync();
        await harness.RecheckAsync();
        Assert.AreEqual(8, harness.Client.SearchCount);
        var second = await harness.ReadAsync(database => database.SearchVariantAttempts.AsNoTracking()
            .Where(row => row.RequestFormatId == request.Formats.Single().FormatId).ToArrayAsync());
        Assert.HasCount(4, second);
        Assert.IsTrue(second.All(row => row.TimesTried == 2), "Every variant runs again in the next cycle.");
    }
}

// AUTOFIND 2c. A request waiting on a person (a preference review, an admin review after the attempt budget is spent) or
// with results but no acceptable copy keeps being rechecked on the AF-10 sliding scale; a recheck that finds a copy needing
// no answer withdraws the question; a spent budget still allows one extra, capped attempt for a new strong candidate; and the
// batch no longer starves requests beyond the 20 oldest. One class per scenario, each with its own database (the recheck
// considers every provider and request in the database).

internal sealed class WaitingRecheckClient : IExternalProviderClient
{
    private readonly object _gate = new();

    /// <summary>The next search's answer. Set by the test between passes.</summary>
    public Func<ExternalProviderSearchRequest, IReadOnlyList<ExternalProviderCandidate>> Script { get; set; } = _ => [];

    /// <summary>Candidate references whose job reports Failed instead of Completed.</summary>
    public HashSet<string> FailingReferences { get; } = [];

    public int SearchCount { get; private set; }

    public List<string> SubmittedCandidates { get; } = [];

    private readonly Dictionary<string, string> _jobs = [];

    public static ExternalProviderCandidate Candidate(
        string reference, string releaseName, long size, bool withAuthor = true, bool collection = false,
        IReadOnlyList<BookIdentifier>? identifiers = null) =>
        new(reference,
            new ExternalProviderWorkEvidence(
                "The Hobbit", null, withAuthor ? [new BookAuthor("J. R. R. Tolkien", "author")] : [], [], []),
            new ExternalProviderEditionEvidence("en", null, null, identifiers ?? []),
            new ExternalProviderReleaseEvidence(
                releaseName, "epub", size, collection, 1, false, null, null, [], null, ExternalProviderDrmStatus.None));

    public static ExternalProviderCandidate Identifier(string reference, ExternalProviderSearchRequest request, string? releaseName = null, long size = 1_100_000) =>
        Candidate(reference, releaseName ?? $"Tolkien - The Hobbit ({reference}) (epub)", size, identifiers: request.Edition?.Identifiers);

    public static ExternalProviderCandidate Collection(string reference) =>
        Candidate(reference, $"Tolkien - The Hobbit and more ({reference}) (epub)", 2_000_000, collection: true);

    public Task<ExternalProviderManifest> GetManifestAsync(string baseUrl, string? apiKey, CancellationToken cancellationToken) =>
        Task.FromResult(new ExternalProviderManifest(
            ["2"], "2", null, "waiting-source", "Waiting Source", "1.0.0",
            new ProviderCapabilities(["ebook"], ["search", "acquire"], []), null, null, null));

    public Task<ExternalProviderHealth> GetHealthAsync(string baseUrl, string? apiKey, CancellationToken cancellationToken) =>
        Task.FromResult(new ExternalProviderHealth(
            ProviderHealthStatus.Healthy, ProviderOperationalStatus.Available, ProviderOperationalStatus.Available));

    public Task<IReadOnlyList<ExternalProviderCandidate>> SearchAsync(
        string baseUrl, string? apiKey, ExternalProviderSearchRequest request, CancellationToken cancellationToken)
    {
        if (request.MediaType != RequestMediaType.Ebook)
        {
            return Task.FromResult<IReadOnlyList<ExternalProviderCandidate>>([]);
        }

        lock (_gate)
        {
            SearchCount++;
        }

        return Task.FromResult(Script(request));
    }

    public Task<ExternalProviderArtifact> AcquireAsync(
        string baseUrl, string? apiKey, string candidateReference, RequestMediaType mediaType, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<ExternalProviderAcquireSubmission> SubmitAcquireAsync(
        string baseUrl, string? apiKey, ExternalAcquireRequest request, string idempotencyKey, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            SubmittedCandidates.Add(request.CandidateReference);
            var jobId = $"waiting-job-{_jobs.Count + 1}";
            _jobs[jobId] = request.CandidateReference;
            return Task.FromResult(ExternalProviderAcquireSubmission.Accepted(
                jobId, ProviderAcquisitionJobLifecycleState.Running, phase: null, pollAfterSeconds: 0));
        }
    }

    public Task<ExternalProviderJobStatus> GetAcquireStatusAsync(
        string baseUrl, string? apiKey, string jobId, CancellationToken cancellationToken)
    {
        var failing = FailingReferences.Contains(_jobs[jobId]);
        return Task.FromResult(failing
            ? new ExternalProviderJobStatus(
                jobId, ProviderAcquisitionJobLifecycleState.Failed, null, null, null,
                new ProviderJobError("TRANSFER_FAILED", "The source could not deliver this copy.", false, null, null), null)
            : new ExternalProviderJobStatus(
                jobId, ProviderAcquisitionJobLifecycleState.Completed, null, null, null, null, null));
    }

    public Task<IReadOnlyList<ExternalProviderOutput>> ListOutputsAsync(
        string baseUrl, string? apiKey, string jobId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ExternalProviderOutput>>(
            [new ExternalProviderOutput("primary", ProviderOutputKind.File, "primary", "the-hobbit.epub", "application/epub+zip", null, null, null, null, null)]);

    public Task<ExternalProviderArtifact> GetOutputAsync(
        string baseUrl, string? apiKey, string jobId, string outputId, CancellationToken cancellationToken) =>
        Task.FromResult(new ExternalProviderArtifact(new MemoryStream(EpubTestFixture.BuildMinimalEpubBytes()), "the-hobbit.epub"));

    public Task CancelAcquireAsync(string baseUrl, string? apiKey, string jobId, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task DeleteAcquireAsync(string baseUrl, string? apiKey, string jobId, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

internal sealed class WaitingHarness : IAsyncDisposable
{
    private WaitingHarness(FamilyLibrarianAppFactory factory, HttpClient admin, WaitingRecheckClient client, WebTestFixture fixture)
    {
        Factory = factory;
        Admin = admin;
        Client = client;
        Fixture = fixture;
    }

    public FamilyLibrarianAppFactory Factory { get; }

    public HttpClient Admin { get; }

    public WaitingRecheckClient Client { get; }

    public WebTestFixture Fixture { get; }

    public static async Task<WaitingHarness> CreateAsync(WebTestFixture fixture, int attemptLimit = 3)
    {
        var client = new WaitingRecheckClient();
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
        admin.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(admin));

        var created = await admin.PostAsJsonAsync(
            "/api/v1/admin/external-providers/",
            new CreateExternalProviderRequest("waiting-source", "Waiting Source", "http://fake-waiting.test"));
        var provider = await created.Content.ReadFromJsonAsync<ExternalProviderResponse>();
        Assert.IsNotNull(provider);
        (await admin.PutAsJsonAsync($"/api/v1/admin/external-providers/{provider.Id}/enabled", new SetExternalProviderEnabledRequest(true)))
            .EnsureSuccessStatusCode();
        (await admin.PutAsJsonAsync(
            $"/api/v1/admin/external-providers/{provider.Id}/recheck-schedule", new SetExternalProviderRecheckScheduleRequest("Daily")))
            .EnsureSuccessStatusCode();
        (await admin.PutAsJsonAsync(
            $"/api/v1/admin/external-providers/{provider.Id}/automatic-attempt-limit",
            new SetExternalProviderAutomaticAttemptLimitRequest(attemptLimit))).EnsureSuccessStatusCode();
        return new WaitingHarness(factory, admin, client, fixture);
    }

    public async Task<BookRequestResponse> RequestHobbitAsync(bool copyOfWork = false)
    {
        var resolve = await Admin.PostAsync("/api/v1/catalog/candidates/demo/the-hobbit/resolve", content: null);
        var work = await resolve.Content.ReadFromJsonAsync<CatalogWorkResponse>();
        Assert.IsNotNull(work);
        var workId = copyOfWork ? await Fixture.CopyWorkForTestAsync(work.Id) : work.Id;
        var request = await (await Admin.PostAsJsonAsync(
            "/api/v1/requests/", new CreateBookRequestRequest(workId, ["Ebook"], null, false, false)))
            .Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(request);
        return request;
    }

    public async Task RecheckAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ExternalProviderRecheckService>().ProcessDueAsync(CancellationToken.None);
    }

    public async Task PollAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AcquisitionJobPollingService>().ProcessDueAsync(CancellationToken.None);
    }

    /// <summary>Makes every lookup due again, as if its wait had elapsed.</summary>
    public async Task MakeEverythingDueAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlRawAsync(
            "UPDATE acquisition.provider_attempts SET next_eligible_check_at_utc = now() - interval '1 minute'");
    }

    public async Task<T> ReadAsync<T>(Func<AppDbContext, Task<T>> read)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await read(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public Task<ProviderAttemptResponse[]> AttemptsAsync(Guid requestId) =>
        Admin.GetFromJsonAsync<ProviderAttemptResponse[]>($"/api/v1/admin/requests/{requestId}/provider-attempts")!;

    public async ValueTask DisposeAsync()
    {
        Admin.Dispose();
        await Factory.DisposeAsync();
    }
}

/// <summary>A preference review keeps being rechecked; a copy needing no answer withdraws the question and is fetched.</summary>
[TestClass]
public sealed class ExternalProviderWaitingReviewWithdrawnEndpointTests
{
    [TestMethod]
    public async Task AReviewIsRecheckedAndWithdrawnWhenAnAutomaticCopyAppears()
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        await using var harness = await WaitingHarness.CreateAsync(fixture);
        harness.Client.Script = _ => [WaitingRecheckClient.Collection("coll-1")];
        var request = await harness.RequestHobbitAsync();

        await harness.RecheckAsync();
        var afterReview = await harness.ReadAsync(async database => (
            (await database.BookRequests.SingleAsync(item => item.Id == request.Id)).Status,
            await database.RequestReviewCandidates.CountAsync(item => item.RequestId == request.Id)));
        Assert.AreEqual(RequestStatus.NeedsReview, afterReview.Status);
        Assert.AreEqual(1, afterReview.Item2);
        var reviewAttempt = (await harness.AttemptsAsync(request.Id)).Single();
        Assert.AreEqual("CandidatesFound", reviewAttempt.Outcome);
        Assert.IsNotNull(reviewAttempt.NextEligibleCheckAtUtc, "A request waiting on a person keeps being rechecked.");

        // The wait elapses and the source now has a copy that needs no answer.
        harness.Client.Script = search => [WaitingRecheckClient.Identifier("ident-1", search)];
        await harness.MakeEverythingDueAsync();
        await harness.RecheckAsync();

        var after = await harness.ReadAsync(async database => (
            (await database.BookRequests.SingleAsync(item => item.Id == request.Id)).Status,
            await database.RequestReviewCandidates.CountAsync(item => item.RequestId == request.Id),
            await database.NotificationEvents.CountAsync(item => item.Category == "request.review_withdrawn")));
        Assert.AreNotEqual(RequestStatus.NeedsReview, after.Status);
        Assert.AreEqual(0, after.Item2, "The stored review is withdrawn.");
        Assert.AreEqual(1, after.Item3, "The requester is told no answer is needed.");
        CollectionAssert.AreEqual(new List<string> { "ident-1" }, harness.Client.SubmittedCandidates);

        // Written after the status change, so the next pass does not search again at once.
        var searches = harness.Client.SearchCount;
        await harness.RecheckAsync();
        Assert.AreEqual(searches, harness.Client.SearchCount);
    }
}

/// <summary>Finding only the same weak candidates again changes nothing the person can see.</summary>
[TestClass]
public sealed class ExternalProviderWaitingReviewUnchangedEndpointTests
{
    [TestMethod]
    public async Task TheSameWeakCandidatesLeaveTheReviewAndTheNotificationsAlone()
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        await using var harness = await WaitingHarness.CreateAsync(fixture);
        harness.Client.Script = _ => [WaitingRecheckClient.Collection("coll-1")];
        var request = await harness.RequestHobbitAsync();
        await harness.RecheckAsync();
        var before = await harness.ReadAsync(async database => (
            await database.RequestReviewCandidates.AsNoTracking().Where(item => item.RequestId == request.Id).Select(item => item.Id).ToListAsync(),
            await database.NotificationEvents.CountAsync()));

        await harness.MakeEverythingDueAsync();
        await harness.RecheckAsync();

        var after = await harness.ReadAsync(async database => (
            await database.RequestReviewCandidates.AsNoTracking().Where(item => item.RequestId == request.Id).Select(item => item.Id).ToListAsync(),
            await database.NotificationEvents.CountAsync(),
            (await database.BookRequests.SingleAsync(item => item.Id == request.Id)).Status));
        CollectionAssert.AreEqual(before.Item1, after.Item1);
        Assert.AreEqual(before.Item2, after.Item2, "No new notification.");
        Assert.AreEqual(RequestStatus.NeedsReview, after.Item3);
        var attempts = await harness.AttemptsAsync(request.Id);
        Assert.HasCount(2, attempts, "The second look is recorded.");
        Assert.IsTrue(attempts.All(attempt => attempt.NextEligibleCheckAtUtc is not null));
    }
}

/// <summary>The batch of 20 no longer starves the requests after the 20 oldest.</summary>
[TestClass]
public sealed class ExternalProviderRecheckBatchFairnessEndpointTests
{
    [TestMethod]
    public async Task RequestsBeyondTheTwentyOldestAreSearchedWhenTheOldestAreNotDue()
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        await using var harness = await WaitingHarness.CreateAsync(fixture);
        var requests = new List<BookRequestResponse>();
        for (var index = 0; index < 25; index++)
        {
            requests.Add(await harness.RequestHobbitAsync(copyOfWork: true));
        }

        // The 20 oldest were looked at a moment ago and are not due for another day.
        await harness.ReadAsync(async database =>
        {
            foreach (var request in requests.Take(20))
            {
                database.ProviderAttempts.Add(new ProviderAttempt(
                    request.Id, request.Formats.Single().FormatId, "waiting-source", ProviderAttemptOutcome.NoMatch, "Nothing yet.",
                    DateTimeOffset.UtcNow.AddSeconds(30), DateTimeOffset.UtcNow.AddDays(1)));
            }

            await database.SaveChangesAsync();
            return 0;
        });

        await harness.RecheckAsync();

        Assert.IsGreaterThan(0, harness.Client.SearchCount, "The newest requests are searched in the first pass.");
        var searched = await harness.ReadAsync(database => database.ProviderAttempts.AsNoTracking()
            .Where(attempt => attempt.Summary != "Nothing yet.").Select(attempt => attempt.RequestId).Distinct().ToListAsync());
        CollectionAssert.AreEquivalent(requests.Skip(20).Select(request => request.Id).ToList(), searched);
    }
}

/// <summary>A request that must be handled by a person is never searched.</summary>
[TestClass]
public sealed class ExternalProviderManualFulfillmentNeverRecheckedEndpointTests
{
    [TestMethod]
    public async Task ARequestNeedingASpecificVersionIsNeverSearched()
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        await using var harness = await WaitingHarness.CreateAsync(fixture);
        harness.Client.Script = search => [WaitingRecheckClient.Identifier("ident-1", search)];
        var request = await harness.RequestHobbitAsync();
        await harness.ReadAsync(async database =>
        {
            var tracked = await database.BookRequests.Include(item => item.Formats).Include(item => item.StatusHistory)
                .Include(item => item.Participants).SingleAsync(item => item.Id == request.Id);
            tracked.RequireVersionReview("Language", "Needs the German edition.", Guid.NewGuid(), DateTimeOffset.UtcNow);
            await database.SaveChangesAsync();
            return 0;
        });

        await harness.RecheckAsync();

        Assert.AreEqual(0, harness.Client.SearchCount);
        Assert.IsEmpty(await harness.AttemptsAsync(request.Id));
    }
}

/// <summary>
/// After the attempt budget is spent a recheck may still try a new, strong candidate once, capped at the attempt limit and one
/// per cycle; anything weaker, anything already failed or re-posted, and anything past the cap only waits.
/// </summary>
[TestClass]
public sealed class ExternalProviderExtraAttemptAfterSpentBudgetEndpointTests
{
    private const string FailedName = "Tolkien - The Hobbit (first) (epub)";

    private static async Task<(WaitingHarness Harness, BookRequestResponse Request)> SpendTheBudgetAsync(WebTestFixture fixture)
    {
        var harness = await WaitingHarness.CreateAsync(fixture, attemptLimit: 1);
        harness.Client.FailingReferences.Add("first");
        harness.Client.Script = search => [WaitingRecheckClient.Identifier("first", search, FailedName, 1_100_000)];
        var request = await harness.RequestHobbitAsync();
        await harness.RecheckAsync();
        await harness.PollAsync();

        var status = await harness.ReadAsync(async database => (await database.BookRequests.SingleAsync(item => item.Id == request.Id)).Status);
        Assert.AreEqual(RequestStatus.NeedsReview, status, "One failed copy spends a budget of one.");
        return (harness, request);
    }

    [TestMethod]
    public async Task ANewStrongCandidateIsDownloadedOnceWithTheReviewOpen()
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        var (harness, request) = await SpendTheBudgetAsync(fixture);
        await using var _ = harness;
        harness.Client.Script = search =>
        [
            WaitingRecheckClient.Identifier("first", search, FailedName, 1_100_000),
            WaitingRecheckClient.Identifier("second", search, "Tolkien - The Hobbit (second) (epub)", 1_300_000)
        ];

        await harness.MakeEverythingDueAsync();
        await harness.RecheckAsync();
        await harness.PollAsync();

        CollectionAssert.AreEqual(new List<string> { "first", "second" }, harness.Client.SubmittedCandidates);
        var attempts = await harness.AttemptsAsync(request.Id);
        Assert.IsTrue(attempts.Any(attempt => attempt.Summary.StartsWith("Found a stronger new copy after the attempt limit", StringComparison.Ordinal)));
        var state = await harness.ReadAsync(async database => (
            (await database.BookRequests.SingleAsync(item => item.Id == request.Id)).Status,
            await database.MediaAssets.CountAsync(asset => asset.StorageState == MediaAssetStorageState.Trusted)));
        Assert.AreEqual(1, state.Item2, "The new copy passed its checks and is trusted.");
    }

    [TestMethod]
    public async Task ANewCandidateThatFailsIsDeclinedTheReviewStaysAndTheCapStopsFurtherAttempts()
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        var (harness, request) = await SpendTheBudgetAsync(fixture);
        await using var _ = harness;
        harness.Client.FailingReferences.Add("second");
        harness.Client.Script = search =>
        [
            WaitingRecheckClient.Identifier("first", search, FailedName, 1_100_000),
            WaitingRecheckClient.Identifier("second", search, "Tolkien - The Hobbit (second) (epub)", 1_300_000)
        ];
        var notificationsBefore = await harness.ReadAsync(database => database.NotificationEvents.CountAsync());

        await harness.MakeEverythingDueAsync();
        await harness.RecheckAsync();
        await harness.PollAsync();

        var after = await harness.ReadAsync(async database => (
            (await database.BookRequests.SingleAsync(item => item.Id == request.Id)).Status,
            await database.DeclinedRequestCandidates.Where(item => item.RequestId == request.Id).Select(item => item.ProviderResultId).ToListAsync(),
            await database.NotificationEvents.CountAsync()));
        Assert.AreEqual(RequestStatus.NeedsReview, after.Item1);
        CollectionAssert.AreEquivalent(new List<string> { "first", "second" }, after.Item2);
        Assert.AreEqual(notificationsBefore, after.Item3, "No second review notification.");

        // A third strong candidate is past the cap (one extra for a limit of one): it is only recorded, never fetched; and
        // the declined one is never tried again.
        harness.Client.Script = search =>
        [
            WaitingRecheckClient.Identifier("second", search, "Tolkien - The Hobbit (second) (epub)", 1_300_000),
            WaitingRecheckClient.Identifier("third", search, "Tolkien - The Hobbit (third) (epub)", 1_500_000)
        ];
        await harness.MakeEverythingDueAsync();
        await harness.RecheckAsync();
        await harness.PollAsync();
        CollectionAssert.AreEqual(new List<string> { "first", "second" }, harness.Client.SubmittedCandidates);
    }

    [TestMethod]
    public async Task ARepostOfTheFailedReleaseIsNotDownloaded()
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        var (harness, _) = await SpendTheBudgetAsync(fixture);
        await using var _h = harness;
        harness.Client.Script = search =>
        [
            WaitingRecheckClient.Identifier("first", search, FailedName, 1_100_000),
            WaitingRecheckClient.Identifier("twin", search, "Tolkien.The.Hobbit.(first).(epub)", 1_100_000)
        ];

        await harness.MakeEverythingDueAsync();
        await harness.RecheckAsync();

        CollectionAssert.AreEqual(new List<string> { "first" }, harness.Client.SubmittedCandidates);
    }

    [TestMethod]
    public async Task ACloseCallIsNeverGivenAnExtraAttemptAndTheReviewIsUnchanged()
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        var (harness, request) = await SpendTheBudgetAsync(fixture);
        await using var _ = harness;
        // An exact title with no author evidence is not a strong candidate.
        harness.Client.Script = _ =>
            [WaitingRecheckClient.Candidate("close", "The Hobbit (close) (epub)", 900_000, withAuthor: false)];

        await harness.MakeEverythingDueAsync();
        await harness.RecheckAsync();

        CollectionAssert.AreEqual(new List<string> { "first" }, harness.Client.SubmittedCandidates);
        var status = await harness.ReadAsync(async database => (await database.BookRequests.SingleAsync(item => item.Id == request.Id)).Status);
        Assert.AreEqual(RequestStatus.NeedsReview, status);
    }
}
