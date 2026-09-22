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

// Covers the fix for a gap found while scoping the lab's mock external-
// provider suite: ExternalProviderRecheckService used to route every found
// candidate to NeedsReview, even a single candidate whose title/author
// corroborates a Work FL already knows an ISBN-13 for. That contradicted the
// wire protocol's own documented trust rule
// (docs/04-external-provider-http-protocol.md §7) and the manual acquire
// flow, which already trusted an Identifier-basis match.
//
// Each scenario below is its own [TestClass] with its own WebTestFixture
// (own Postgres), rather than three [TestMethod]s sharing one class-level
// fixture: ExternalProviderRecheckService.ProcessDueAsync considers every
// enabled/Daily-or-Weekly-scheduled provider in the whole database against
// every pending request in the whole database, and stops at the first
// provider that reports anything for a given request/format. Confirmed by
// running this as three methods in one class first: a provider registered
// by an earlier method stayed enabled and got a turn at a later method's
// own (distinct) Work, claiming it before that method's own provider ever
// ran. Only a fully separate database per scenario removes that
// cross-scenario race.

/// <summary>
/// A single ISBN-corroborated ("Identifier"-basis) candidate is trusted the
/// same way the manual acquire flow already trusts one -- fetched, scanned,
/// and evaluated automatically, with no librarian action.
/// </summary>
[TestClass]
public sealed class ExternalProviderAutomaticAcquisitionEndpointTests
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
    public async Task AScheduledExternalLookupWithASingleIdentifierMatchAcquiresAutomatically()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = new FamilyLibrarianAppFactory(
            fixture.ConnectionString,
            services =>
            {
                services.RemoveAll<IExternalProviderClient>();
                services.AddSingleton<IExternalProviderClient>(new FakeHobbitExternalProviderClient());
            });

        using var admin = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await SignInAsync(admin, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword);
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(admin);
        admin.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);

        var create = await admin.PostAsJsonAsync(
            "/api/v1/admin/external-providers/",
            new CreateExternalProviderRequest("identifier-match-external", "Identifier Match External", "http://fake-external.test"));
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

        // The raw resolved demo Work is used directly (not
        // WebTestFixture.CopyWorkForTestAsync's per-test copy) because that
        // copy deliberately carries no Editions -- only the freshly resolved
        // Work has the demo catalog's declared ISBN-13 attached
        // (CatalogWorkResolver.AddEdition), which is what lets this
        // scenario's candidate reach Identifier basis at all.
        var resolve = await admin.PostAsync("/api/v1/catalog/candidates/demo/the-hobbit/resolve", content: null);
        var work = await resolve.Content.ReadFromJsonAsync<CatalogWorkResponse>();
        Assert.IsNotNull(work);
        var created = await admin.PostAsJsonAsync(
            "/api/v1/requests/", new CreateBookRequestRequest(work.Id, ["Ebook"], null, false, false));
        var request = await created.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(request);
        var format = request.Formats.Single(candidate => candidate.MediaType == "Ebook");

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var rechecks = scope.ServiceProvider.GetRequiredService<ExternalProviderRecheckService>();
            Assert.IsTrue(await rechecks.ProcessDueAsync(CancellationToken.None) >= 1);
        }

        var attempts = await admin.GetFromJsonAsync<ProviderAttemptResponse[]>(
            $"/api/v1/admin/requests/{request.Id}/provider-attempts");
        Assert.IsNotNull(attempts);
        var attempt = attempts.Single();
        Assert.AreEqual("identifier-match-external", attempt.ProviderId);
        // Protocol v2: a single ISBN-corroborated candidate is still
        // acquired automatically, not left for review -- but "automatic"
        // now means "a durable job was submitted", not "bytes landed
        // synchronously". AcquisitionJobPollingService drives it the rest
        // of the way below.
        Assert.AreEqual("Submitted", attempt.Outcome, "A single ISBN-corroborated candidate must be acquired automatically, not left for review.");

        await using (var pollScope = factory.Services.CreateAsyncScope())
        {
            var polling = pollScope.ServiceProvider.GetRequiredService<AcquisitionJobPollingService>();
            Assert.AreEqual(1, await polling.ProcessDueAsync(CancellationToken.None));
        }

        await using var verificationScope = factory.Services.CreateAsyncScope();
        var database = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var persisted = await database.BookRequests.SingleAsync(bookRequest => bookRequest.Id == request.Id);
        Assert.AreNotEqual(RequestStatus.NeedsReview, persisted.Status, "An automatically-acquired match must never be routed to librarian review.");

        var asset = await database.MediaAssets.SingleAsync(mediaAsset => mediaAsset.AssociatedRequestFormatId == format.FormatId);
        Assert.AreEqual(MediaAssetStorageState.Trusted, asset.StorageState);
        Assert.AreEqual(1, await database.SecurityEvaluations.CountAsync(evaluation => evaluation.AssetId == asset.Id));

        var job = await database.AcquisitionJobs.SingleAsync(acquisitionJob => acquisitionJob.RequestId == request.Id);
        Assert.AreEqual("identifier-match-external", job.ProviderId);
    }

    private static Task SignInAsync(HttpClient client, string email, string password) =>
        ExternalProviderAutomaticFixtureSupport.SignInAsync(client, email, password);
}

/// <summary>
/// Equivalent source records are not a requester choice. The scheduled path
/// selects the policy-preferred safe format once and never submits a fallback
/// download automatically.
/// </summary>
[TestClass]
public sealed class ExternalProviderStrictEquivalentAutomaticAcquisitionEndpointTests
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
    public async Task AScheduledExternalLookupWithStrictEquivalentCandidatesSubmitsOnlyThePreferredSafeFormat()
    {
        var fixture = WebTestFixture.Require(_fixture);
        var providerClient = new StrictEquivalentExternalProviderClient();
        await using var factory = new FamilyLibrarianAppFactory(
            fixture.ConnectionString,
            services =>
            {
                services.RemoveAll<IExternalProviderClient>();
                services.AddSingleton<IExternalProviderClient>(providerClient);
            });

        using var admin = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await ExternalProviderAutomaticFixtureSupport.SignInAsync(
            admin, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword);
        admin.DefaultRequestHeaders.Add(
            AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(admin));

        var create = await admin.PostAsJsonAsync(
            "/api/v1/admin/external-providers/",
            new CreateExternalProviderRequest("strict-equivalent-external", "Strict Equivalent External", "http://fake-external.test"));
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
            "/api/v1/requests/", new CreateBookRequestRequest(work.Id, ["Ebook"], null, false, false));
        var request = await created.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(request);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var rechecks = scope.ServiceProvider.GetRequiredService<ExternalProviderRecheckService>();
            Assert.IsTrue(await rechecks.ProcessDueAsync(CancellationToken.None) >= 1);
        }

        Assert.HasCount(1, providerClient.SubmittedCandidateReferences);
        Assert.AreEqual("strict-epub", providerClient.SubmittedCandidateReferences.Single());

        await using var verificationScope = factory.Services.CreateAsyncScope();
        var database = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var persisted = await database.BookRequests.SingleAsync(bookRequest => bookRequest.Id == request.Id);
        Assert.AreNotEqual(RequestStatus.NeedsReview, persisted.Status);
        var job = await database.ProviderAcquisitionJobs.SingleAsync(acquisitionJob => acquisitionJob.RequestId == request.Id);
        Assert.AreEqual("strict-epub", job.CandidateReference);
    }
}

/// <summary>
/// The AutoAcquireEnabled toggle (plan §B) is a separate, explicit opt-in --
/// a provider with a Daily/Weekly recheck schedule but the toggle left at its
/// default (off) must still route even a single ISBN-corroborated candidate
/// to librarian review, never fetch it unattended.
/// </summary>
[TestClass]
public sealed class ExternalProviderAutoAcquireDisabledByDefaultEndpointTests
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
    public async Task AScheduledExternalLookupWithAnIdentifierMatchStillRequiresReviewWhenAutoAcquireIsNotEnabled()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = new FamilyLibrarianAppFactory(
            fixture.ConnectionString,
            services =>
            {
                services.RemoveAll<IExternalProviderClient>();
                services.AddSingleton<IExternalProviderClient>(new FakeHobbitExternalProviderClient());
            });

        using var admin = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await ExternalProviderAutomaticFixtureSupport.SignInAsync(admin, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword);
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(admin);
        admin.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);

        var create = await admin.PostAsJsonAsync(
            "/api/v1/admin/external-providers/",
            new CreateExternalProviderRequest("identifier-match-external", "Identifier Match External", "http://fake-external.test"));
        var provider = await create.Content.ReadFromJsonAsync<ExternalProviderResponse>();
        Assert.IsNotNull(provider);
        Assert.IsFalse(provider.AutoAcquireEnabled, "A newly registered provider must default to automatic-acquire disabled.");
        (await admin.PutAsJsonAsync(
            $"/api/v1/admin/external-providers/{provider.Id}/enabled", new SetExternalProviderEnabledRequest(true)))
            .EnsureSuccessStatusCode();
        (await admin.PutAsJsonAsync(
            $"/api/v1/admin/external-providers/{provider.Id}/recheck-schedule",
            new SetExternalProviderRecheckScheduleRequest("Daily")))
            .EnsureSuccessStatusCode();

        var resolve = await admin.PostAsync("/api/v1/catalog/candidates/demo/the-hobbit/resolve", content: null);
        var work = await resolve.Content.ReadFromJsonAsync<CatalogWorkResponse>();
        Assert.IsNotNull(work);
        var created = await admin.PostAsJsonAsync(
            "/api/v1/requests/", new CreateBookRequestRequest(work.Id, ["Ebook"], null, false, false));
        var request = await created.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(request);
        var format = request.Formats.Single(candidate => candidate.MediaType == "Ebook");

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var rechecks = scope.ServiceProvider.GetRequiredService<ExternalProviderRecheckService>();
            Assert.IsTrue(await rechecks.ProcessDueAsync(CancellationToken.None) >= 1);
        }

        var attempts = await admin.GetFromJsonAsync<ProviderAttemptResponse[]>(
            $"/api/v1/admin/requests/{request.Id}/provider-attempts");
        Assert.IsNotNull(attempts);
        var attempt = attempts.Single();
        Assert.AreEqual("identifier-match-external", attempt.ProviderId);
        Assert.AreEqual("CandidatesFound", attempt.Outcome,
            "An ISBN-corroborated candidate must still require review when AutoAcquireEnabled is off.");

        await using var verificationScope = factory.Services.CreateAsyncScope();
        var database = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var persisted = await database.BookRequests.SingleAsync(bookRequest => bookRequest.Id == request.Id);
        Assert.AreEqual(RequestStatus.NeedsReview, persisted.Status);
        Assert.AreEqual(RequestReviewCategory.PreferenceAmbiguity, persisted.ReviewCategory);
        var reviewCandidate = await database.RequestReviewCandidates.SingleAsync(
            candidate => candidate.RequestId == request.Id);
        Assert.AreEqual("The Hobbit", reviewCandidate.Title);
        Assert.AreEqual("J. R. R. Tolkien", reviewCandidate.Author);
        Assert.AreEqual(0, await database.MediaAssets.CountAsync(
            asset => asset.AssociatedRequestFormatId == format.FormatId));
    }
}

/// <summary>
/// A candidate that never corroborates title/author -- regardless of
/// whether FL already knows an ISBN for the Work -- must still require
/// librarian review, unchanged from before the automatic-acquire fix.
/// </summary>
[TestClass]
public sealed class ExternalProviderAutomaticReviewFallbackEndpointTests
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
    public async Task AScheduledExternalLookupWithoutAnIdentifierMatchStillRequiresReview()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = new FamilyLibrarianAppFactory(
            fixture.ConnectionString,
            services =>
            {
                services.RemoveAll<IExternalProviderClient>();
                services.AddSingleton<IExternalProviderClient>(new WrongTitleExternalProviderClient());
            });

        using var admin = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await ExternalProviderAutomaticFixtureSupport.SignInAsync(admin, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword);
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(admin);
        admin.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);

        var create = await admin.PostAsJsonAsync(
            "/api/v1/admin/external-providers/",
            new CreateExternalProviderRequest("scheduled-external", "Scheduled External", "http://fake-external.test"));
        var provider = await create.Content.ReadFromJsonAsync<ExternalProviderResponse>();
        Assert.IsNotNull(provider);
        (await admin.PutAsJsonAsync(
            $"/api/v1/admin/external-providers/{provider.Id}/enabled", new SetExternalProviderEnabledRequest(true)))
            .EnsureSuccessStatusCode();
        (await admin.PutAsJsonAsync(
            $"/api/v1/admin/external-providers/{provider.Id}/recheck-schedule",
            new SetExternalProviderRecheckScheduleRequest("Daily")))
            .EnsureSuccessStatusCode();

        var resolve = await admin.PostAsync("/api/v1/catalog/candidates/demo/a-wrinkle-in-time/resolve", content: null);
        var work = await resolve.Content.ReadFromJsonAsync<CatalogWorkResponse>();
        Assert.IsNotNull(work);
        var created = await admin.PostAsJsonAsync(
            "/api/v1/requests/", new CreateBookRequestRequest(work.Id, ["Ebook"], null, false, false));
        var request = await created.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(request);
        var format = request.Formats.Single(candidate => candidate.MediaType == "Ebook");

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var rechecks = scope.ServiceProvider.GetRequiredService<ExternalProviderRecheckService>();
            Assert.IsTrue(await rechecks.ProcessDueAsync(CancellationToken.None) >= 1);
        }

        var attempts = await admin.GetFromJsonAsync<ProviderAttemptResponse[]>(
            $"/api/v1/admin/requests/{request.Id}/provider-attempts");
        Assert.IsNotNull(attempts);
        var attempt = attempts.Single();
        Assert.AreEqual("scheduled-external", attempt.ProviderId);
        Assert.AreEqual("CandidatesFound", attempt.Outcome);
        Assert.IsNull(attempt.NextEligibleCheckAtUtc);

        await using var verificationScope = factory.Services.CreateAsyncScope();
        var database = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var persisted = await database.BookRequests.SingleAsync(bookRequest => bookRequest.Id == request.Id);
        Assert.AreEqual(RequestStatus.NeedsReview, persisted.Status);
        Assert.AreEqual(0, await database.MediaAssets.CountAsync(
            asset => asset.AssociatedRequestFormatId == format.FormatId));

        var requesterView = await admin.GetFromJsonAsync<BookRequestListResponse>("/api/v1/me/requests");
        Assert.IsNotNull(requesterView);
        var review = requesterView.Active.Single(item => item.Id == request.Id).NeedsReview;
        Assert.IsNotNull(review);
        Assert.AreEqual(
            "Possible copies were found, but their titles could not be confirmed as the requested work. A librarian must verify the source before acquisition.",
            review.Reason);
    }
}

/// <summary>
/// A high-confidence (Identifier-basis) match that fails during the fetch
/// itself must be recorded as <c>Failed</c> and sent to review, not
/// silently dropped or retried forever within the same pass.
/// </summary>
[TestClass]
public sealed class ExternalProviderAutomaticAcquisitionFailureEndpointTests
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
    public async Task AScheduledExternalLookupWhoseIdentifierMatchFailsToAcquireIsRecordedAndSentToReview()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = new FamilyLibrarianAppFactory(
            fixture.ConnectionString,
            services =>
            {
                services.RemoveAll<IExternalProviderClient>();
                services.AddSingleton<IExternalProviderClient>(new FailingFetchExternalProviderClient());
            });

        using var admin = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await ExternalProviderAutomaticFixtureSupport.SignInAsync(admin, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword);
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(admin);
        admin.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);

        var create = await admin.PostAsJsonAsync(
            "/api/v1/admin/external-providers/",
            new CreateExternalProviderRequest("failing-fetch-external", "Failing Fetch External", "http://fake-external.test"));
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

        var resolve = await admin.PostAsync("/api/v1/catalog/candidates/demo/project-hail-mary/resolve", content: null);
        var work = await resolve.Content.ReadFromJsonAsync<CatalogWorkResponse>();
        Assert.IsNotNull(work);
        var created = await admin.PostAsJsonAsync(
            "/api/v1/requests/", new CreateBookRequestRequest(work.Id, ["Ebook"], null, false, false));
        var request = await created.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(request);
        var format = request.Formats.Single(candidate => candidate.MediaType == "Ebook");

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var rechecks = scope.ServiceProvider.GetRequiredService<ExternalProviderRecheckService>();
            Assert.IsTrue(await rechecks.ProcessDueAsync(CancellationToken.None) >= 1);
        }

        var attempts = await admin.GetFromJsonAsync<ProviderAttemptResponse[]>(
            $"/api/v1/admin/requests/{request.Id}/provider-attempts");
        Assert.IsNotNull(attempts);
        var attempt = attempts.Single();
        Assert.AreEqual("failing-fetch-external", attempt.ProviderId);
        Assert.AreEqual("Failed", attempt.Outcome);

        await using var verificationScope = factory.Services.CreateAsyncScope();
        var database = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var persisted = await database.BookRequests.SingleAsync(bookRequest => bookRequest.Id == request.Id);
        Assert.AreEqual(RequestStatus.NeedsReview, persisted.Status, "A high-confidence match that fails to fetch must still reach a librarian, not silently retry forever.");
        Assert.AreEqual(0, await database.MediaAssets.CountAsync(
            asset => asset.AssociatedRequestFormatId == format.FormatId));
    }
}

/// <summary>
/// Exercises the requester-safe review flow from one external search through
/// the real host and PostgreSQL projection. The provider returns four records,
/// but only two distinct requester-visible editions; no acquire/download call
/// is permitted while the default auto-acquire setting is off.
/// </summary>
[TestClass]
public sealed class ExternalProviderReviewCandidatePresentationEndpointTests
{
    private static readonly string[] ExpectedPreferenceCandidateDetails =
    [
        "EPUB · Published 2014 · Example Press · 1.5 MB",
        "EPUB · Published 2016 · Archive House · 2 MB"
    ];

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
    public async Task AnExternalSearchCollapsesDuplicateReviewCandidatesAndKeepsNeutralEditionFactsPrivateToTheRequester()
    {
        var fixture = WebTestFixture.Require(_fixture);
        var providerClient = new PresentationExternalProviderClient();
        await using var factory = new FamilyLibrarianAppFactory(
            fixture.ConnectionString,
            services =>
            {
                services.RemoveAll<IExternalProviderClient>();
                services.AddSingleton<IExternalProviderClient>(providerClient);
            });

        using var requester = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await ExternalProviderAutomaticFixtureSupport.SignInAsync(
            requester, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword);
        requester.DefaultRequestHeaders.Add(
            AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(requester));

        var createdProvider = await requester.PostAsJsonAsync(
            "/api/v1/admin/external-providers/",
            new CreateExternalProviderRequest("presentation-external", "Presentation External", "http://fake-external.test"));
        var provider = await createdProvider.Content.ReadFromJsonAsync<ExternalProviderResponse>();
        Assert.IsNotNull(provider);
        (await requester.PutAsJsonAsync(
            $"/api/v1/admin/external-providers/{provider.Id}/enabled", new SetExternalProviderEnabledRequest(true)))
            .EnsureSuccessStatusCode();
        (await requester.PutAsJsonAsync(
            $"/api/v1/admin/external-providers/{provider.Id}/recheck-schedule",
            new SetExternalProviderRecheckScheduleRequest("Daily"))).EnsureSuccessStatusCode();

        var resolve = await requester.PostAsync("/api/v1/catalog/candidates/demo/the-hobbit/resolve", content: null);
        var work = await resolve.Content.ReadFromJsonAsync<CatalogWorkResponse>();
        Assert.IsNotNull(work);
        var createdRequest = await requester.PostAsJsonAsync(
            "/api/v1/requests/", new CreateBookRequestRequest(work.Id, ["Ebook"], null, false, false));
        var request = await createdRequest.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(request);
        var searchCallsBeforeRecheck = providerClient.SearchCalls;

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var rechecks = scope.ServiceProvider.GetRequiredService<ExternalProviderRecheckService>();
            Assert.IsTrue(await rechecks.ProcessDueAsync(CancellationToken.None) >= 1);
        }
        Assert.AreEqual(
            searchCallsBeforeRecheck + 1,
            providerClient.SearchCalls,
            "The scheduled review flow must use one provider search for every candidate record.");

        using var response = await requester.GetAsync("/api/v1/me/requests");
        response.EnsureSuccessStatusCode();
        var rawResponse = await response.Content.ReadAsStringAsync();
        var listed = await response.Content.ReadFromJsonAsync<BookRequestListResponse>();
        Assert.IsNotNull(listed);
        var review = listed.Active.Single(item => item.Id == request.Id);
        Assert.IsNotNull(review.NeedsReview);
        Assert.AreEqual("PreferenceAmbiguity", review.NeedsReview.Category);
        Assert.HasCount(2, review.NeedsReview.Candidates);
        CollectionAssert.AreEquivalent(
            ExpectedPreferenceCandidateDetails,
            review.NeedsReview.Candidates.Select(candidate => candidate.Details).ToArray());
        Assert.IsFalse(rawResponse.Contains("presentation-external", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(rawResponse.Contains("opaque-duplicate", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(rawResponse.Contains("source.example.test", StringComparison.OrdinalIgnoreCase),
            "A provider inspection URL is administrator-only and must never leave the family endpoint.");
        Assert.AreEqual(
            searchCallsBeforeRecheck + 1,
            providerClient.SearchCalls,
            "Reading the review must reuse persisted evidence, not search the provider again.");
        Assert.AreEqual(0, providerClient.AcquireCalls, "Review enrichment must not download a candidate.");

        await using var verificationScope = factory.Services.CreateAsyncScope();
        var database = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var persisted = await database.RequestReviewCandidates
            .Where(candidate => candidate.RequestId == request.Id)
            .OrderBy(candidate => candidate.DisplayOrder)
            .ToArrayAsync();
        Assert.HasCount(2, persisted);
        Assert.AreEqual("opaque-duplicate-a", persisted[0].ProviderResultId,
            "The first duplicate remains an opaque server-side acquisition handle.");

        var adminView = await requester.GetFromJsonAsync<AdminBookRequestResponse>(
            $"/api/v1/admin/requests/{request.Id}");
        Assert.IsNotNull(adminView);
        Assert.IsNotNull(adminView.ReviewCandidates);
        Assert.HasCount(2, adminView.ReviewCandidates);
        Assert.AreEqual("presentation-external", adminView.ReviewCandidates[0].ProviderId);
        Assert.AreEqual("https://source.example.test/md5/opaque-duplicate-a", adminView.ReviewCandidates[0].InspectionUri);
    }
}

file static class ExternalProviderAutomaticFixtureSupport
{
    public static async Task SignInAsync(HttpClient client, string email, string password)
    {
        var response = await client.PostAsJsonAsync(
            "/api/auth/login", new FamilyLibrarian.Contracts.Authentication.LoginRequest { Email = email, Password = password });
        Assert.AreEqual(System.Net.HttpStatusCode.NoContent, response.StatusCode);
    }
}

/// <summary>Always finds "the-hobbit"-matching searches and fetches a real, minimal, valid EPUB.</summary>
file sealed class FakeHobbitExternalProviderClient : IExternalProviderClient
{
    public Task<ExternalProviderManifest> GetManifestAsync(
        string baseUrl, string? apiKey, EgressRoute route, CancellationToken cancellationToken) =>
        Task.FromResult(new ExternalProviderManifest(
            ["2"], "2", null, "identifier-match-external", "Identifier Match External", "1.0.0",
            new ProviderCapabilities(["ebook"], ["search", "acquire"], []), null, null, null, "NORMAL"));

    public Task<ExternalProviderHealth> GetHealthAsync(
        string baseUrl, string? apiKey, EgressRoute route, CancellationToken cancellationToken) =>
        Task.FromResult(new ExternalProviderHealth(
            ProviderHealthStatus.Healthy, ProviderOperationalStatus.Available, ProviderOperationalStatus.Available));

    public Task<IReadOnlyList<ExternalProviderCandidate>> SearchAsync(
        string baseUrl, string? apiKey, ExternalProviderSearchRequest request, EgressRoute route,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ExternalProviderCandidate> candidates = request.MediaType == RequestMediaType.Ebook
            ?
            [
                new ExternalProviderCandidate(
                    "fake-hobbit-1",
                    new ExternalProviderWorkEvidence(
                        "The Hobbit", null, [new BookAuthor("J. R. R. Tolkien", "author")], [], []),
                    new ExternalProviderEditionEvidence(
                        "en", null, null, request.Edition?.Identifiers ?? []),
                    new ExternalProviderReleaseEvidence(
                        null, "epub", null, false, 1, false, null, null, [], null,
                        ExternalProviderDrmStatus.None))
            ]
            : [];
        return Task.FromResult(candidates);
    }

    public Task<ExternalProviderArtifact> AcquireAsync(
        string baseUrl, string? apiKey, string candidateReference, RequestMediaType mediaType, EgressRoute route,
        CancellationToken cancellationToken) =>
        Task.FromResult(new ExternalProviderArtifact(
            new MemoryStream(EpubTestFixture.BuildMinimalEpubBytes()),
            "the-hobbit.epub"));

    public Task<ExternalProviderAcquireSubmission> SubmitAcquireAsync(
        string baseUrl, string? apiKey, ExternalAcquireRequest request, string idempotencyKey, EgressRoute route,
        CancellationToken cancellationToken) =>
        Task.FromResult(ExternalProviderAcquireSubmission.Accepted(
            "fake-job-1", ProviderAcquisitionJobLifecycleState.Completed, phase: null, pollAfterSeconds: 0));

    public Task<ExternalProviderJobStatus> GetAcquireStatusAsync(
        string baseUrl, string? apiKey, string jobId, EgressRoute route, CancellationToken cancellationToken) =>
        Task.FromResult(new ExternalProviderJobStatus(
            jobId, ProviderAcquisitionJobLifecycleState.Completed, Phase: null, Interaction: null, Progress: null,
            Error: null, PollAfterSeconds: null));

    public Task<IReadOnlyList<ExternalProviderOutput>> ListOutputsAsync(
        string baseUrl, string? apiKey, string jobId, EgressRoute route, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ExternalProviderOutput>>(
        [
            new ExternalProviderOutput(
                "primary", ProviderOutputKind.File, "primary", "the-hobbit.epub", "application/epub+zip",
                null, null, null, null, null)
        ]);

    public Task<ExternalProviderArtifact> GetOutputAsync(
        string baseUrl, string? apiKey, string jobId, string outputId, EgressRoute route,
        CancellationToken cancellationToken) =>
        Task.FromResult(new ExternalProviderArtifact(
            new MemoryStream(EpubTestFixture.BuildMinimalEpubBytes()), "the-hobbit.epub"));

    public Task CancelAcquireAsync(
        string baseUrl, string? apiKey, string jobId, EgressRoute route, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task DeleteAcquireAsync(
        string baseUrl, string? apiKey, string jobId, EgressRoute route, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

/// <summary>
/// Always returns a plausible-looking but unrelated title/author, no matter
/// what it was actually searched for -- never an Identifier-basis match
/// against any real Work, so it always requires review.
/// </summary>
file sealed class WrongTitleExternalProviderClient : IExternalProviderClient
{
    public Task<ExternalProviderManifest> GetManifestAsync(
        string baseUrl, string? apiKey, EgressRoute route, CancellationToken cancellationToken) =>
        Task.FromResult(new ExternalProviderManifest(
            ["2"], "2", null, "scheduled-external", "Scheduled External", "1.0.0",
            new ProviderCapabilities(["ebook"], ["search", "acquire"], []), null, null, null, "NORMAL"));

    public Task<ExternalProviderHealth> GetHealthAsync(
        string baseUrl, string? apiKey, EgressRoute route, CancellationToken cancellationToken) =>
        Task.FromResult(new ExternalProviderHealth(
            ProviderHealthStatus.Healthy, ProviderOperationalStatus.Available, ProviderOperationalStatus.Available));

    public Task<IReadOnlyList<ExternalProviderCandidate>> SearchAsync(
        string baseUrl, string? apiKey, ExternalProviderSearchRequest request, EgressRoute route,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ExternalProviderCandidate> candidates = request.MediaType == RequestMediaType.Ebook
            ? [ExternalProviderCandidate.FromSimple("wrong-title-1", "Dim Sum of Fears", "Someone Unrelated", "epub", null)]
            : [];
        return Task.FromResult(candidates);
    }

    public Task<ExternalProviderArtifact> AcquireAsync(
        string baseUrl, string? apiKey, string candidateReference, RequestMediaType mediaType, EgressRoute route,
        CancellationToken cancellationToken) =>
        Task.FromResult(new ExternalProviderArtifact(
            new MemoryStream(EpubTestFixture.BuildMinimalEpubBytes()),
            "wrong-title.epub"));

    public Task<ExternalProviderAcquireSubmission> SubmitAcquireAsync(
        string baseUrl, string? apiKey, ExternalAcquireRequest request, string idempotencyKey, EgressRoute route,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<ExternalProviderJobStatus> GetAcquireStatusAsync(
        string baseUrl, string? apiKey, string jobId, EgressRoute route, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<ExternalProviderOutput>> ListOutputsAsync(
        string baseUrl, string? apiKey, string jobId, EgressRoute route, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<ExternalProviderArtifact> GetOutputAsync(
        string baseUrl, string? apiKey, string jobId, string outputId, EgressRoute route,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task CancelAcquireAsync(
        string baseUrl, string? apiKey, string jobId, EgressRoute route, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task DeleteAcquireAsync(
        string baseUrl, string? apiKey, string jobId, EgressRoute route, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

/// <summary>
/// Returns a single, genuinely title/author-corroborating candidate for
/// "Project Hail Mary" (an Identifier-basis match), but fails the fetch step
/// itself.
/// </summary>
file sealed class FailingFetchExternalProviderClient : IExternalProviderClient
{
    public Task<ExternalProviderManifest> GetManifestAsync(
        string baseUrl, string? apiKey, EgressRoute route, CancellationToken cancellationToken) =>
        Task.FromResult(new ExternalProviderManifest(
            ["2"], "2", null, "failing-fetch-external", "Failing Fetch External", "1.0.0",
            new ProviderCapabilities(["ebook"], ["search", "acquire"], []), null, null, null, "NORMAL"));

    public Task<ExternalProviderHealth> GetHealthAsync(
        string baseUrl, string? apiKey, EgressRoute route, CancellationToken cancellationToken) =>
        Task.FromResult(new ExternalProviderHealth(
            ProviderHealthStatus.Healthy, ProviderOperationalStatus.Available, ProviderOperationalStatus.Available));

    public Task<IReadOnlyList<ExternalProviderCandidate>> SearchAsync(
        string baseUrl, string? apiKey, ExternalProviderSearchRequest request, EgressRoute route,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ExternalProviderCandidate> candidates = request.MediaType == RequestMediaType.Ebook
            ?
            [
                new ExternalProviderCandidate(
                    "hail-mary-1",
                    new ExternalProviderWorkEvidence(
                        "Project Hail Mary", null, [new BookAuthor("Andy Weir", "author")], [], []),
                    new ExternalProviderEditionEvidence(
                        "en", null, null, request.Edition?.Identifiers ?? []),
                    new ExternalProviderReleaseEvidence(
                        null, "epub", null, false, 1, false, null, null, [], null,
                        ExternalProviderDrmStatus.None))
            ]
            : [];
        return Task.FromResult(candidates);
    }

    public Task<ExternalProviderArtifact> AcquireAsync(
        string baseUrl, string? apiKey, string candidateReference, RequestMediaType mediaType, EgressRoute route,
        CancellationToken cancellationToken) =>
        throw new HttpRequestException("Simulated upstream failure while fetching the artifact.");

    public Task<ExternalProviderAcquireSubmission> SubmitAcquireAsync(
        string baseUrl, string? apiKey, ExternalAcquireRequest request, string idempotencyKey, EgressRoute route,
        CancellationToken cancellationToken) =>
        throw new HttpRequestException("Simulated upstream failure while starting the acquisition.");

    public Task<ExternalProviderJobStatus> GetAcquireStatusAsync(
        string baseUrl, string? apiKey, string jobId, EgressRoute route, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<ExternalProviderOutput>> ListOutputsAsync(
        string baseUrl, string? apiKey, string jobId, EgressRoute route, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<ExternalProviderArtifact> GetOutputAsync(
        string baseUrl, string? apiKey, string jobId, string outputId, EgressRoute route,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task CancelAcquireAsync(
        string baseUrl, string? apiKey, string jobId, EgressRoute route, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task DeleteAcquireAsync(
        string baseUrl, string? apiKey, string jobId, EgressRoute route, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

file sealed class PresentationExternalProviderClient : IExternalProviderClient
{
    public int SearchCalls { get; private set; }

    public int AcquireCalls { get; private set; }

    public Task<ExternalProviderManifest> GetManifestAsync(
        string baseUrl, string? apiKey, EgressRoute route, CancellationToken cancellationToken) =>
        Task.FromResult(new ExternalProviderManifest(
            ["2"], "2", null, "presentation-external", "Presentation External", "1.0.0",
            new ProviderCapabilities(["ebook"], ["search", "acquire"], []), null, null, null, "NORMAL"));

    public Task<ExternalProviderHealth> GetHealthAsync(
        string baseUrl, string? apiKey, EgressRoute route, CancellationToken cancellationToken) =>
        Task.FromResult(new ExternalProviderHealth(
            ProviderHealthStatus.Healthy, ProviderOperationalStatus.Available, ProviderOperationalStatus.Available));

    public Task<IReadOnlyList<ExternalProviderCandidate>> SearchAsync(
        string baseUrl, string? apiKey, ExternalProviderSearchRequest request, EgressRoute route,
        CancellationToken cancellationToken)
    {
        SearchCalls++;
        if (request.MediaType != RequestMediaType.Ebook)
        {
            return Task.FromResult<IReadOnlyList<ExternalProviderCandidate>>([]);
        }

        var identifiers = request.Edition?.Identifiers ?? [];
        var work = new ExternalProviderWorkEvidence(
            "The Hobbit", null, [new BookAuthor("J. R. R. Tolkien", "author")], [], identifiers);
        IReadOnlyList<ExternalProviderCandidate> candidates =
        [
            new ExternalProviderCandidate(
                "opaque-duplicate-a", work,
                new ExternalProviderEditionEvidence("en", 2014, "Example Press", identifiers),
                new ExternalProviderReleaseEvidence(null, "epub", 1_572_864, false, 1, false, null, null, [], null,
                    ExternalProviderDrmStatus.None),
                InspectionUri: new Uri("https://source.example.test/md5/opaque-duplicate-a")),
            new ExternalProviderCandidate(
                "opaque-duplicate-b", work,
                new ExternalProviderEditionEvidence("en", 2014, "Example Press", identifiers),
                new ExternalProviderReleaseEvidence(null, "epub", 1_572_864, false, 1, false, null, null, [], null,
                    ExternalProviderDrmStatus.None)),
            new ExternalProviderCandidate(
                "opaque-edition-c", work,
                new ExternalProviderEditionEvidence("en", 2016, "Archive House", identifiers),
                new ExternalProviderReleaseEvidence(null, "epub", 2_097_152, false, 1, false, null, null, [], null,
                    ExternalProviderDrmStatus.None),
                InspectionUri: new Uri("https://source.example.test/md5/opaque-edition-c")),
            new ExternalProviderCandidate(
                "opaque-duplicate-d", work,
                new ExternalProviderEditionEvidence("en", 2014, "Example Press", identifiers),
                new ExternalProviderReleaseEvidence(null, "epub", 1_572_864, false, 1, false, null, null, [], null,
                    ExternalProviderDrmStatus.None))
        ];
        return Task.FromResult(candidates);
    }

    public Task<ExternalProviderArtifact> AcquireAsync(
        string baseUrl, string? apiKey, string candidateReference, RequestMediaType mediaType, EgressRoute route,
        CancellationToken cancellationToken)
    {
        AcquireCalls++;
        throw new InvalidOperationException("Review presentation must not acquire a file.");
    }

    public Task<ExternalProviderAcquireSubmission> SubmitAcquireAsync(
        string baseUrl, string? apiKey, ExternalAcquireRequest request, string idempotencyKey, EgressRoute route,
        CancellationToken cancellationToken)
    {
        AcquireCalls++;
        throw new InvalidOperationException("Review presentation must not acquire a file.");
    }

    public Task<ExternalProviderJobStatus> GetAcquireStatusAsync(
        string baseUrl, string? apiKey, string jobId, EgressRoute route, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<ExternalProviderOutput>> ListOutputsAsync(
        string baseUrl, string? apiKey, string jobId, EgressRoute route, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<ExternalProviderArtifact> GetOutputAsync(
        string baseUrl, string? apiKey, string jobId, string outputId, EgressRoute route,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task CancelAcquireAsync(
        string baseUrl, string? apiKey, string jobId, EgressRoute route, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task DeleteAcquireAsync(
        string baseUrl, string? apiKey, string jobId, EgressRoute route, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

file sealed class StrictEquivalentExternalProviderClient : IExternalProviderClient
{
    public List<string> SubmittedCandidateReferences { get; } = [];

    public Task<ExternalProviderManifest> GetManifestAsync(
        string baseUrl, string? apiKey, EgressRoute route, CancellationToken cancellationToken) =>
        Task.FromResult(new ExternalProviderManifest(
            ["2"], "2", null, "strict-equivalent-external", "Strict Equivalent External", "1.0.0",
            new ProviderCapabilities(["ebook"], ["search", "acquire"], []), null, null, null, "NORMAL"));

    public Task<ExternalProviderHealth> GetHealthAsync(
        string baseUrl, string? apiKey, EgressRoute route, CancellationToken cancellationToken) =>
        Task.FromResult(new ExternalProviderHealth(
            ProviderHealthStatus.Healthy, ProviderOperationalStatus.Available, ProviderOperationalStatus.Available));

    public Task<IReadOnlyList<ExternalProviderCandidate>> SearchAsync(
        string baseUrl, string? apiKey, ExternalProviderSearchRequest request, EgressRoute route,
        CancellationToken cancellationToken)
    {
        if (request.MediaType != RequestMediaType.Ebook)
        {
            return Task.FromResult<IReadOnlyList<ExternalProviderCandidate>>([]);
        }

        var work = new ExternalProviderWorkEvidence(
            "The Hobbit", null, [new BookAuthor("J. R. R. Tolkien", "author")], [], []);
        IReadOnlyList<ExternalProviderCandidate> candidates =
        [
            new ExternalProviderCandidate(
                "strict-mobi", work,
                new ExternalProviderEditionEvidence("en", null, null, []),
                new ExternalProviderReleaseEvidence(null, "mobi", 500_000, false, 1, false, null, null, [], null,
                    ExternalProviderDrmStatus.None)),
            new ExternalProviderCandidate(
                "strict-epub", work,
                new ExternalProviderEditionEvidence("en", null, null, []),
                new ExternalProviderReleaseEvidence(null, "epub", 500_000, false, 1, false, null, null, [], null,
                    ExternalProviderDrmStatus.None))
        ];
        return Task.FromResult(candidates);
    }

    public Task<ExternalProviderArtifact> AcquireAsync(
        string baseUrl, string? apiKey, string candidateReference, RequestMediaType mediaType, EgressRoute route,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The scheduled path must submit one durable provider job.");

    public Task<ExternalProviderAcquireSubmission> SubmitAcquireAsync(
        string baseUrl, string? apiKey, ExternalAcquireRequest request, string idempotencyKey, EgressRoute route,
        CancellationToken cancellationToken)
    {
        SubmittedCandidateReferences.Add(request.CandidateReference);
        return Task.FromResult(ExternalProviderAcquireSubmission.Accepted(
            "strict-equivalent-job", ProviderAcquisitionJobLifecycleState.Queued, phase: null, pollAfterSeconds: null));
    }

    public Task<ExternalProviderJobStatus> GetAcquireStatusAsync(
        string baseUrl, string? apiKey, string jobId, EgressRoute route, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<ExternalProviderOutput>> ListOutputsAsync(
        string baseUrl, string? apiKey, string jobId, EgressRoute route, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<ExternalProviderArtifact> GetOutputAsync(
        string baseUrl, string? apiKey, string jobId, string outputId, EgressRoute route,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task CancelAcquireAsync(
        string baseUrl, string? apiKey, string jobId, EgressRoute route, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task DeleteAcquireAsync(
        string baseUrl, string? apiKey, string jobId, EgressRoute route, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}
