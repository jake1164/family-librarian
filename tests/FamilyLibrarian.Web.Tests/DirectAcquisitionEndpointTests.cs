using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Contracts.Acquisition;
using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Contracts.Requests;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Notifications;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// Exercises the bundled free-ebook acquisition endpoint through the real
/// host, with a fake <see cref="IDirectAcquisitionProvider"/> standing in for
/// Gutendex so the test never depends on a real network call.
/// </summary>
[TestClass]
public sealed class DirectAcquisitionEndpointTests
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
    public async Task AnAdminCanAcquireAMatchedFreeEbook()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = CreateFactory(fixture, new FakeProvider(matches: true));
        using var admin = await CreateTokenClientAsync(factory);
        var (requestId, formatId) = await CreateEbookRequestAsync(admin);

        var response = await admin.PostAsync(
            $"/api/v1/admin/requests/{requestId}/formats/{formatId}/direct-acquisitions/gutendex/1234",
            content: null);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ManualImportResultResponse>();
        Assert.IsNotNull(result);

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var asset = await database.MediaAssets.SingleAsync(a => a.Id == result.MediaAssetId);
        Assert.AreEqual(MediaAssetStorageState.Trusted, asset.StorageState);
        Assert.AreEqual(formatId, asset.AssociatedRequestFormatId);

        Assert.AreEqual(1, await database.SecurityEvaluations.CountAsync(
            evaluation => evaluation.AssetId == asset.Id));

        var job = await database.AcquisitionJobs.SingleAsync(j => j.Id == result.AcquisitionJobId);
        Assert.AreEqual("gutendex", job.ProviderId);

        // A manual "get free copy" click is a provider lookup too — it must
        // land in the same ledger as an automatic one, or the request
        // detail's "Provider activity" goes stale after a manual retry.
        Assert.AreEqual(1, await database.ProviderAttempts.CountAsync(attempt =>
            attempt.RequestFormatId == formatId &&
            attempt.ProviderId == "gutendex" &&
            attempt.Outcome == ProviderAttemptOutcome.Acquired));
    }

    [TestMethod]
    public async Task AStaleProviderResultIdIsRejected()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = CreateFactory(fixture, new FakeProvider(matches: false));
        using var admin = await CreateTokenClientAsync(factory);
        var (requestId, formatId) = await CreateEbookRequestAsync(admin);

        var response = await admin.PostAsync(
            $"/api/v1/admin/requests/{requestId}/formats/{formatId}/direct-acquisitions/gutendex/nonexistent",
            content: null);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.AreEqual(1, await database.ProviderAttempts.CountAsync(attempt =>
            attempt.RequestFormatId == formatId &&
            attempt.ProviderId == "gutendex" &&
            attempt.Outcome == ProviderAttemptOutcome.Failed));
    }

    [TestMethod]
    public async Task AManualFetchThatExceedsTheTrackLimitReturnsACleanErrorInsteadOfCrashing()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = CreateFactory(fixture, new FakeProvider(matches: true, throwsOnFetch: true));
        using var admin = await CreateTokenClientAsync(factory);
        var (requestId, formatId) = await CreateEbookRequestAsync(admin);

        var response = await admin.PostAsync(
            $"/api/v1/admin/requests/{requestId}/formats/{formatId}/direct-acquisitions/gutendex/1234",
            content: null);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.AreEqual(1, await database.ProviderAttempts.CountAsync(attempt =>
            attempt.RequestFormatId == formatId &&
            attempt.ProviderId == "gutendex" &&
            attempt.Outcome == ProviderAttemptOutcome.Failed));
    }

    [TestMethod]
    public async Task AnUnknownProviderIdIsRejected()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = CreateFactory(fixture, new FakeProvider(matches: true));
        using var admin = await CreateTokenClientAsync(factory);
        var (requestId, formatId) = await CreateEbookRequestAsync(admin);

        var response = await admin.PostAsync(
            $"/api/v1/admin/requests/{requestId}/formats/{formatId}/direct-acquisitions/not-a-real-provider/1234",
            content: null);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task ANonAdminCannotAcquire()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = CreateFactory(fixture, new FakeProvider(matches: true));
        using var admin = await CreateTokenClientAsync(factory);
        var (requestId, formatId) = await CreateEbookRequestAsync(admin);

        using var user = await CreateTokenClientAsync(factory, isAdmin: false);
        var response = await user.PostAsync(
            $"/api/v1/admin/requests/{requestId}/formats/{formatId}/direct-acquisitions/gutendex/1234",
            content: null);

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [TestMethod]
    public async Task AHighConfidenceAutomaticMatchIsFetchedScannedAndTrustedWithoutAnAdminAction()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = CreateFactory(fixture, new FakeProvider(matches: true));
        using var requester = await CreateTokenClientAsync(factory, isAdmin: false);
        var (requestId, formatId) = await CreateEbookRequestAsync(requester);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var fulfillment = scope.ServiceProvider.GetRequiredService<AutomaticRequestFulfillmentService>();
            Assert.AreEqual(1, await fulfillment.ProcessPendingAsync(CancellationToken.None));
        }

        await using var verificationScope = factory.Services.CreateAsyncScope();
        var database = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var asset = await database.MediaAssets.SingleAsync(asset => asset.AssociatedRequestFormatId == formatId);
        Assert.AreEqual(MediaAssetStorageState.Trusted, asset.StorageState);
        Assert.AreEqual(1, await database.SecurityEvaluations.CountAsync(
            evaluation => evaluation.AssetId == asset.Id));
        Assert.IsNotNull(await database.BookRequests.FindAsync(requestId));
    }

    [TestMethod]
    public async Task ANoMatchLeavesTheRequestInTheAutomaticQueueInsteadOfNeedingALibrarian()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = CreateFactory(fixture, new FakeProvider(matches: false));
        using var requester = await CreateTokenClientAsync(factory, isAdmin: false);
        var (requestId, formatId) = await CreateEbookRequestAsync(requester);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var fulfillment = scope.ServiceProvider.GetRequiredService<AutomaticRequestFulfillmentService>();
            Assert.AreEqual(0, await fulfillment.ProcessPendingAsync(CancellationToken.None));
        }

        await using var verificationScope = factory.Services.CreateAsyncScope();
        var database = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var request = await database.BookRequests.SingleAsync(request => request.Id == requestId);
        // Coming up empty is not a failure worth a librarian's attention — the
        // request stays queued so the next automatic pass, after the retry
        // cooldown elapses, tries again with no one having to click anything.
        Assert.AreEqual(RequestStatus.PendingAcquisition, request.Status);
        Assert.AreEqual(0, await database.MediaAssets.CountAsync(
            asset => asset.AssociatedRequestFormatId == formatId));
        Assert.AreEqual(1, await database.ProviderAttempts.CountAsync(
            attempt => attempt.RequestFormatId == formatId && attempt.Outcome == ProviderAttemptOutcome.NoMatch));
    }

    [TestMethod]
    public async Task ANotYetReadyProviderIsSkippedWithoutRecordingAnAttempt()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = CreateFactory(fixture, new FakeProvider(matches: true, isReady: false));
        using var requester = await CreateTokenClientAsync(factory, isAdmin: false);
        var (requestId, formatId) = await CreateEbookRequestAsync(requester);

        await ProcessAutomaticFulfillmentAsync(factory);

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var request = await database.BookRequests.SingleAsync(request => request.Id == requestId);

        // A provider that isn't ready yet (e.g. a local catalogue mid-import)
        // must be skipped exactly like a source that has nothing yet -- no
        // ProviderAttempt recorded (which would otherwise start this
        // provider's retry cooldown on a lookup that was never really made),
        // and the request stays queued for the next automatic pass.
        Assert.AreEqual(RequestStatus.PendingAcquisition, request.Status);
        Assert.AreEqual(0, await database.ProviderAttempts.CountAsync(
            attempt => attempt.RequestFormatId == formatId));
        Assert.AreEqual(0, await database.MediaAssets.CountAsync(
            asset => asset.AssociatedRequestFormatId == formatId));
    }

    [TestMethod]
    public async Task DifferentProvidersConfidentlyDisagreeingStillGoesToReview()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = new FamilyLibrarianAppFactory(fixture.ConnectionString, services =>
        {
            services.RemoveAll<IDirectAcquisitionProvider>();
            services.RemoveAll<IAutomaticDirectAcquisitionProvider>();
            services.AddSingleton<IDirectAcquisitionProvider>(new FakeProvider(matches: true));
            services.AddSingleton<IAutomaticDirectAcquisitionProvider>(new FakeProvider(matches: true));
            services.AddSingleton<IDirectAcquisitionProvider>(new FakeProvider(matches: true, providerId: "other-gutendex", providerResultId: "5678"));
            services.AddSingleton<IAutomaticDirectAcquisitionProvider>(new FakeProvider(matches: true, providerId: "other-gutendex", providerResultId: "5678"));
        });
        using var requester = await CreateTokenClientAsync(factory, isAdmin: false);
        var (requestId, formatId) = await CreateEbookRequestAsync(requester);

        await ProcessAutomaticFulfillmentAsync(factory);

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var request = await database.BookRequests.SingleAsync(request => request.Id == requestId);
        Assert.AreEqual(RequestStatus.NeedsReview, request.Status);
        Assert.AreEqual(0, await database.MediaAssets.CountAsync(
            asset => asset.AssociatedRequestFormatId == formatId));
    }

    [TestMethod]
    public async Task ALanguageExcludedResultRoutesToThePreferenceAmbiguityFlowNotifyingTheRequesterAndAdmin()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = CreateFactory(
            fixture, new FakeProvider(matches: true, requiresLanguageConfirmation: true, language: "spa"));
        using var requester = await CreateTokenClientAsync(factory, isAdmin: false);
        var (requestId, formatId) = await CreateEbookRequestAsync(requester);

        await ProcessAutomaticFulfillmentAsync(factory);

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var request = await database.BookRequests.SingleAsync(request => request.Id == requestId);

        // ACCURACY-1's language exclusion, with nothing better available, is a
        // preference decision -- not a trust/safety judgment -- so it must not
        // be silently acquired, and must not be treated as a plain "nothing
        // found yet" either.
        Assert.AreEqual(RequestStatus.NeedsReview, request.Status);
        Assert.AreEqual(RequestReviewCategory.PreferenceAmbiguity, request.ReviewCategory);
        Assert.AreEqual(0, await database.MediaAssets.CountAsync(
            asset => asset.AssociatedRequestFormatId == formatId));

        var candidate = await database.RequestReviewCandidates.SingleAsync(c => c.RequestId == requestId);
        Assert.AreEqual(formatId, candidate.RequestFormatId);
        Assert.AreEqual("gutendex", candidate.ProviderId);
        Assert.AreEqual("1234", candidate.ProviderResultId);
        Assert.AreEqual("spa", candidate.Language);

        var requesterUserId = await GetUserIdAsync(database, WebTestFixture.UserEmail);
        var userNotifications = await database.NotificationEvents.Where(e =>
            e.Category == NotificationCategories.RequestPreferenceAmbiguity &&
            e.RecipientUserId == requesterUserId &&
            e.SubjectId == requestId.ToString()).ToListAsync();
        Assert.AreEqual(1, userNotifications.Count);
        Assert.AreEqual(NotificationAudience.SingleUser, userNotifications[0].Audience);

        // Additive, not exclusive: the admin-broadcast NeedsReview notification
        // still fires unconditionally, same as every other review category.
        Assert.AreEqual(1, await database.NotificationEvents.CountAsync(e =>
            e.Category == NotificationCategories.RequestNeedsReview &&
            e.Audience == NotificationAudience.AdminBroadcast &&
            e.SubjectId == requestId.ToString()));
    }

    [TestMethod]
    public async Task ProviderDisagreementDoesNotNotifyTheRequesterOnlyTheAdmin()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = new FamilyLibrarianAppFactory(fixture.ConnectionString, services =>
        {
            services.RemoveAll<IDirectAcquisitionProvider>();
            services.RemoveAll<IAutomaticDirectAcquisitionProvider>();
            services.AddSingleton<IDirectAcquisitionProvider>(new FakeProvider(matches: true));
            services.AddSingleton<IAutomaticDirectAcquisitionProvider>(new FakeProvider(matches: true));
            services.AddSingleton<IDirectAcquisitionProvider>(new FakeProvider(matches: true, providerId: "other-gutendex", providerResultId: "5678"));
            services.AddSingleton<IAutomaticDirectAcquisitionProvider>(new FakeProvider(matches: true, providerId: "other-gutendex", providerResultId: "5678"));
        });
        using var requester = await CreateTokenClientAsync(factory, isAdmin: false);
        var (requestId, _) = await CreateEbookRequestAsync(requester);

        await ProcessAutomaticFulfillmentAsync(factory);

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var request = await database.BookRequests.SingleAsync(request => request.Id == requestId);
        Assert.AreEqual(RequestReviewCategory.ProviderDisagreement, request.ReviewCategory);
        Assert.AreEqual(0, await database.RequestReviewCandidates.CountAsync(c => c.RequestId == requestId));

        var requesterUserId = await GetUserIdAsync(database, WebTestFixture.UserEmail);
        Assert.AreEqual(0, await database.NotificationEvents.CountAsync(e =>
            e.Audience == NotificationAudience.SingleUser && e.RecipientUserId == requesterUserId &&
            e.SubjectId == requestId.ToString()));
    }

    [TestMethod]
    public async Task AcceptingAPreferenceAmbiguityCandidateAcquiresItAndClearsReviewState()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = CreateFactory(
            fixture, new FakeProvider(matches: true, requiresLanguageConfirmation: true, language: "spa"));
        using var requester = await CreateTokenClientAsync(factory, isAdmin: false);
        var (requestId, formatId) = await CreateEbookRequestAsync(requester);

        await ProcessAutomaticFulfillmentAsync(factory);

        Guid candidateId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            candidateId = (await database.RequestReviewCandidates.SingleAsync(c => c.RequestId == requestId)).Id;
        }

        var response = await requester.PostAsJsonAsync(
            $"/api/v1/requests/{requestId}/needs-review/resolve", new ResolveNeedsReviewRequest(candidateId));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var resolved = await response.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(resolved);
        Assert.IsNull(resolved.NeedsReview);

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDatabase = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var verifiedRequest = await verifyDatabase.BookRequests.SingleAsync(r => r.Id == requestId);
        Assert.IsNull(verifiedRequest.ReviewCategory);
        Assert.AreEqual(0, await verifyDatabase.RequestReviewCandidates.CountAsync(c => c.RequestId == requestId));
        Assert.AreEqual(1, await verifyDatabase.MediaAssets.CountAsync(
            asset => asset.AssociatedRequestFormatId == formatId));
    }

    [TestMethod]
    public async Task DismissingAPreferenceAmbiguityReviewReopensTheRequestForAnotherAutomaticPass()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = CreateFactory(
            fixture, new FakeProvider(matches: true, requiresLanguageConfirmation: true, language: "spa"));
        using var requester = await CreateTokenClientAsync(factory, isAdmin: false);
        var (requestId, _) = await CreateEbookRequestAsync(requester);

        await ProcessAutomaticFulfillmentAsync(factory);

        var response = await requester.PostAsJsonAsync(
            $"/api/v1/requests/{requestId}/needs-review/resolve", new ResolveNeedsReviewRequest(CandidateId: null));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var dismissed = await response.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(dismissed);
        Assert.AreEqual("PendingAcquisition", dismissed.Status);
        Assert.IsNull(dismissed.NeedsReview);

        // Dismissing bumps StatusChangedAtUtc, which is what lets the retry
        // cooldown be bypassed immediately -- same mechanism a librarian's
        // manual recheck already relies on.
        await ProcessAutomaticFulfillmentAsync(factory);

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDatabase = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.AreEqual(2, await verifyDatabase.ProviderAttempts.CountAsync(
            attempt => attempt.RequestId == requestId && attempt.ProviderId == "gutendex"));
        var reprocessedRequest = await verifyDatabase.BookRequests.SingleAsync(r => r.Id == requestId);
        Assert.AreEqual(RequestReviewCategory.PreferenceAmbiguity, reprocessedRequest.ReviewCategory);
    }

    [TestMethod]
    public async Task AStaleExpectedVersionOnResolveReturnsConflictWithoutMutatingTheRequest()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = CreateFactory(
            fixture, new FakeProvider(matches: true, requiresLanguageConfirmation: true, language: "spa"));
        using var requester = await CreateTokenClientAsync(factory, isAdmin: false);
        var (requestId, _) = await CreateEbookRequestAsync(requester);

        await ProcessAutomaticFulfillmentAsync(factory);

        Guid candidateId;
        uint currentVersion;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            candidateId = (await database.RequestReviewCandidates.SingleAsync(c => c.RequestId == requestId)).Id;
            currentVersion = (await database.BookRequests.SingleAsync(r => r.Id == requestId)).Version;
        }

        // Simulates a second household member acting on a stale copy of the
        // page -- the loser of a race must see a clean conflict, not an
        // unhandled DbUpdateConcurrencyException (a real gap found in review:
        // this endpoint originally had no version check at all).
        var response = await requester.PostAsJsonAsync(
            $"/api/v1/requests/{requestId}/needs-review/resolve",
            new ResolveNeedsReviewRequest(candidateId, ExpectedVersion: currentVersion + 12345));

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDatabase = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var verifiedRequest = await verifyDatabase.BookRequests.SingleAsync(r => r.Id == requestId);
        Assert.AreEqual(RequestStatus.NeedsReview, verifiedRequest.Status);
        Assert.AreEqual(RequestReviewCategory.PreferenceAmbiguity, verifiedRequest.ReviewCategory);
        Assert.AreEqual(1, await verifyDatabase.RequestReviewCandidates.CountAsync(c => c.RequestId == requestId));
    }

    [TestMethod]
    public async Task AnAdminCanResolveAPreferenceAmbiguityReviewTooAdditiveNotExclusive()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = CreateFactory(
            fixture, new FakeProvider(matches: true, requiresLanguageConfirmation: true, language: "spa"));
        using var requester = await CreateTokenClientAsync(factory, isAdmin: false);
        using var admin = await CreateTokenClientAsync(factory);
        var (requestId, formatId) = await CreateEbookRequestAsync(requester);

        await ProcessAutomaticFulfillmentAsync(factory);

        Guid candidateId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            candidateId = (await database.RequestReviewCandidates.SingleAsync(c => c.RequestId == requestId)).Id;
        }

        var response = await admin.PostAsJsonAsync(
            $"/api/v1/admin/requests/{requestId}/needs-review/resolve", new ResolveNeedsReviewRequest(candidateId));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDatabase = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.AreEqual(1, await verifyDatabase.MediaAssets.CountAsync(
            asset => asset.AssociatedRequestFormatId == formatId));
    }

    private static async Task<Guid> GetUserIdAsync(AppDbContext database, string email) =>
        (await database.Users.SingleAsync(user => user.Email == email)).Id;

    [TestMethod]
    public async Task RepeatedAutomaticPassesDoNotRepeatALookupUntilTheRequestIsReopened()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = CreateFactory(fixture, new FakeProvider(matches: false));
        using var requester = await CreateTokenClientAsync(factory, isAdmin: false);
        var (requestId, formatId) = await CreateEbookRequestAsync(requester);

        await ProcessAutomaticFulfillmentAsync(factory);
        // A second pass moments later must not repeat the lookup: the retry
        // cooldown has not elapsed and nothing about the request has changed.
        await ProcessAutomaticFulfillmentAsync(factory);

        await using (var firstPassScope = factory.Services.CreateAsyncScope())
        {
            var firstPassDatabase = firstPassScope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.AreEqual(1, await firstPassDatabase.ProviderAttempts.CountAsync(attempt =>
                attempt.RequestId == requestId && attempt.RequestFormatId == formatId && attempt.ProviderId == "gutendex"));
        }

        var current = await requester.GetFromJsonAsync<BookRequestListResponse>("/api/v1/me/requests");
        Assert.IsNotNull(current);
        var pending = current.Active.Single(request => request.Id == requestId);
        Assert.AreEqual("PendingAcquisition", pending.Status);

        var cancelled = await requester.PostAsJsonAsync(
            $"/api/v1/requests/{requestId}/transitions",
            new ChangeBookRequestStatusRequest("Cancelled", null, pending.Version));
        var cancelledRequest = await cancelled.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.AreEqual(HttpStatusCode.OK, cancelled.StatusCode);
        Assert.IsNotNull(cancelledRequest);

        var reopened = await requester.PostAsJsonAsync(
            $"/api/v1/requests/{requestId}/transitions",
            new ChangeBookRequestStatusRequest("PendingAcquisition", null, cancelledRequest.Version));
        Assert.AreEqual(HttpStatusCode.OK, reopened.StatusCode);

        await ProcessAutomaticFulfillmentAsync(factory);

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.AreEqual(2, await database.ProviderAttempts.CountAsync(attempt =>
            attempt.RequestId == requestId && attempt.RequestFormatId == formatId && attempt.ProviderId == "gutendex"));
    }

    private static async Task ProcessAutomaticFulfillmentAsync(FamilyLibrarianAppFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var fulfillment = scope.ServiceProvider.GetRequiredService<AutomaticRequestFulfillmentService>();
        await fulfillment.ProcessPendingAsync(CancellationToken.None);
    }

    private static FamilyLibrarianAppFactory CreateFactory(WebTestFixture fixture, IAutomaticDirectAcquisitionProvider provider) =>
        new(
            fixture.ConnectionString,
            services =>
            {
                services.RemoveAll<IDirectAcquisitionProvider>();
                services.RemoveAll<IAutomaticDirectAcquisitionProvider>();
                services.AddSingleton<IDirectAcquisitionProvider>(provider);
                services.AddSingleton<IAutomaticDirectAcquisitionProvider>(provider);
            });

    private static async Task<HttpClient> CreateTokenClientAsync(FamilyLibrarianAppFactory factory, bool isAdmin = true)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new FamilyLibrarian.Contracts.Authentication.LoginRequest
            {
                Email = isAdmin ? FamilyLibrarianAppFactory.AdminEmail : WebTestFixture.UserEmail,
                Password = isAdmin ? FamilyLibrarianAppFactory.AdminPassword : WebTestFixture.UserPassword
            });
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);

        var token = await WebTestFixture.GetAntiforgeryTokenAsync(client);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);
        return client;
    }

    private static async Task<(Guid RequestId, Guid FormatId)> CreateEbookRequestAsync(HttpClient client)
    {
        var resolve = await client.PostAsync("/api/v1/catalog/candidates/demo/the-hobbit/resolve", content: null);
        resolve.EnsureSuccessStatusCode();
        var work = await resolve.Content.ReadFromJsonAsync<CatalogWorkResponse>();
        Assert.IsNotNull(work);

        var created = await client.PostAsJsonAsync(
            "/api/v1/requests/",
            new CreateBookRequestRequest(await WebTestFixture.Require(_fixture).CopyWorkForTestAsync(work.Id), ["Ebook"], null, false, false));
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
        var request = await created.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(request);

        var format = request.Formats.Single(format => format.MediaType == "Ebook");
        return (request.Id, format.FormatId);
    }

    /// <summary>Always reports one DirectAcquisition match (or none), and fetches a fake EPUB.</summary>
    private sealed class FakeProvider(
        bool matches, string providerId = "gutendex", string providerResultId = "1234", bool throwsOnFetch = false,
        bool isReady = true, bool requiresLanguageConfirmation = false, string? language = null)
        : IAutomaticDirectAcquisitionProvider
    {
        public string Id => providerId;

        public Task<bool> IsReadyAsync(CancellationToken cancellationToken) => Task.FromResult(isReady);

        public Task<IReadOnlyList<FulfillmentOption>> FindDirectAcquisitionsAsync(
            Guid workId, RequestMediaType mediaType, CancellationToken cancellationToken)
        {
            if (!matches || mediaType != RequestMediaType.Ebook)
            {
                return Task.FromResult<IReadOnlyList<FulfillmentOption>>([]);
            }

            IReadOnlyList<FulfillmentOption> options =
            [
                new FulfillmentOption(
                    ProviderId: Id,
                    ProviderResultId: providerResultId,
                    WorkId: workId,
                    EditionId: null,
                    MediaType: RequestMediaType.Ebook,
                    OptionKind: OptionKind.DirectAcquisition,
                    AcquisitionMethod: AcquisitionMethod.DirectDownload,
                    Format: "epub",
                    Language: language,
                    Quality: null,
                    Availability: null,
                    Cost: 0m,
                    Currency: null,
                    LicenseOrUsageStatus: "Public domain",
                    DrmStatus: null,
                    ExternalActionUri: null,
                    ProviderData: "https://example.test/book.epub",
                    MatchBasis: null,
                    RequiresLanguageConfirmation: requiresLanguageConfirmation)
            ];
            return Task.FromResult(options);
        }

        public Task<IReadOnlyList<FulfillmentOption>> FindDirectAcquisitionsAsync(
            BookIdentity identity, RequestMediaType mediaType, CancellationToken cancellationToken) =>
            FindDirectAcquisitionsAsync(Guid.Empty, mediaType, cancellationToken);

        public Task<IReadOnlyList<DirectAcquisitionFile>> FetchAsync(
            FulfillmentOption fulfillmentOption, CancellationToken cancellationToken)
        {
            if (throwsOnFetch)
            {
                throw new InvalidOperationException(
                    "The Gutenberg audiobook exceeds the configured track limit.");
            }

            return Task.FromResult<IReadOnlyList<DirectAcquisitionFile>>(
            [
                new DirectAcquisitionFile(
                    new MemoryStream(EpubTestFixture.BuildMinimalEpubBytes()),
                    "the-hobbit.epub")
            ]);
        }
    }
}
