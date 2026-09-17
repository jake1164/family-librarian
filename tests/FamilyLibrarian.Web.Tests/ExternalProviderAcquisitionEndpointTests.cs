using System.Net;
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

/// <summary>
/// Proves the actual payoff of M13's design: an enabled external provider's
/// search results appear through the *existing* fulfillment-options endpoint,
/// and acquiring through it stages a <see cref="MediaAsset"/> through the
/// *existing* direct-acquisitions endpoint — zero endpoint changes needed for
/// either, only where the options came from server-side.
/// </summary>
[TestClass]
public sealed class ExternalProviderAcquisitionEndpointTests
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
    public async Task AnEnabledExternalProvidersResultAppearsAndCanBeAcquired()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = new FamilyLibrarianAppFactory(
            fixture.ConnectionString,
            services =>
            {
                services.RemoveAll<IExternalProviderClient>();
                services.AddSingleton<IExternalProviderClient>(new FakeExternalProviderClient());
            });

        using var admin = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await SignInAsync(admin, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword);
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(admin);
        admin.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);

        var create = await admin.PostAsJsonAsync(
            "/api/v1/admin/external-providers/",
            new CreateExternalProviderRequest("fake-external", "Fake External", "http://fake-external.test"));
        create.EnsureSuccessStatusCode();
        var provider = await create.Content.ReadFromJsonAsync<ExternalProviderResponse>();
        Assert.IsNotNull(provider);

        var enable = await admin.PutAsJsonAsync(
            $"/api/v1/admin/external-providers/{provider.Id}/enabled", new SetExternalProviderEnabledRequest(true));
        enable.EnsureSuccessStatusCode();

        var resolve = await admin.PostAsync("/api/v1/catalog/candidates/demo/the-hobbit/resolve", content: null);
        resolve.EnsureSuccessStatusCode();
        var work = await resolve.Content.ReadFromJsonAsync<CatalogWorkResponse>();
        Assert.IsNotNull(work);

        var created = await admin.PostAsJsonAsync(
            "/api/v1/requests/", new CreateBookRequestRequest(await WebTestFixture.Require(_fixture).CopyWorkForTestAsync(work.Id), ["Ebook"], null, false, false));
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
        var request = await created.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(request);
        var format = request.Formats.Single(candidate => candidate.MediaType == "Ebook");

        var fulfillment = await admin.GetFromJsonAsync<WorkFulfillmentOptionsResponse>(
            $"/api/v1/catalog/works/{work.Id}/fulfillment-options");
        Assert.IsNotNull(fulfillment);
        var option = fulfillment.Ebook.SingleOrDefault(candidate => candidate.ProviderId == "fake-external");
        Assert.IsNotNull(option, "The fake external provider's search result should appear in fulfillment options.");

        // "the-hobbit" is the shared canonical Work every test in this class
        // resolves to, so whether IWorkLookup already has an ISBN for it
        // (from an earlier test's own acquisition) depends on execution
        // order -- MatchBasis can legitimately be Identifier or TitleAuthor
        // here. Always confirming is safe either way (a no-op for an
        // Identifier-tier match); APlausibleButWrongTitleRequiresConfirmationBeforeFetching
        // below is what actually proves the confidence gate itself.
        var acquire = await admin.PostAsync(
            $"/api/v1/admin/requests/{request.Id}/formats/{format.FormatId}/direct-acquisitions/fake-external/{option.ProviderResultId}" +
            "?confirmLowConfidenceMatch=true",
            content: null);
        // Protocol v2: the provider accepts a durable job rather than
        // returning bytes synchronously -- see DirectAcquisitionService.
        Assert.AreEqual(HttpStatusCode.Accepted, acquire.StatusCode);
        var result = await acquire.Content.ReadFromJsonAsync<ManualAcquisitionInProgressResponse>();
        Assert.IsNotNull(result);

        await using (var pollScope = factory.Services.CreateAsyncScope())
        {
            var polling = pollScope.ServiceProvider.GetRequiredService<AcquisitionJobPollingService>();
            Assert.AreEqual(1, await polling.ProcessDueAsync(CancellationToken.None));
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await database.ProviderAcquisitionJobs.SingleAsync(
            acquisitionJob => acquisitionJob.Id == result.ProviderAcquisitionJobId);
        Assert.AreEqual(ProviderAcquisitionJobLifecycleState.Completed, job.LifecycleState);
        Assert.AreEqual("fake-external", job.ProviderId);

        var asset = await database.MediaAssets.SingleAsync(
            mediaAsset => mediaAsset.AssociatedRequestFormatId == format.FormatId);
        Assert.AreEqual(MediaAssetStorageState.Trusted, asset.StorageState);
        Assert.AreEqual(1, await database.SecurityEvaluations.CountAsync(
            evaluation => evaluation.AssetId == asset.Id));

        var acquisitionJob = await database.AcquisitionJobs.SingleAsync(
            acquisitionJob => acquisitionJob.RequestId == request.Id);
        Assert.AreEqual("fake-external", acquisitionJob.ProviderId);
        Assert.AreEqual(EgressPolicy.Normal, acquisitionJob.EgressPolicy);
    }

    [TestMethod]
    public async Task ADisabledExternalProviderIsRefused()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = new FamilyLibrarianAppFactory(
            fixture.ConnectionString,
            services =>
            {
                services.RemoveAll<IExternalProviderClient>();
                services.AddSingleton<IExternalProviderClient>(new FakeExternalProviderClient());
            });

        using var admin = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await SignInAsync(admin, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword);
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(admin);
        admin.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);

        // Registered but never enabled.
        var create = await admin.PostAsJsonAsync(
            "/api/v1/admin/external-providers/",
            new CreateExternalProviderRequest("disabled-external", "Disabled External", "http://fake-external.test"));
        create.EnsureSuccessStatusCode();

        var resolve = await admin.PostAsync("/api/v1/catalog/candidates/demo/the-hobbit/resolve", content: null);
        var work = await resolve.Content.ReadFromJsonAsync<CatalogWorkResponse>();
        Assert.IsNotNull(work);
        var created = await admin.PostAsJsonAsync(
            "/api/v1/requests/", new CreateBookRequestRequest(await WebTestFixture.Require(_fixture).CopyWorkForTestAsync(work.Id), ["Ebook"], null, false, false));
        var request = await created.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(request);
        var format = request.Formats.Single(candidate => candidate.MediaType == "Ebook");

        var acquire = await admin.PostAsync(
            $"/api/v1/admin/requests/{request.Id}/formats/{format.FormatId}/direct-acquisitions/disabled-external/anything",
            content: null);

        Assert.AreEqual(HttpStatusCode.BadRequest, acquire.StatusCode);
    }

    [TestMethod]
    public async Task AScheduledExternalLookupCreatesAnAdminVisibleReviewRecordWithoutDownloading()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = new FamilyLibrarianAppFactory(
            fixture.ConnectionString,
            services =>
            {
                services.RemoveAll<IExternalProviderClient>();
                services.AddSingleton<IExternalProviderClient>(new FakeExternalProviderClient());
            });

        using var admin = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await SignInAsync(admin, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword);
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

        var resolve = await admin.PostAsync("/api/v1/catalog/candidates/demo/the-hobbit/resolve", content: null);
        var work = await resolve.Content.ReadFromJsonAsync<CatalogWorkResponse>();
        Assert.IsNotNull(work);
        var created = await admin.PostAsJsonAsync(
            "/api/v1/requests/", new CreateBookRequestRequest(await WebTestFixture.Require(_fixture).CopyWorkForTestAsync(work.Id), ["Ebook"], null, false, false));
        var request = await created.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(request);

        var format = request.Formats.Single(candidate => candidate.MediaType == "Ebook");

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var rechecks = scope.ServiceProvider.GetRequiredService<ExternalProviderRecheckService>();
            // The shared integration database may also contain another pending
            // request while the suite runs in parallel. This test's assertions
            // below remain scoped to the request it created.
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
    }

    [TestMethod]
    public async Task OverridingEgressPolicyDownToNormalLetsAcquisitionSucceedWithNoGatewayConfigured()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = new FamilyLibrarianAppFactory(
            fixture.ConnectionString,
            services =>
            {
                services.RemoveAll<IExternalProviderClient>();
                services.AddSingleton<IExternalProviderClient>(new FakeExternalProviderClient("PRIVATE_REQUIRED"));
            });

        using var admin = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await SignInAsync(admin, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword);
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(admin);
        admin.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);

        var create = await admin.PostAsJsonAsync(
            "/api/v1/admin/external-providers/",
            new CreateExternalProviderRequest("override-down-external", "Override Down External", "http://fake-external.test"));
        create.EnsureSuccessStatusCode();
        var provider = await create.Content.ReadFromJsonAsync<ExternalProviderResponse>();
        Assert.IsNotNull(provider);

        var enable = await admin.PutAsJsonAsync(
            $"/api/v1/admin/external-providers/{provider.Id}/enabled", new SetExternalProviderEnabledRequest(true));
        enable.EnsureSuccessStatusCode();

        // Populates CachedEgressPolicy from the manifest — PRIVATE_REQUIRED, with no gateway configured.
        var test = await admin.PostAsync($"/api/v1/admin/external-providers/{provider.Id}/test", content: null);
        test.EnsureSuccessStatusCode();

        var setOverride = await admin.PutAsJsonAsync(
            $"/api/v1/admin/external-providers/{provider.Id}/egress-policy-override",
            new SetExternalProviderEgressPolicyOverrideRequest("Normal"));
        setOverride.EnsureSuccessStatusCode();

        var resolve = await admin.PostAsync("/api/v1/catalog/candidates/demo/the-hobbit/resolve", content: null);
        resolve.EnsureSuccessStatusCode();
        var work = await resolve.Content.ReadFromJsonAsync<CatalogWorkResponse>();
        Assert.IsNotNull(work);

        var created = await admin.PostAsJsonAsync(
            "/api/v1/requests/", new CreateBookRequestRequest(await WebTestFixture.Require(_fixture).CopyWorkForTestAsync(work.Id), ["Ebook"], null, false, false));
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
        var request = await created.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(request);
        var format = request.Formats.Single(candidate => candidate.MediaType == "Ebook");

        var fulfillment = await admin.GetFromJsonAsync<WorkFulfillmentOptionsResponse>(
            $"/api/v1/catalog/works/{work.Id}/fulfillment-options");
        Assert.IsNotNull(fulfillment);
        var option = fulfillment.Ebook.SingleOrDefault(candidate => candidate.ProviderId == "override-down-external");
        Assert.IsNotNull(option, "An overridden-to-Normal provider should still surface options with no gateway configured.");

        var acquire = await admin.PostAsync(
            $"/api/v1/admin/requests/{request.Id}/formats/{format.FormatId}/direct-acquisitions/override-down-external/{option.ProviderResultId}" +
            "?confirmLowConfidenceMatch=true",
            content: null);
        // Reaching Accepted at all proves the override took effect: with the
        // provider's own declared PRIVATE_REQUIRED and no gateway
        // configured, routeResolver.Resolve would otherwise have blocked
        // this before ever calling SubmitAcquireAsync.
        Assert.AreEqual(HttpStatusCode.Accepted, acquire.StatusCode);
        var result = await acquire.Content.ReadFromJsonAsync<ManualAcquisitionInProgressResponse>();
        Assert.IsNotNull(result);

        await using (var pollScope = factory.Services.CreateAsyncScope())
        {
            var polling = pollScope.ServiceProvider.GetRequiredService<AcquisitionJobPollingService>();
            Assert.AreEqual(1, await polling.ProcessDueAsync(CancellationToken.None));
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await database.ProviderAcquisitionJobs.SingleAsync(
            acquisitionJob => acquisitionJob.Id == result.ProviderAcquisitionJobId);
        Assert.AreEqual(ProviderAcquisitionJobLifecycleState.Completed, job.LifecycleState);

        var acquisitionJob = await database.AcquisitionJobs.SingleAsync(
            acquisitionJob => acquisitionJob.RequestId == request.Id);
        Assert.AreEqual(EgressPolicy.Normal, acquisitionJob.EgressPolicy);
    }

    [TestMethod]
    public async Task OverridingEgressPolicyUpToPrivateRequiredBlocksAcquisitionEvenThoughTheManifestDeclaresNormal()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = new FamilyLibrarianAppFactory(
            fixture.ConnectionString,
            services =>
            {
                services.RemoveAll<IExternalProviderClient>();
                services.AddSingleton<IExternalProviderClient>(new FakeExternalProviderClient());
            });

        using var admin = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await SignInAsync(admin, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword);
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(admin);
        admin.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);

        var create = await admin.PostAsJsonAsync(
            "/api/v1/admin/external-providers/",
            new CreateExternalProviderRequest("override-up-external", "Override Up External", "http://fake-external.test"));
        create.EnsureSuccessStatusCode();
        var provider = await create.Content.ReadFromJsonAsync<ExternalProviderResponse>();
        Assert.IsNotNull(provider);

        var enable = await admin.PutAsJsonAsync(
            $"/api/v1/admin/external-providers/{provider.Id}/enabled", new SetExternalProviderEnabledRequest(true));
        enable.EnsureSuccessStatusCode();

        var setOverride = await admin.PutAsJsonAsync(
            $"/api/v1/admin/external-providers/{provider.Id}/egress-policy-override",
            new SetExternalProviderEgressPolicyOverrideRequest("PrivateRequired"));
        setOverride.EnsureSuccessStatusCode();
        var overridden = await setOverride.Content.ReadFromJsonAsync<ExternalProviderResponse>();
        Assert.IsNotNull(overridden);
        Assert.AreEqual("Normal", overridden.CachedEgressPolicy);
        Assert.AreEqual("PrivateRequired", overridden.EffectiveEgressPolicy);

        var resolve = await admin.PostAsync("/api/v1/catalog/candidates/demo/the-hobbit/resolve", content: null);
        var work = await resolve.Content.ReadFromJsonAsync<CatalogWorkResponse>();
        Assert.IsNotNull(work);
        var created = await admin.PostAsJsonAsync(
            "/api/v1/requests/", new CreateBookRequestRequest(await WebTestFixture.Require(_fixture).CopyWorkForTestAsync(work.Id), ["Ebook"], null, false, false));
        var request = await created.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(request);
        var format = request.Formats.Single(candidate => candidate.MediaType == "Ebook");

        var acquire = await admin.PostAsync(
            $"/api/v1/admin/requests/{request.Id}/formats/{format.FormatId}/direct-acquisitions/override-up-external/anything",
            content: null);

        Assert.AreEqual(HttpStatusCode.BadRequest, acquire.StatusCode);
    }

    [TestMethod]
    public async Task APlausibleButWrongTitleRequiresConfirmationBeforeFetching()
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
        await SignInAsync(admin, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword);
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(admin);
        admin.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);

        var create = await admin.PostAsJsonAsync(
            "/api/v1/admin/external-providers/",
            new CreateExternalProviderRequest("wrong-title-external", "Wrong Title External", "http://fake-external.test"));
        create.EnsureSuccessStatusCode();
        var provider = await create.Content.ReadFromJsonAsync<ExternalProviderResponse>();
        Assert.IsNotNull(provider);

        var enable = await admin.PutAsJsonAsync(
            $"/api/v1/admin/external-providers/{provider.Id}/enabled", new SetExternalProviderEnabledRequest(true));
        enable.EnsureSuccessStatusCode();

        var resolve = await admin.PostAsync("/api/v1/catalog/candidates/demo/a-wrinkle-in-time/resolve", content: null);
        resolve.EnsureSuccessStatusCode();
        var work = await resolve.Content.ReadFromJsonAsync<CatalogWorkResponse>();
        Assert.IsNotNull(work);

        var created = await admin.PostAsJsonAsync(
            "/api/v1/requests/", new CreateBookRequestRequest(await WebTestFixture.Require(_fixture).CopyWorkForTestAsync(work.Id), ["Ebook"], null, false, false));
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
        var request = await created.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(request);
        var format = request.Formats.Single(candidate => candidate.MediaType == "Ebook");

        var fulfillment = await admin.GetFromJsonAsync<WorkFulfillmentOptionsResponse>(
            $"/api/v1/catalog/works/{work.Id}/fulfillment-options");
        Assert.IsNotNull(fulfillment);
        var option = fulfillment.Ebook.SingleOrDefault(candidate => candidate.ProviderId == "wrong-title-external");
        Assert.IsNotNull(option, "The wrong-title external provider's search result should still appear as an option.");

        var acquire = await admin.PostAsync(
            $"/api/v1/admin/requests/{request.Id}/formats/{format.FormatId}/direct-acquisitions/wrong-title-external/{option.ProviderResultId}",
            content: null);
        Assert.AreEqual(HttpStatusCode.Conflict, acquire.StatusCode, "An unverified title/author match must never be fetched silently.");

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.AreEqual(0, await database.MediaAssets.CountAsync(
                asset => asset.AssociatedRequestFormatId == format.FormatId));
        }

        var confirmed = await admin.PostAsync(
            $"/api/v1/admin/requests/{request.Id}/formats/{format.FormatId}/direct-acquisitions/wrong-title-external/{option.ProviderResultId}" +
            "?confirmLowConfidenceMatch=true",
            content: null);
        Assert.AreEqual(
            HttpStatusCode.Accepted, confirmed.StatusCode, "Confirming the low-confidence match should let it proceed.");
        var confirmedResult = await confirmed.Content.ReadFromJsonAsync<ManualAcquisitionInProgressResponse>();
        Assert.IsNotNull(confirmedResult);
    }

    private static async Task SignInAsync(HttpClient client, string email, string password)
    {
        var response = await client.PostAsJsonAsync(
            "/api/auth/login", new FamilyLibrarian.Contracts.Authentication.LoginRequest { Email = email, Password = password });
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
    }

    /// <summary>Always finds "the-hobbit"-matching searches and fetches a real, minimal, valid EPUB.</summary>
    private sealed class FakeExternalProviderClient(string egressPolicy = "NORMAL") : IExternalProviderClient
    {
        public Task<ExternalProviderManifest> GetManifestAsync(
            string baseUrl, string? apiKey, EgressRoute route, CancellationToken cancellationToken) =>
            Task.FromResult(new ExternalProviderManifest(
                ["2"], "2", null, "fake-external", "Fake External", "1.0.0",
                new ProviderCapabilities(["ebook"], ["search", "acquire"], []), null, null, null, egressPolicy));

        public Task<ExternalProviderHealth> GetHealthAsync(
            string baseUrl, string? apiKey, EgressRoute route, CancellationToken cancellationToken) =>
            Task.FromResult(new ExternalProviderHealth(
                ProviderHealthStatus.Healthy, ProviderOperationalStatus.Available, ProviderOperationalStatus.Available));

        public Task<IReadOnlyList<ExternalProviderCandidate>> SearchAsync(
            string baseUrl, string? apiKey, ExternalProviderSearchRequest request, EgressRoute route,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<ExternalProviderCandidate> candidates = request.MediaType == RequestMediaType.Ebook
                ? [new ExternalProviderCandidate("fake-hobbit-1", "The Hobbit", "J. R. R. Tolkien", "epub", null, null)]
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
    /// Always returns a plausible-looking but unrelated title/author, no
    /// matter what it was actually searched for -- the exact shape PROVIDER-1
    /// §F2 exists to catch (e.g. a loosely-indexed source matched by
    /// keyword). Reproduces exit criterion 5a: FL must never fetch this
    /// without explicit confirmation, regardless of the request's own ISBN.
    /// </summary>
    private sealed class WrongTitleExternalProviderClient : IExternalProviderClient
    {
        public Task<ExternalProviderManifest> GetManifestAsync(
            string baseUrl, string? apiKey, EgressRoute route, CancellationToken cancellationToken) =>
            Task.FromResult(new ExternalProviderManifest(
                ["2"], "2", null, "wrong-title-external", "Wrong Title External", "1.0.0",
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
                ? [new ExternalProviderCandidate("wrong-title-1", "Dim Sum of Fears", "Someone Unrelated", "epub", null, null)]
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
                    "primary", ProviderOutputKind.File, "primary", "wrong-title.epub", "application/epub+zip",
                    null, null, null, null, null)
            ]);

        public Task<ExternalProviderArtifact> GetOutputAsync(
            string baseUrl, string? apiKey, string jobId, string outputId, EgressRoute route,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ExternalProviderArtifact(
                new MemoryStream(EpubTestFixture.BuildMinimalEpubBytes()), "wrong-title.epub"));

        public Task CancelAcquireAsync(
            string baseUrl, string? apiKey, string jobId, EgressRoute route, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task DeleteAcquireAsync(
            string baseUrl, string? apiKey, string jobId, EgressRoute route, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
