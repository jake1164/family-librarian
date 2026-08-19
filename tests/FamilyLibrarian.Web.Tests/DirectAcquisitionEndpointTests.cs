using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Contracts.Acquisition;
using FamilyLibrarian.Contracts.Catalog;
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
            new CreateBookRequestRequest(work.Id, ["Ebook"], null, true));
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
        var request = await created.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(request);

        var format = request.Formats.Single(format => format.MediaType == "Ebook");
        return (request.Id, format.FormatId);
    }

    /// <summary>Always reports one DirectAcquisition match (or none), and fetches a fake EPUB.</summary>
    private sealed class FakeProvider(bool matches, string providerId = "gutendex", string providerResultId = "1234")
        : IAutomaticDirectAcquisitionProvider
    {
        public string Id => providerId;

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
                    Language: null,
                    Quality: null,
                    Availability: null,
                    Cost: 0m,
                    Currency: null,
                    LicenseOrUsageStatus: "Public domain",
                    DrmStatus: null,
                    ExternalActionUri: null,
                    ProviderData: "https://example.test/book.epub")
            ];
            return Task.FromResult(options);
        }

        public Task<IReadOnlyList<DirectAcquisitionFile>> FetchAsync(
            FulfillmentOption fulfillmentOption, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DirectAcquisitionFile>>(
            [
                new DirectAcquisitionFile(
                    new MemoryStream(EpubTestFixture.BuildMinimalEpubBytes()),
                    "the-hobbit.epub")
            ]);
    }
}
