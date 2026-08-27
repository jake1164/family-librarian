using System.Net.Http.Json;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Contracts.Authentication;
using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Contracts.Policy;
using FamilyLibrarian.Domain.Policy;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FamilyLibrarian.Web.Tests;

/// <summary>Covers the "already in your library" read model surfaced on a Work's detail page.</summary>
[TestClass]
public sealed class WorkFulfillmentOptionsEndpointTests
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
    public async Task ByDefaultNothingIsReportedAsOwned()
    {
        // The shared fixture's default fakes (AlwaysEmptyCwaCatalogClient,
        // AlwaysEmptyAudiobookshelfApiClient) always report "not found."
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await fixture.CreateUserClientAsync();
        var userToken = await WebTestFixture.GetAntiforgeryTokenAsync(client);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, userToken);

        var workId = await ResolveHobbitWorkIdAsync(client);

        var response = await client.GetFromJsonAsync<WorkFulfillmentOptionsResponse>(
            $"/api/v1/catalog/works/{workId}/fulfillment-options");

        Assert.IsNotNull(response);
        Assert.AreEqual(0, response.Ebook.Count);
        Assert.AreEqual(0, response.Audiobook.Count);
    }

    [TestMethod]
    public async Task ADirectProviderTimeoutDoesNotFailTheWorkOptionsEndpoint()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = new FamilyLibrarianAppFactory(
            fixture.ConnectionString,
            services =>
            {
                services.RemoveAll<IDirectAcquisitionProvider>();
                services.AddSingleton<IDirectAcquisitionProvider, TimingOutDirectAcquisitionProvider>();
            });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await SignInAsync(client, WebTestFixture.UserEmail, WebTestFixture.UserPassword);
        var userToken = await WebTestFixture.GetAntiforgeryTokenAsync(client);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, userToken);

        var workId = await ResolveHobbitWorkIdAsync(client);

        var response = await client.GetFromJsonAsync<WorkFulfillmentOptionsResponse>(
            $"/api/v1/catalog/works/{workId}/fulfillment-options");

        Assert.IsNotNull(response);
        Assert.AreEqual(0, response.Ebook.Count);
        Assert.AreEqual(0, response.Audiobook.Count);
    }

    [TestMethod]
    public async Task AnEbookFoundInCwaIsReportedAsOwned()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = new FamilyLibrarianAppFactory(
            fixture.ConnectionString,
            services =>
            {
                services.RemoveAll<ICwaCatalogClient>();
                services.AddSingleton<ICwaCatalogClient>(new DeterministicCatalogClient("42"));
            });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await SignInAsync(client, WebTestFixture.UserEmail, WebTestFixture.UserPassword);
        var userToken = await WebTestFixture.GetAntiforgeryTokenAsync(client);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, userToken);

        // CWA is enabled and configured by the admin, but any signed-in family
        // member can see the resulting "already owned" status.
        using var adminClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await SignInAsync(adminClient, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword);
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(adminClient);
        adminClient.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);
        await ConfigureCwaAsync(adminClient);

        var workId = await ResolveHobbitWorkIdAsync(client);

        var response = await client.GetFromJsonAsync<WorkFulfillmentOptionsResponse>(
            $"/api/v1/catalog/works/{workId}/fulfillment-options");

        Assert.IsNotNull(response);
        Assert.AreEqual(1, response.Ebook.Count);
        Assert.AreEqual("Owned", response.Ebook[0].OptionKind);
        Assert.AreEqual("cwa", response.Ebook[0].ProviderId);
        Assert.AreEqual(0, response.Audiobook.Count);
    }

    [TestMethod]
    public async Task ARecommendationAppearsOnceTheSystemDefaultProfileIsSet()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = new FamilyLibrarianAppFactory(
            fixture.ConnectionString,
            services =>
            {
                services.RemoveAll<ICwaCatalogClient>();
                services.AddSingleton<ICwaCatalogClient>(new DeterministicCatalogClient("42"));
            });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await SignInAsync(client, WebTestFixture.UserEmail, WebTestFixture.UserPassword);
        var userToken = await WebTestFixture.GetAntiforgeryTokenAsync(client);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, userToken);

        using var adminClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await SignInAsync(adminClient, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword);
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(adminClient);
        adminClient.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);
        await ConfigureCwaAsync(adminClient);

        var setDefault = await adminClient.PutAsJsonAsync(
            "/api/v1/admin/policy/settings", new SetDefaultPolicyProfileRequest(PolicyProfileIds.LibraryFirst));
        setDefault.EnsureSuccessStatusCode();

        var workId = await ResolveHobbitWorkIdAsync(client);

        var response = await client.GetFromJsonAsync<WorkFulfillmentOptionsResponse>(
            $"/api/v1/catalog/works/{workId}/fulfillment-options");

        Assert.IsNotNull(response);
        Assert.IsNotNull(response.EbookRecommendation);
        Assert.AreEqual("cwa", response.EbookRecommendation.ProviderId);
        Assert.AreEqual("Already in your library", response.EbookRecommendation.Reason);
    }

    private static async Task ConfigureCwaAsync(HttpClient client)
    {
        var settings = await client.PutAsJsonAsync(
            "/api/v1/admin/publishing/cwa/",
            new FamilyLibrarian.Contracts.Publishing.SetCwaSettingsRequest(
                "Local", "/data/cwa-ingest-test", null, null, null, null, "PrivateKey", "https://cwa.example.test", null));
        settings.EnsureSuccessStatusCode();

        // Enabling requires a passing connection test for the saved configuration
        // (docs/01 §12.1.1) -- FamilyLibrarianAppFactory registers a default-safe
        // ICwaConnectionTester double, so this succeeds without a reachable CWA.
        var test = await client.PostAsJsonAsync("/api/v1/admin/publishing/cwa/test", new { });
        test.EnsureSuccessStatusCode();

        var enabled = await client.PutAsJsonAsync(
            "/api/v1/admin/publishing/cwa/enabled",
            new FamilyLibrarian.Contracts.Publishing.SetPublishingEnabledRequest(true));
        enabled.EnsureSuccessStatusCode();
    }

    private static async Task SignInAsync(HttpClient client, string email, string password)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = email, Password = password });
        Assert.AreEqual(System.Net.HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task<Guid> ResolveHobbitWorkIdAsync(HttpClient client)
    {
        var resolve = await client.PostAsync("/api/v1/catalog/candidates/demo/the-hobbit/resolve", content: null);
        resolve.EnsureSuccessStatusCode();
        var work = await resolve.Content.ReadFromJsonAsync<CatalogWorkResponse>();
        Assert.IsNotNull(work);
        return work.Id;
    }

    private sealed class DeterministicCatalogClient(string? bookId) : ICwaCatalogClient
    {
        public Task<string?> FindBookIdAsync(string title, string? author, CancellationToken cancellationToken) =>
            Task.FromResult(bookId);
    }

    private sealed class TimingOutDirectAcquisitionProvider : IDirectAcquisitionProvider
    {
        public string Id => "timing-out";

        public Task<IReadOnlyList<FulfillmentOption>> FindDirectAcquisitionsAsync(
            Guid workId,
            RequestMediaType mediaType,
            CancellationToken cancellationToken) =>
            Task.FromException<IReadOnlyList<FulfillmentOption>>(
                new TaskCanceledException("The provider lookup timed out."));

        public Task<IReadOnlyList<DirectAcquisitionFile>> FetchAsync(
            FulfillmentOption fulfillmentOption,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
