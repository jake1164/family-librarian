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
        await RetryLoopSupport.DrainWakeUpAsync(wakeUp);
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
        Assert.IsTrue(
            attempts.Any(attempt => attempt.Outcome == "Acquired"),
            "The copy that arrived must close its 'Submitted' row, or the activity page shows it in progress for ever.");

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

/// <summary>
/// A job the provider itself reports as <c>cancelled</c> is a failed attempt, in
/// its own database: it must wake the fulfillment worker and rule the candidate
/// out, not leave the format reverting to "Requested" with a "Submitted" attempt.
/// </summary>
[TestClass]
public sealed class ExternalProviderCancelledJobEndpointTests
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
    public async Task AProviderCancelledJobIsRecordedAsAFailureAndTheNextCandidateIsTried() =>
        await AssertFailureAdvancesToNextCandidateAsync(
            _fixture, new RetryLoopExternalProviderClient
            {
                FailingJobState = ProviderAcquisitionJobLifecycleState.Cancelled
            },
            "cancelled-job-external");

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ACancelledSubmissionAdvancesToTheNextCandidate(bool replayAfterLostResponse)
    {
        await using var ownFixture = await WebTestFixture.CreateAsync();
        await AssertFailureAdvancesToNextCandidateAsync(
            ownFixture, new RetryLoopExternalProviderClient
            {
                FailingJobState = ProviderAcquisitionJobLifecycleState.Cancelled,
                FailingSubmissionIsTerminal = true,
                LoseFirstSubmissionResponse = replayAfterLostResponse
            },
            "cancelled-submission-external");
    }

    [TestMethod]
    public async Task AProtocolBreakingStatusFailsTheJobInsteadOfBeingPolledForever()
    {
        await using var ownFixture = await WebTestFixture.CreateAsync();
        await AssertFailureAdvancesToNextCandidateAsync(
            ownFixture, new RetryLoopExternalProviderClient { FailingJobBreaksProtocol = true },
            "protocol-error-external");
    }

    private static async Task AssertFailureAdvancesToNextCandidateAsync(
        WebTestFixture? testFixture, object fakeClient, string providerId)
    {
        var client = (RetryLoopExternalProviderClient)fakeClient;
        var fixture = WebTestFixture.Require(testFixture);
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

        await RetryLoopSupport.RegisterProviderAsync(admin, providerId: providerId);

        var resolve = await admin.PostAsync("/api/v1/catalog/candidates/demo/the-hobbit/resolve", content: null);
        var work = await resolve.Content.ReadFromJsonAsync<CatalogWorkResponse>();
        Assert.IsNotNull(work);
        var created = await admin.PostAsJsonAsync(
            "/api/v1/requests/", new CreateBookRequestRequest(work.Id, ["Ebook"], null, false, false));
        var request = await created.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(request);

        await RetryLoopSupport.RunRecheckAsync(factory);
        Assert.AreEqual(RetryLoopExternalProviderClient.FailingReference, client.LastSubmittedCandidate);

        if (client.LoseFirstSubmissionResponse)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var job = await database.ProviderAcquisitionJobs.SingleAsync(candidate => candidate.RequestId == request.Id);
            Assert.IsNull(job.ProviderJobId, "A lost response must leave the durable submission available for replay.");
            job.Reschedule(DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow);
            await database.SaveChangesAsync();
        }

        var wakeUp = factory.Services.GetRequiredService<FamilyLibrarian.Web.Acquisition.AutomaticFulfillmentSignal>();
        await RetryLoopSupport.DrainWakeUpAsync(wakeUp);
        await RetryLoopSupport.RunPollingAsync(factory);

        Assert.IsTrue(
            await wakeUp.WaitAsync(TimeSpan.Zero, CancellationToken.None),
            "The failure must ask the fulfillment worker to run now.");

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var declined = await database.DeclinedRequestCandidates
                .Where(candidate => candidate.RequestId == request.Id)
                .ToListAsync();
            Assert.HasCount(1, declined, "The failed copy must be ruled out so it is not retried.");
            Assert.AreEqual(RetryLoopExternalProviderClient.FailingReference, declined[0].ProviderResultId);
            var failedJob = await database.ProviderAcquisitionJobs.SingleAsync(job => job.RequestId == request.Id);
            Assert.AreEqual(ProviderAcquisitionJobLifecycleState.Failed, failedJob.LifecycleState);
            Assert.IsNull(failedJob.NextPollAtUtc, "A failed job must never remain due for polling.");
            if (client.FailingJobState == ProviderAcquisitionJobLifecycleState.Cancelled)
                Assert.AreEqual("PROVIDER_CANCELLED", failedJob.ErrorCode);
            Assert.IsTrue(await database.ProviderAttempts.AnyAsync(attempt =>
                attempt.RequestId == request.Id && attempt.Outcome == ProviderAttemptOutcome.Retrying),
                "Advancing after a cancellation must record the retry attempt.");
        }

        if (client.LoseFirstSubmissionResponse)
        {
            Assert.HasCount(2, client.SubmittedRequestIds);
            Assert.AreEqual(client.SubmittedRequestIds[0], client.SubmittedRequestIds[1]);
            Assert.AreEqual(client.SubmittedIdempotencyKeys[0], client.SubmittedIdempotencyKeys[1]);
            Assert.AreEqual(0, client.StatusPollCount, "A replayed cancelled response must be handled without another status request.");
        }

        await RetryLoopSupport.RunRecheckAsync(factory);
        Assert.AreEqual(
            RetryLoopExternalProviderClient.SucceedingReference, client.LastSubmittedCandidate,
            "The next pass must advance to the next ranked candidate, not resubmit the failed one.");
    }
}

/// <summary>
/// A background job that completes but delivers the wrong book, in its own database. Identity verification holds the
/// file; the hold must rule the candidate out and advance exactly like a failed download. Before this was handled the
/// request was left with a "Submitted" attempt and no next check: never retried, never reviewed.
/// </summary>
[TestClass]
public sealed class ExternalProviderIdentityHoldRetryEndpointTests
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
    public async Task ACompletedJobWhoseFileIsHeldForIdentityAdvancesToTheNextCandidate()
    {
        var fixture = WebTestFixture.Require(_fixture);
        var client = new RetryLoopExternalProviderClient { FailingJobDeliversWrongBook = true };
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
        await RetryLoopSupport.RegisterProviderAsync(admin, providerId: "identity-hold-external");

        var resolve = await admin.PostAsync("/api/v1/catalog/candidates/demo/the-hobbit/resolve", content: null);
        var work = await resolve.Content.ReadFromJsonAsync<CatalogWorkResponse>();
        Assert.IsNotNull(work);
        var created = await admin.PostAsJsonAsync(
            "/api/v1/requests/", new CreateBookRequestRequest(work.Id, ["Ebook"], null, false, false));
        var request = await created.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(request);

        await RetryLoopSupport.RunRecheckAsync(factory);
        Assert.AreEqual(RetryLoopExternalProviderClient.FailingReference, client.LastSubmittedCandidate);

        var wakeUp = factory.Services.GetRequiredService<FamilyLibrarian.Web.Acquisition.AutomaticFulfillmentSignal>();
        await RetryLoopSupport.DrainWakeUpAsync(wakeUp);
        await RetryLoopSupport.RunPollingAsync(factory);
        Assert.IsTrue(
            await wakeUp.WaitAsync(TimeSpan.Zero, CancellationToken.None),
            "A held wrong book must ask the fulfillment worker to try the next copy now.");

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var held = await database.MediaAssets.SingleAsync(asset => asset.WorkId == work.Id);
            Assert.AreEqual(MediaAssetStorageState.Unmatched, held.StorageState, "The wrong book is held, not trusted.");
            Assert.IsNotNull(held.RuledOutAtUtc, "The queue must show the held copy as settled, not as a review to do.");

            var declined = await database.DeclinedRequestCandidates
                .Where(candidate => candidate.RequestId == request.Id)
                .ToListAsync();
            Assert.HasCount(1, declined, "The held copy must be ruled out so it is not fetched again.");
            Assert.AreEqual(RetryLoopExternalProviderClient.FailingReference, declined[0].ProviderResultId);

            var retrying = await database.ProviderAttempts.SingleAsync(attempt =>
                attempt.RequestId == request.Id && attempt.Outcome == ProviderAttemptOutcome.Retrying);
            Assert.Contains("Attempt 1 of 3", retrying.Summary);
            Assert.IsNotNull(retrying.NextEligibleCheckAtUtc, "Advancing must make the format due again at once.");

            var persisted = await database.BookRequests.SingleAsync(entity => entity.Id == request.Id);
            Assert.AreEqual(RequestStatus.PendingAcquisition, persisted.Status);
        }

        await RetryLoopSupport.RunRecheckAsync(factory);
        Assert.AreEqual(
            RetryLoopExternalProviderClient.SucceedingReference, client.LastSubmittedCandidate,
            "The next pass must fetch the next ranked copy, not wait forever on the held one.");

        // The held copy stays (releasable) while the request is open, and is deleted once it is settled.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var discards = scope.ServiceProvider.GetRequiredService<FamilyLibrarian.Application.Security.AssetDiscardService>();
            Assert.AreEqual(0, await discards.DiscardSettledHeldCopiesAsync(50, CancellationToken.None));

            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var persisted = await database.BookRequests.SingleAsync(entity => entity.Id == request.Id);
            persisted.TransitionTo(RequestStatus.Cancelled, null, "Test: the requester withdrew.", DateTimeOffset.UtcNow);
            await database.SaveChangesAsync();
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var discards = scope.ServiceProvider.GetRequiredService<FamilyLibrarian.Application.Security.AssetDiscardService>();
            Assert.AreEqual(1, await discards.DiscardSettledHeldCopiesAsync(50, CancellationToken.None));
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var held = await database.MediaAssets.SingleAsync(asset => asset.WorkId == work.Id);
            Assert.AreEqual(MediaAssetStorageState.Destroyed, held.StorageState);
        }
    }
}

/// <summary>
/// One download per format at a time, in its own database. On the lab a second source started the same ebook two
/// minutes after the first, while the first was still downloading, spending a metered download on a duplicate.
/// </summary>
[TestClass]
public sealed class ExternalProviderSingleDownloadPerFormatEndpointTests
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
    public async Task ASecondSourceDoesNotStartTheSameFormatWhileTheFirstIsStillDownloading()
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
        await RetryLoopSupport.RegisterProviderAsync(admin, providerId: "first-source");
        await RetryLoopSupport.RegisterProviderAsync(admin, providerId: "second-source");

        var resolve = await admin.PostAsync("/api/v1/catalog/candidates/demo/the-hobbit/resolve", content: null);
        var work = await resolve.Content.ReadFromJsonAsync<CatalogWorkResponse>();
        Assert.IsNotNull(work);
        var created = await admin.PostAsJsonAsync(
            "/api/v1/requests/", new CreateBookRequestRequest(work.Id, ["Ebook"], null, false, false));
        Assert.IsTrue(created.IsSuccessStatusCode);

        // The first pass submits one download, which is still running (the job is never polled here).
        await RetryLoopSupport.RunRecheckAsync(factory);
        Assert.HasCount(1, client.SubmittedCandidates);

        // The other source has never been asked, so it is due; it must still wait for the running download.
        await RetryLoopSupport.RunRecheckAsync(factory);
        Assert.HasCount(
            1, client.SubmittedCandidates,
            "A second source must not start a parallel download of the same format.");
    }
}

/// <summary>
/// A provider job that never finishes, in its own database. On the lab one sat "queued" for nineteen hours while the
/// provider retried an NZB its indexer would not hand over; nothing in Family Librarian ever gave up on it.
/// </summary>
[TestClass]
public sealed class ExternalProviderJobTimeLimitEndpointTests
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
    public async Task AJobQueuedPastItsTimeLimitIsCancelledFailedAndTheNextCopyIsTried()
    {
        var fixture = WebTestFixture.Require(_fixture);
        var client = new RetryLoopExternalProviderClient { FailingJobNeverFinishes = true };
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
        await RetryLoopSupport.RegisterProviderAsync(admin, providerId: "stuck-source");

        var resolve = await admin.PostAsync("/api/v1/catalog/candidates/demo/the-hobbit/resolve", content: null);
        var work = await resolve.Content.ReadFromJsonAsync<CatalogWorkResponse>();
        Assert.IsNotNull(work);
        var created = await admin.PostAsJsonAsync(
            "/api/v1/requests/", new CreateBookRequestRequest(work.Id, ["Ebook"], null, false, false));
        var request = await created.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(request);

        await RetryLoopSupport.RunRecheckAsync(factory);
        await RetryLoopSupport.RunPollingAsync(factory);

        // Within its time limit a slow job is simply still in progress.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var job = await database.ProviderAcquisitionJobs.SingleAsync(entity => entity.RequestId == request.Id);
            Assert.AreEqual(ProviderAcquisitionJobLifecycleState.Queued, job.LifecycleState);

            // Age it past the queued limit and make it due.
            await database.Database.ExecuteSqlInterpolatedAsync(
                $"update acquisition.provider_acquisition_jobs set created_at_utc = {DateTimeOffset.UtcNow.AddHours(-13)}, next_poll_at_utc = {DateTimeOffset.UtcNow.AddSeconds(-1)} where id = {job.Id}");
        }

        await RetryLoopSupport.RunPollingAsync(factory);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var job = await database.ProviderAcquisitionJobs.SingleAsync(entity => entity.RequestId == request.Id);
            Assert.AreEqual(ProviderAcquisitionJobLifecycleState.Failed, job.LifecycleState);
            Assert.AreEqual("PROVIDER_TIMEOUT", job.ErrorCode);
            Assert.HasCount(1, client.CancelledJobs, "The provider must be asked to stop the job it never finished.");
            CollectionAssert.Contains(
                client.DeletedJobs, client.CancelledJobs[0],
                "A failed job must be released at the provider, or its downloaded files stay on its disk.");
            var declined = await database.DeclinedRequestCandidates.SingleAsync(candidate => candidate.RequestId == request.Id);
            Assert.AreEqual(RetryLoopExternalProviderClient.FailingReference, declined.ProviderResultId);
        }

        await RetryLoopSupport.RunRecheckAsync(factory);
        Assert.AreEqual(RetryLoopExternalProviderClient.SucceedingReference, client.LastSubmittedCandidate);
    }
}

/// <summary>
/// Once a format is in the library, a download still running for it elsewhere is waste (and on a metered source,
/// spent quota). In its own database.
/// </summary>
[TestClass]
public sealed class ExternalProviderFulfilledFormatCancellationEndpointTests
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
    public async Task AFulfilledFormatCancelsAndReleasesEveryDownloadStillRunningForIt()
    {
        var fixture = WebTestFixture.Require(_fixture);
        var client = new RetryLoopExternalProviderClient { FailingJobNeverFinishes = true };
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
        await RetryLoopSupport.RegisterProviderAsync(admin, providerId: "still-downloading");

        var resolve = await admin.PostAsync("/api/v1/catalog/candidates/demo/the-hobbit/resolve", content: null);
        var work = await resolve.Content.ReadFromJsonAsync<CatalogWorkResponse>();
        Assert.IsNotNull(work);
        var created = await admin.PostAsJsonAsync(
            "/api/v1/requests/", new CreateBookRequestRequest(work.Id, ["Ebook"], null, false, false));
        var request = await created.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(request);
        await RetryLoopSupport.RunRecheckAsync(factory);

        Guid formatId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var job = await database.ProviderAcquisitionJobs.SingleAsync(entity => entity.RequestId == request.Id);
            Assert.AreNotEqual(ProviderAcquisitionJobLifecycleState.Cancelled, job.LifecycleState);
            formatId = job.RequestFormatId;
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<FulfilledFormatDownloadCancellation>()
                .CancelRemainingAsync(formatId, [], CancellationToken.None);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var job = await database.ProviderAcquisitionJobs.SingleAsync(entity => entity.RequestId == request.Id);
            Assert.AreEqual(ProviderAcquisitionJobLifecycleState.Cancelled, job.LifecycleState);
            Assert.AreEqual(FulfilledFormatDownloadCancellation.ErrorCode, job.ErrorCode);
            Assert.IsNull(job.NextPollAtUtc, "A cancelled job is never polled again.");
        }

        Assert.HasCount(1, client.CancelledJobs, "The provider must be told to stop downloading.");
    }
}

/// <summary>
/// A source that refuses or throttles a download is not a bad copy. On the lab an indexer's bot filter refused the
/// provider's client and the only English copy of a book was discarded for good; the copy must be kept and the source
/// asked again later. Own database: the recheck considers every provider against every pending request.
/// </summary>
[TestClass]
public sealed class ExternalProviderSourceRefusalEndpointTests
{
    // A database per case: a second request for the same work joins the first, so cases would share state.

    [TestMethod]
    public async Task ARefusedDownloadKeepsTheCopyAndTheSourceIsAskedAgainLater()
    {
        var client = new RetryLoopExternalProviderClient { FailingJobErrorCode = "AUTH_FAILED" };
        var before = DateTimeOffset.UtcNow;
        await using var ownFixture = await WebTestFixture.CreateAsync();
        var (factory, requestId, attempts) = await RunToFailureAsync(WebTestFixture.Require(ownFixture), client, "refusing-source");
        await using var _ = factory;

        Assert.IsFalse(
            attempts.Any(attempt => attempt.Outcome == "Retrying"),
            "A refused download is not a copy that was tried and ruled out.");
        var failed = attempts.Single(attempt => attempt.Outcome == "Failed");
        Assert.DoesNotContain("Nothing needs doing", failed.Summary);
        Assert.Contains("source", failed.Summary);
        Assert.IsNotNull(failed.NextEligibleCheckAtUtc);
        var wait = failed.NextEligibleCheckAtUtc.Value - before;
        Assert.IsTrue(wait > TimeSpan.FromHours(5.5) && wait < TimeSpan.FromHours(6.5), $"Expected about six hours, got {wait}.");

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.AreEqual(
                0, await database.DeclinedRequestCandidates.CountAsync(candidate => candidate.RequestId == requestId),
                "The copy was fine; the source refused. It must not be ruled out.");
            var persisted = await database.BookRequests.SingleAsync(entity => entity.Id == requestId);
            Assert.AreEqual(RequestStatus.PendingAcquisition, persisted.Status);

            // Time passes: the source is due again, and the same copy is the one tried.
            await database.ProviderAttempts.Where(attempt => attempt.RequestId == requestId)
                .ExecuteUpdateAsync(set => set.SetProperty(attempt => attempt.NextEligibleCheckAtUtc, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }

        await RetryLoopSupport.RunRecheckAsync(factory);
        Assert.AreEqual(
            2, client.SubmittedCandidates.Count(candidate => candidate == RetryLoopExternalProviderClient.FailingReference),
            "After the wait, the same candidate must be submitted again.");
    }

    [TestMethod]
    [DataRow(1800, 30)]
    [DataRow(10, 5)]
    [DataRow(null, 15)]
    public async Task AThrottledDownloadKeepsTheCopyAndWaitsAsLongAsTheSourceAsked(int? retryAfterSeconds, int expectedMinutes)
    {
        var client = new RetryLoopExternalProviderClient { FailingJobErrorCode = "RATE_LIMITED", FailingJobRetryAfterSeconds = retryAfterSeconds };
        var before = DateTimeOffset.UtcNow;
        await using var ownFixture = await WebTestFixture.CreateAsync();
        var (factory, requestId, attempts) = await RunToFailureAsync(WebTestFixture.Require(ownFixture), client, "throttling-source");
        await using var _ = factory;

        var paused = attempts.Single(attempt => attempt.Outcome == "Paused");
        Assert.IsNotNull(paused.NextEligibleCheckAtUtc);
        var wait = paused.NextEligibleCheckAtUtc.Value - before;
        Assert.IsTrue(
            wait > TimeSpan.FromMinutes(expectedMinutes - 1) && wait < TimeSpan.FromMinutes(expectedMinutes + 2),
            $"Expected about {expectedMinutes} minutes, got {wait}.");
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.AreEqual(0, await database.DeclinedRequestCandidates.CountAsync(candidate => candidate.RequestId == requestId));
    }

    private static async Task<(FamilyLibrarianAppFactory Factory, Guid RequestId, ProviderAttemptResponse[] Attempts)> RunToFailureAsync(
        WebTestFixture fixture, IExternalProviderClient client, string providerId)
    {
        var factory = new FamilyLibrarianAppFactory(
            fixture.ConnectionString,
            services =>
            {
                services.RemoveAll<IExternalProviderClient>();
                services.AddSingleton<IExternalProviderClient>(client);
            });
        using var admin = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await RetryLoopSupport.SignInAsync(admin, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword);
        admin.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(admin));
        await RetryLoopSupport.RegisterProviderAsync(admin, providerId: providerId);

        var resolve = await admin.PostAsync("/api/v1/catalog/candidates/demo/the-hobbit/resolve", content: null);
        var work = await resolve.Content.ReadFromJsonAsync<CatalogWorkResponse>();
        Assert.IsNotNull(work);
        var created = await admin.PostAsJsonAsync(
            "/api/v1/requests/", new CreateBookRequestRequest(work.Id, ["Ebook"], null, false, false));
        var request = await created.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(request);

        await RetryLoopSupport.RunRecheckAsync(factory);
        await RetryLoopSupport.RunPollingAsync(factory);

        var attempts = await admin.GetFromJsonAsync<ProviderAttemptResponse[]>(
            $"/api/v1/admin/requests/{request.Id}/provider-attempts");
        Assert.IsNotNull(attempts);
        return (factory, request.Id, attempts);
    }
}

/// <summary>
/// The activity list shows what is true now. On the lab, finished downloads still read "In progress" and a step the
/// loop had moved past still read "Trying next copy", because the append-only ledger was drawn row by row.
/// Own database: a second request for the same work would join the first.
/// </summary>
[TestClass]
public sealed class ExternalProviderActivityDisplayEndpointTests
{
    [TestMethod]
    public async Task FinishedAndMovedOnStepsAreShownAsSuchWhileTheStoredLedgerIsUntouched()
    {
        await using var ownFixture = await WebTestFixture.CreateAsync();
        var fixture = WebTestFixture.Require(ownFixture);
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
        admin.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(admin));
        await RetryLoopSupport.RegisterProviderAsync(admin, providerId: "activity-display-source");

        var resolve = await admin.PostAsync("/api/v1/catalog/candidates/demo/the-hobbit/resolve", content: null);
        var work = await resolve.Content.ReadFromJsonAsync<CatalogWorkResponse>();
        Assert.IsNotNull(work);
        var created = await admin.PostAsJsonAsync(
            "/api/v1/requests/", new CreateBookRequestRequest(work.Id, ["Ebook"], null, false, false));
        var request = await created.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(request);

        // The lab shape: the first copy fails and the loop moves on; the second copy arrives.
        await RetryLoopSupport.RunRecheckAsync(factory);
        await RetryLoopSupport.RunPollingAsync(factory);
        await RetryLoopSupport.RunRecheckAsync(factory);
        await RetryLoopSupport.RunPollingAsync(factory);

        var attempts = await admin.GetFromJsonAsync<ProviderAttemptResponse[]>(
            $"/api/v1/admin/requests/{request.Id}/provider-attempts");
        Assert.IsNotNull(attempts);

        var firstSubmitted = attempts.Single(attempt => attempt.Outcome == "Submitted" && attempt.Summary.Contains("(retail)", StringComparison.Ordinal));
        var secondSubmitted = attempts.Single(attempt => attempt.Outcome == "Submitted" && attempt.Summary.Contains("Attempt 2 of 3", StringComparison.Ordinal));
        var retrying = attempts.Single(attempt => attempt.Outcome == "Retrying");
        var acquired = attempts.Single(attempt => attempt.Outcome == "Acquired");

        Assert.AreEqual("Finished", firstSubmitted.DisplayOutcome, "Its download failed long ago.");
        Assert.AreEqual("Finished", secondSubmitted.DisplayOutcome, "Its download completed.");
        Assert.AreEqual("MovedOn", retrying.DisplayOutcome, "The next copy was tried.");
        Assert.AreEqual("Acquired", acquired.DisplayOutcome, "Everything else is shown as stored.");
        Assert.IsTrue(attempts.All(attempt => attempt.MediaType == "Ebook"), "Each row names its format.");

        // Nothing stored was rewritten, and the rows are linked to their downloads.
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await database.ProviderAttempts.Where(attempt => attempt.RequestId == request.Id).ToListAsync();
        Assert.HasCount(attempts.Length, stored);
        foreach (var row in attempts)
        {
            Assert.AreEqual(stored.Single(attempt => attempt.Id == row.Id).Outcome.ToString(), row.Outcome);
        }

        Assert.IsTrue(
            stored.Where(attempt => attempt.Outcome == ProviderAttemptOutcome.Submitted).All(attempt => attempt.ProviderAcquisitionJobId != null),
            "A row about a download must say which one.");
    }
}

/// <summary>
/// An administrator can ask every provider to let go of every download, so a reset leaves no downloaded, never-approved
/// files behind at the providers. Running downloads are cancelled; every job's staged files are deleted through the
/// provider's own protocol. Own database: a second request for the same work would join the first.
/// </summary>
[TestClass]
public sealed class ExternalProviderReleaseDownloadsEndpointTests
{
    [TestMethod]
    public async Task ReleasingDownloadsCancelsRunningOnesAndTellsTheProviderToDeleteEveryJobsFiles()
    {
        await using var ownFixture = await WebTestFixture.CreateAsync();
        var client = new RetryLoopExternalProviderClient { FailingJobNeverFinishes = true };
        await using var factory = new FamilyLibrarianAppFactory(
            WebTestFixture.Require(ownFixture).ConnectionString,
            services =>
            {
                services.RemoveAll<IExternalProviderClient>();
                services.AddSingleton<IExternalProviderClient>(client);
            });
        using var admin = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await RetryLoopSupport.SignInAsync(admin, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword);
        admin.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(admin));
        await RetryLoopSupport.RegisterProviderAsync(admin, providerId: "release-source");

        var resolve = await admin.PostAsync("/api/v1/catalog/candidates/demo/the-hobbit/resolve", content: null);
        var work = await resolve.Content.ReadFromJsonAsync<CatalogWorkResponse>();
        Assert.IsNotNull(work);
        var created = await admin.PostAsJsonAsync(
            "/api/v1/requests/", new CreateBookRequestRequest(work.Id, ["Ebook"], null, false, false));
        var request = await created.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(request);
        await RetryLoopSupport.RunRecheckAsync(factory);

        // A download is running at the provider and never finishes.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var job = await database.ProviderAcquisitionJobs.SingleAsync(entity => entity.RequestId == request.Id);
            Assert.IsNotNull(job.ProviderJobId);
            Assert.AreNotEqual(ProviderAcquisitionJobLifecycleState.Cancelled, job.LifecycleState);
        }

        var response = await admin.PostAsync("/api/v1/admin/external-providers/release-downloads", content: null);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ProviderJobReleaseResponse>();
        Assert.IsNotNull(result);
        Assert.AreEqual(new ProviderJobReleaseResponse(1, 0, 0), result);

        await using var verification = factory.Services.CreateAsyncScope();
        var db = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        var released = await db.ProviderAcquisitionJobs.SingleAsync(entity => entity.RequestId == request.Id);
        Assert.AreEqual(ProviderAcquisitionJobLifecycleState.Cancelled, released.LifecycleState);
        Assert.AreEqual(ProviderJobReleaseService.ErrorCode, released.ErrorCode);
        CollectionAssert.Contains(client.CancelledJobs, released.ProviderJobId);
        CollectionAssert.Contains(client.DeletedJobs, released.ProviderJobId, "The provider must be told to delete the job's files.");

        // Safe to repeat: the job is now finished here, so a second run only asks the provider to delete again.
        var again = await (await admin.PostAsync("/api/v1/admin/external-providers/release-downloads", content: null))
            .Content.ReadFromJsonAsync<ProviderJobReleaseResponse>();
        Assert.AreEqual(new ProviderJobReleaseResponse(0, 1, 0), again);
    }

    [TestMethod]
    public async Task ReleasingDownloadsRequiresAnAdministrator()
    {
        await using var ownFixture = await WebTestFixture.CreateAsync();
        await using var factory = new FamilyLibrarianAppFactory(WebTestFixture.Require(ownFixture).ConnectionString);
        using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await anonymous.PostAsync("/api/v1/admin/external-providers/release-downloads", content: null);

        Assert.IsFalse(response.IsSuccessStatusCode, "Releasing every download is an administrator action.");
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

    /// <summary>
    /// Creating a request legitimately asks the fulfillment worker to run, so that wake-up is already pending when the
    /// test reaches the failure it is about. Clear it so the assertion afterwards proves the failure raised its own.
    /// </summary>
    public static async Task DrainWakeUpAsync(FamilyLibrarian.Web.Acquisition.AutomaticFulfillmentSignal wakeUp)
    {
        while (await wakeUp.WaitAsync(TimeSpan.Zero, CancellationToken.None))
        {
        }
    }

    public static async Task RunPollingAsync(FamilyLibrarianAppFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var polling = scope.ServiceProvider.GetRequiredService<AcquisitionJobPollingService>();
        await polling.ProcessDueAsync(CancellationToken.None);
    }
}

/// <summary>
/// Reports two release-name-only candidates for The Hobbit -- the live an indexer
/// shape, with no <c>work</c> object at all. The retail-tagged one outranks the
/// other and always fails its job; the untagged one succeeds.
/// </summary>
file sealed class RetryLoopExternalProviderClient : IExternalProviderClient
{
    public const string FailingReference = "c_retail_fails";
    public const string SucceedingReference = "c_plain_succeeds";

    private readonly Dictionary<string, string> _jobCandidates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _idempotentJobs = new(StringComparer.Ordinal);
    private int _jobCounter;

    public const string TwinReference = "c_retail_twin";

    /// <summary>The terminal state the failing candidate's job reports: Failed (default) or Cancelled.</summary>
    public ProviderAcquisitionJobLifecycleState FailingJobState { get; init; } = ProviderAcquisitionJobLifecycleState.Failed;

    /// <summary>When set, the failing candidate's status poll breaks the protocol instead of reporting a state.</summary>
    public bool FailingJobBreaksProtocol { get; init; }

    /// <summary>The protocol error code the failing candidate's job reports.</summary>
    public string FailingJobErrorCode { get; init; } = "TRANSFER_FAILED";

    public int? FailingJobRetryAfterSeconds { get; init; }

    public bool FailingSubmissionIsTerminal { get; init; }

    /// <summary>When set, the failing candidate's job is reported queued forever, like a provider stuck retrying.</summary>
    public bool FailingJobNeverFinishes { get; init; }

    public List<string> CancelledJobs { get; } = [];

    public List<string> DeletedJobs { get; } = [];

    /// <summary>When set, the failing candidate's job completes but its file is a different book.</summary>
    public bool FailingJobDeliversWrongBook { get; init; }

    public bool LoseFirstSubmissionResponse { get; init; }

    public List<Guid> SubmittedRequestIds { get; } = [];

    public List<string> SubmittedIdempotencyKeys { get; } = [];

    public int StatusPollCount { get; private set; }

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
        SubmittedRequestIds.Add(request.RequestId);
        SubmittedIdempotencyKeys.Add(idempotencyKey);
        if (!_idempotentJobs.TryGetValue(idempotencyKey, out var jobId))
        {
            jobId = $"retry-job-{++_jobCounter}";
            _idempotentJobs[idempotencyKey] = jobId;
            _jobCandidates[jobId] = request.CandidateReference;
            if (LoseFirstSubmissionResponse && request.CandidateReference == FailingReference)
                throw new HttpRequestException("The provider accepted the job, but its response was lost.");
        }
        var state = FailingSubmissionIsTerminal && request.CandidateReference == FailingReference
            ? FailingJobState
            : FailingJobNeverFinishes && request.CandidateReference == FailingReference
                ? ProviderAcquisitionJobLifecycleState.Queued
                : ProviderAcquisitionJobLifecycleState.Running;
        return Task.FromResult(ExternalProviderAcquireSubmission.Accepted(
            jobId, state, phase: null, pollAfterSeconds: 0));
    }

    public Task<ExternalProviderJobStatus> GetAcquireStatusAsync(
        string baseUrl, string? apiKey, string jobId, CancellationToken cancellationToken)
    {
        StatusPollCount++;
        var isFailing = _jobCandidates.GetValueOrDefault(jobId) == FailingReference && !FailingJobDeliversWrongBook;
        if (_jobCandidates.GetValueOrDefault(jobId) == FailingReference && FailingJobNeverFinishes)
        {
            return Task.FromResult(new ExternalProviderJobStatus(
                jobId, ProviderAcquisitionJobLifecycleState.Queued, Phase: "resolving", Interaction: null, Progress: null,
                Error: null, PollAfterSeconds: 0));
        }

        if (isFailing && FailingJobBreaksProtocol)
        {
            throw new ExternalProviderProtocolException("The provider reported an unrecognized job state 'complete'.");
        }

        if (isFailing && FailingJobState == ProviderAcquisitionJobLifecycleState.Cancelled)
        {
            return Task.FromResult(new ExternalProviderJobStatus(
                jobId, ProviderAcquisitionJobLifecycleState.Cancelled, Phase: null, Interaction: null, Progress: null,
                Error: null, PollAfterSeconds: null));
        }

        return Task.FromResult(isFailing
            ? new ExternalProviderJobStatus(
                jobId, ProviderAcquisitionJobLifecycleState.Failed, Phase: null, Interaction: null, Progress: null,
                Error: new ProviderJobError(
                    FailingJobErrorCode, "The source could not deliver this copy.", Retryable: false,
                    FailingJobRetryAfterSeconds, null),
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
            new MemoryStream(FailingJobDeliversWrongBook && _jobCandidates.GetValueOrDefault(jobId) == FailingReference
                ? EpubTestFixture.BuildMinimalEpubBytes(title: "The Silmarillion")
                : EpubTestFixture.BuildMinimalEpubBytes()),
            "the-hobbit.epub"));

    public Task CancelAcquireAsync(
        string baseUrl, string? apiKey, string jobId, CancellationToken cancellationToken)
    {
        CancelledJobs.Add(jobId);
        return Task.CompletedTask;
    }

    public Task DeleteAcquireAsync(
        string baseUrl, string? apiKey, string jobId, CancellationToken cancellationToken)
    {
        DeletedJobs.Add(jobId);
        return Task.CompletedTask;
    }
}
