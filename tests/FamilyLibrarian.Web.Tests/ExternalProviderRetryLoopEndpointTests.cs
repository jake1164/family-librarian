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

// Own database per class, for the same reason the sibling automatic-acquisition
// suite documents: ExternalProviderRecheckService considers every enabled,
// scheduled provider against every pending request in the whole database, so a
// provider registered by another class can claim this class's request.

/// <summary>
/// PROVIDER-7's retry loop: when an automatically acquired copy fails after
/// download, the request advances to the next ranked candidate instead of
/// stopping.
/// </summary>
[TestClass]
public sealed class ExternalProviderRetryLoopEndpointTests
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
    public async Task AFailedAutomaticCopyAdvancesToTheNextRankedCandidateInsteadOfStopping()
    {
        var fixture = WebTestFixture.Require(_fixture);
        var client = new RetryLoopExternalProviderClient();
        await using var factory = new FamilyLibrarianAppFactory(
            fixture.ConnectionString,
            services =>
            {
                services.RemoveAll<IExternalProviderClient>();
                services.AddSingleton<IExternalProviderClient>(client);
            });

        using var admin = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await RetryLoopSupport.SignInAsync(admin, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword);
        admin.DefaultRequestHeaders.Add(
            AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(admin));

        var provider = await RetryLoopSupport.RegisterProviderAsync(admin);

        var resolve = await admin.PostAsync("/api/v1/catalog/candidates/demo/the-hobbit/resolve", content: null);
        var work = await resolve.Content.ReadFromJsonAsync<CatalogWorkResponse>();
        Assert.IsNotNull(work);
        var created = await admin.PostAsJsonAsync(
            "/api/v1/requests/", new CreateBookRequestRequest(work.Id, ["Ebook"], null, false, false));
        var request = await created.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(request);

        // Pass 1: the ranker prefers the retail-tagged release, so that is the
        // candidate submitted first.
        await RetryLoopSupport.RunRecheckAsync(factory);
        Assert.AreEqual(
            RetryLoopExternalProviderClient.FailingReference, client.LastSubmittedCandidate,
            "The retail-tagged release outranks the untagged one and must be tried first.");

        // Its job fails after submit.
        var wakeUp = factory.Services.GetRequiredService<FamilyLibrarian.Web.Acquisition.AutomaticFulfillmentSignal>();
        Assert.IsFalse(
            await wakeUp.WaitAsync(TimeSpan.Zero, CancellationToken.None),
            "Nothing has failed yet, so nothing should have asked for a pass.");
        await RetryLoopSupport.RunPollingAsync(factory);

        // The failure must wake the fulfillment worker, or the next candidate
        // waits for its two-minute sweep -- the lag this signal exists to remove.
        Assert.IsTrue(
            await wakeUp.WaitAsync(TimeSpan.Zero, CancellationToken.None),
            "Ruling out a failed copy must ask the worker to run now.");

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var persisted = await database.BookRequests.SingleAsync(entity => entity.Id == request.Id);
            Assert.AreEqual(
                RequestStatus.PendingAcquisition, persisted.Status,
                "One failed copy must not stop the request while the attempt budget has room.");

            var declined = await database.DeclinedRequestCandidates
                .Where(candidate => candidate.RequestId == request.Id)
                .ToListAsync();
            Assert.HasCount(1, declined);
            Assert.AreEqual(RetryLoopExternalProviderClient.FailingReference, declined[0].ProviderResultId);
            Assert.AreEqual(DeclinedCandidateReason.AutomaticVerificationFailed, declined[0].Reason);
        }

        // Pass 2: the failed candidate is ruled out, so the next ranked one is
        // tried -- the whole point of the loop.
        await RetryLoopSupport.RunRecheckAsync(factory);
        Assert.AreEqual(
            RetryLoopExternalProviderClient.SucceedingReference, client.LastSubmittedCandidate,
            "The second pass must advance to the next ranked candidate, not retry the failed one.");

        await RetryLoopSupport.RunPollingAsync(factory);

        CollectionAssert.DoesNotContain(
            client.SubmittedCandidates, RetryLoopExternalProviderClient.TwinReference,
            "The same release posted under another reference must not be fetched after it failed.");
        CollectionAssert.AreEqual(
            new[] { RetryLoopExternalProviderClient.FailingReference, RetryLoopExternalProviderClient.SucceedingReference },
            client.SubmittedCandidates);

        var attempts = await admin.GetFromJsonAsync<ProviderAttemptResponse[]>(
            $"/api/v1/admin/requests/{request.Id}/provider-attempts");
        Assert.IsNotNull(attempts);

        // A step the loop is handling is "Retrying", not "Failed": nothing is
        // wrong that a person must fix, and the ledger must not say otherwise.
        var retrying = attempts.Single(attempt => attempt.Outcome == "Retrying");
        Assert.Contains("Attempt 1 of 3", retrying.Summary);
        Assert.Contains("Nothing needs doing", retrying.Summary);
        Assert.IsFalse(
            attempts.Any(attempt => attempt.Outcome == "Failed"),
            "One bad copy followed by a good one is not a failure anywhere in the ledger.");

        // The row for a fetch names the release, so the history reads as a
        // sequence of specific copies rather than identical "submitted" lines.
        Assert.IsTrue(
            attempts.Any(attempt => attempt.Outcome == "Submitted" &&
                attempt.Summary.Contains("(retail) (epub)", StringComparison.Ordinal)),
            "The submitted row must identify which release was fetched.");
        Assert.IsTrue(
            attempts.Any(attempt => attempt.Outcome == "Submitted" &&
                attempt.Summary.Contains("Attempt 2 of 3", StringComparison.Ordinal)),
            "The second fetch must say it is attempt 2 of 3.");
    }
}

/// <summary>
/// The metered-source case, in its own database: an administrator who knows a
/// source has a limited download allowance sets the attempt limit to 1, and
/// one spent download is then the whole budget.
/// </summary>
[TestClass]
public sealed class ExternalProviderMeteredRetryLimitEndpointTests
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
    public async Task AMeteredProviderLimitedToOneAttemptStopsAfterASingleFailedCopy()
    {
        var fixture = WebTestFixture.Require(_fixture);
        var client = new RetryLoopExternalProviderClient();
        await using var factory = new FamilyLibrarianAppFactory(
            fixture.ConnectionString,
            services =>
            {
                services.RemoveAll<IExternalProviderClient>();
                services.AddSingleton<IExternalProviderClient>(client);
            });

        using var admin = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await RetryLoopSupport.SignInAsync(admin, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword);
        admin.DefaultRequestHeaders.Add(
            AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(admin));

        var provider = await RetryLoopSupport.RegisterProviderAsync(
            admin, attemptLimit: 1, providerId: "metered-retry-external");
        Assert.AreEqual(1, provider.AutomaticAttemptLimit);

        var resolve = await admin.PostAsync("/api/v1/catalog/candidates/demo/the-hobbit/resolve", content: null);
        var work = await resolve.Content.ReadFromJsonAsync<CatalogWorkResponse>();
        Assert.IsNotNull(work);
        var created = await admin.PostAsJsonAsync(
            "/api/v1/requests/", new CreateBookRequestRequest(work.Id, ["Ebook"], null, false, false));
        var request = await created.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(request);

        await RetryLoopSupport.RunRecheckAsync(factory);
        await RetryLoopSupport.RunPollingAsync(factory);

        // One download spent is the whole budget for a metered source.
        await using var verificationScope = factory.Services.CreateAsyncScope();
        var meteredDatabase = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var meteredRequest = await meteredDatabase.BookRequests.SingleAsync(entity => entity.Id == request.Id);
        Assert.AreEqual(
            RequestStatus.NeedsReview, meteredRequest.Status,
            "A provider limited to one attempt must stop rather than spend a second download.");
    }
}

file static class RetryLoopSupport
{
    public static async Task SignInAsync(HttpClient client, string email, string password)
    {
        var response = await client.PostAsJsonAsync(
            "/api/auth/login", new FamilyLibrarian.Contracts.Authentication.LoginRequest { Email = email, Password = password });
        Assert.AreEqual(System.Net.HttpStatusCode.NoContent, response.StatusCode);
    }

    public static async Task<ExternalProviderResponse> RegisterProviderAsync(
        HttpClient admin, int? attemptLimit = null, string providerId = "retry-loop-external")
    {
        var create = await admin.PostAsJsonAsync(
            "/api/v1/admin/external-providers/",
            new CreateExternalProviderRequest(providerId, "Retry Loop External", "http://fake-retry.test"));
        var provider = await create.Content.ReadFromJsonAsync<ExternalProviderResponse>();
        Assert.IsNotNull(provider);
        (await admin.PutAsJsonAsync(
            $"/api/v1/admin/external-providers/{provider.Id}/enabled", new SetExternalProviderEnabledRequest(true)))
            .EnsureSuccessStatusCode();
        (await admin.PutAsJsonAsync(
            $"/api/v1/admin/external-providers/{provider.Id}/recheck-schedule",
            new SetExternalProviderRecheckScheduleRequest("Daily")))
            .EnsureSuccessStatusCode();
        (await admin.PutAsJsonAsync(
            $"/api/v1/admin/external-providers/{provider.Id}/auto-acquire",
            new SetExternalProviderAutoAcquireEnabledRequest(true)))
            .EnsureSuccessStatusCode();

        if (attemptLimit is not { } limit)
        {
            return provider;
        }

        var updated = await admin.PutAsJsonAsync(
            $"/api/v1/admin/external-providers/{provider.Id}/automatic-attempt-limit",
            new SetExternalProviderAutomaticAttemptLimitRequest(limit));
        updated.EnsureSuccessStatusCode();
        var result = await updated.Content.ReadFromJsonAsync<ExternalProviderResponse>();
        Assert.IsNotNull(result);
        return result;
    }

    public static async Task RunRecheckAsync(FamilyLibrarianAppFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var rechecks = scope.ServiceProvider.GetRequiredService<ExternalProviderRecheckService>();
        await rechecks.ProcessDueAsync(CancellationToken.None);
    }

    public static async Task RunPollingAsync(FamilyLibrarianAppFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var polling = scope.ServiceProvider.GetRequiredService<AcquisitionJobPollingService>();
        await polling.ProcessDueAsync(CancellationToken.None);
    }
}

/// <summary>
/// Reports two release-name-only candidates for The Hobbit -- the live Prowlarr
/// shape, with no <c>work</c> object at all. The retail-tagged one outranks the
/// other and always fails its job; the untagged one succeeds.
/// </summary>
file sealed class RetryLoopExternalProviderClient : IExternalProviderClient
{
    public const string FailingReference = "c_retail_fails";
    public const string SucceedingReference = "c_plain_succeeds";

    private readonly Dictionary<string, string> _jobCandidates = new(StringComparer.Ordinal);
    private int _jobCounter;

    public const string TwinReference = "c_retail_twin";

    public string? LastSubmittedCandidate { get; private set; }

    public List<string> SubmittedCandidates { get; } = [];

    public Task<ExternalProviderManifest> GetManifestAsync(
        string baseUrl, string? apiKey, CancellationToken cancellationToken) =>
        Task.FromResult(new ExternalProviderManifest(
            ["2"], "2", null, "retry-loop-external", "Retry Loop External", "1.0.0",
            new ProviderCapabilities(["ebook"], ["search", "acquire"], []), null, null, null));

    public Task<ExternalProviderHealth> GetHealthAsync(
        string baseUrl, string? apiKey, CancellationToken cancellationToken) =>
        Task.FromResult(new ExternalProviderHealth(
            ProviderHealthStatus.Healthy, ProviderOperationalStatus.Available, ProviderOperationalStatus.Available));

    public Task<IReadOnlyList<ExternalProviderCandidate>> SearchAsync(
        string baseUrl, string? apiKey, ExternalProviderSearchRequest request, CancellationToken cancellationToken)
    {
        IReadOnlyList<ExternalProviderCandidate> candidates = request.MediaType == RequestMediaType.Ebook
            ?
            [
                ReleaseOnly(SucceedingReference, "The Hobbit by J. R. R. Tolkien EPUB", retail: false, size: 1_000_000),
                ReleaseOnly(FailingReference, "J. R. R. Tolkien - The Hobbit (retail) (epub)", retail: true, size: 1_200_000),
                // The very same release posted again under another reference:
                // same name (differently punctuated), same bytes. Trying it
                // after the first failed would spend an attempt on a copy that
                // is certain to fail the same way.
                ReleaseOnly(TwinReference, "J.R.R.Tolkien-The.Hobbit.(retail).(epub)", retail: true, size: 1_200_000)
            ]
            : [];
        return Task.FromResult(candidates);
    }

    private static ExternalProviderCandidate ReleaseOnly(string reference, string releaseName, bool retail, long size) =>
        new(
            reference,
            ExternalProviderWorkEvidence.Empty,
            Edition: null,
            Release: new ExternalProviderReleaseEvidence(
                releaseName, "epub", size, IsCollection: false, PartCount: 1, IsSample: false,
                IsAbridged: null, IsUnabridged: null, QualityTags: retail ? ["retail"] : [], AgeDays: 100,
                DrmStatus: ExternalProviderDrmStatus.None));

    public Task<ExternalProviderArtifact> AcquireAsync(
        string baseUrl, string? apiKey, string candidateReference, RequestMediaType mediaType,
        CancellationToken cancellationToken) =>
        Task.FromResult(new ExternalProviderArtifact(
            new MemoryStream(EpubTestFixture.BuildMinimalEpubBytes()), "the-hobbit.epub"));

    public Task<ExternalProviderAcquireSubmission> SubmitAcquireAsync(
        string baseUrl, string? apiKey, ExternalAcquireRequest request, string idempotencyKey,
        CancellationToken cancellationToken)
    {
        LastSubmittedCandidate = request.CandidateReference;
        SubmittedCandidates.Add(request.CandidateReference);
        var jobId = $"retry-job-{++_jobCounter}";
        _jobCandidates[jobId] = request.CandidateReference;
        return Task.FromResult(ExternalProviderAcquireSubmission.Accepted(
            jobId, ProviderAcquisitionJobLifecycleState.Running, phase: null, pollAfterSeconds: 0));
    }

    public Task<ExternalProviderJobStatus> GetAcquireStatusAsync(
        string baseUrl, string? apiKey, string jobId, CancellationToken cancellationToken)
    {
        var isFailing = _jobCandidates.GetValueOrDefault(jobId) == FailingReference;
        return Task.FromResult(isFailing
            ? new ExternalProviderJobStatus(
                jobId, ProviderAcquisitionJobLifecycleState.Failed, Phase: null, Interaction: null, Progress: null,
                Error: new ProviderJobError(
                    "TRANSFER_FAILED", "The source could not deliver this copy.", Retryable: false, null, null),
                PollAfterSeconds: null)
            : new ExternalProviderJobStatus(
                jobId, ProviderAcquisitionJobLifecycleState.Completed, Phase: null, Interaction: null, Progress: null,
                Error: null, PollAfterSeconds: null));
    }

    public Task<IReadOnlyList<ExternalProviderOutput>> ListOutputsAsync(
        string baseUrl, string? apiKey, string jobId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ExternalProviderOutput>>(
        [
            new ExternalProviderOutput(
                "primary", ProviderOutputKind.File, "primary", "the-hobbit.epub", "application/epub+zip",
                null, null, null, null, null)
        ]);

    public Task<ExternalProviderArtifact> GetOutputAsync(
        string baseUrl, string? apiKey, string jobId, string outputId, CancellationToken cancellationToken) =>
        Task.FromResult(new ExternalProviderArtifact(
            new MemoryStream(EpubTestFixture.BuildMinimalEpubBytes()), "the-hobbit.epub"));

    public Task CancelAcquireAsync(
        string baseUrl, string? apiKey, string jobId, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task DeleteAcquireAsync(
        string baseUrl, string? apiKey, string jobId, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
