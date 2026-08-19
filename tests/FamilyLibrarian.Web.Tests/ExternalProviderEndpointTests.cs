using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Contracts.Providers;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FamilyLibrarian.Web.Tests;

/// <summary>Covers the admin-facing external-provider registration CRUD surface.</summary>
[TestClass]
public sealed class ExternalProviderEndpointTests
{
    private const string SecretValue = "do-not-leak-8675309-api-key";
    private static readonly string[] ExpectedCatalogEntryCapabilities = ["search", "acquire"];

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

    private static async Task<HttpClient> CreateAdminClientWithTokenAsync(WebTestFixture fixture)
    {
        var client = await fixture.CreateAdminClientAsync();
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(client);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);
        return client;
    }

    [TestMethod]
    public async Task AProviderCanBeRegisteredListedAndRemoved()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateAdminClientWithTokenAsync(fixture);

        var create = await client.PostAsJsonAsync(
            "/api/v1/admin/external-providers/",
            new CreateExternalProviderRequest("test-provider-1", "Test Provider", "http://provider.test"));
        Assert.AreEqual(HttpStatusCode.OK, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<ExternalProviderResponse>();
        Assert.IsNotNull(created);
        Assert.AreEqual("test-provider-1", created.ProviderId);
        Assert.IsFalse(created.IsEnabled);

        var list = await client.GetFromJsonAsync<ExternalProviderResponse[]>("/api/v1/admin/external-providers/");
        Assert.IsNotNull(list);
        Assert.IsTrue(list.Any(provider => provider.Id == created.Id));

        var remove = await client.DeleteAsync($"/api/v1/admin/external-providers/{created.Id}");
        Assert.AreEqual(HttpStatusCode.NoContent, remove.StatusCode);

        var listAfter = await client.GetFromJsonAsync<ExternalProviderResponse[]>("/api/v1/admin/external-providers/");
        Assert.IsNotNull(listAfter);
        Assert.IsFalse(listAfter.Any(provider => provider.Id == created.Id));
    }

    [TestMethod]
    public async Task RegisteringTheSameProviderIdTwiceIsRejected()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateAdminClientWithTokenAsync(fixture);

        var first = await client.PostAsJsonAsync(
            "/api/v1/admin/external-providers/",
            new CreateExternalProviderRequest("duplicate-provider", "First", "http://provider.test"));
        Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);

        var second = await client.PostAsJsonAsync(
            "/api/v1/admin/external-providers/",
            new CreateExternalProviderRequest("duplicate-provider", "Second", "http://provider.test"));
        Assert.AreEqual(HttpStatusCode.BadRequest, second.StatusCode);
    }

    [TestMethod]
    public async Task NoEndpointEverReturnsTheStoredApiKey()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateAdminClientWithTokenAsync(fixture);

        var create = await client.PostAsJsonAsync(
            "/api/v1/admin/external-providers/",
            new CreateExternalProviderRequest("secret-provider", "Secret Provider", "http://provider.test"));
        var created = await create.Content.ReadFromJsonAsync<ExternalProviderResponse>();
        Assert.IsNotNull(created);

        var write = await client.PutAsJsonAsync(
            $"/api/v1/admin/external-providers/{created.Id}/api-key",
            new SetExternalProviderApiKeyRequest(SecretValue));
        Assert.AreEqual(HttpStatusCode.OK, write.StatusCode);

        var writeBody = await write.Content.ReadAsStringAsync();
        var getBody = await client.GetStringAsync("/api/v1/admin/external-providers/");

        Assert.IsFalse(writeBody.Contains(SecretValue, StringComparison.Ordinal));
        Assert.IsFalse(getBody.Contains(SecretValue, StringComparison.Ordinal));

        var read = await write.Content.ReadFromJsonAsync<ExternalProviderResponse>();
        Assert.IsNotNull(read);
        Assert.IsTrue(read.HasApiKey);
        Assert.AreEqual(SecretValue[^4..], read.ApiKeyHint);
    }

    [TestMethod]
    public async Task ANonAdminIsForbiddenOnEveryExternalProviderRoute()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await fixture.CreateUserClientAsync();
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(client);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);

        var read = await client.GetAsync("/api/v1/admin/external-providers/");
        var write = await client.PostAsJsonAsync(
            "/api/v1/admin/external-providers/",
            new CreateExternalProviderRequest("nope", "Nope", "http://provider.test"));

        Assert.AreEqual(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, write.StatusCode);
    }

    [TestMethod]
    public async Task SettingAnEgressPolicyOverrideChangesTheEffectivePolicyButNotTheCachedOne()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateAdminClientWithTokenAsync(fixture);

        var create = await client.PostAsJsonAsync(
            "/api/v1/admin/external-providers/",
            new CreateExternalProviderRequest("override-provider", "Override Provider", "http://provider.test"));
        var created = await create.Content.ReadFromJsonAsync<ExternalProviderResponse>();
        Assert.IsNotNull(created);
        Assert.AreEqual("Normal", created.CachedEgressPolicy);
        Assert.IsNull(created.EgressPolicyOverride);
        Assert.AreEqual("Normal", created.EffectiveEgressPolicy);

        var setOverride = await client.PutAsJsonAsync(
            $"/api/v1/admin/external-providers/{created.Id}/egress-policy-override",
            new SetExternalProviderEgressPolicyOverrideRequest("PrivateRequired"));
        Assert.AreEqual(HttpStatusCode.OK, setOverride.StatusCode);
        var overridden = await setOverride.Content.ReadFromJsonAsync<ExternalProviderResponse>();
        Assert.IsNotNull(overridden);
        Assert.AreEqual("Normal", overridden.CachedEgressPolicy);
        Assert.AreEqual("PrivateRequired", overridden.EgressPolicyOverride);
        Assert.AreEqual("PrivateRequired", overridden.EffectiveEgressPolicy);

        var clearOverride = await client.PutAsJsonAsync(
            $"/api/v1/admin/external-providers/{created.Id}/egress-policy-override",
            new SetExternalProviderEgressPolicyOverrideRequest(null));
        Assert.AreEqual(HttpStatusCode.OK, clearOverride.StatusCode);
        var cleared = await clearOverride.Content.ReadFromJsonAsync<ExternalProviderResponse>();
        Assert.IsNotNull(cleared);
        Assert.IsNull(cleared.EgressPolicyOverride);
        Assert.AreEqual("Normal", cleared.EffectiveEgressPolicy);
    }

    [TestMethod]
    public async Task AnAdminCanChooseThePerProviderRecheckSchedule()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateAdminClientWithTokenAsync(fixture);

        var create = await client.PostAsJsonAsync(
            "/api/v1/admin/external-providers/",
            new CreateExternalProviderRequest("scheduled-provider", "Scheduled Provider", "http://provider.test"));
        var created = await create.Content.ReadFromJsonAsync<ExternalProviderResponse>();
        Assert.IsNotNull(created);
        Assert.AreEqual("Manual", created.RecheckSchedule);

        var scheduled = await client.PutAsJsonAsync(
            $"/api/v1/admin/external-providers/{created.Id}/recheck-schedule",
            new SetExternalProviderRecheckScheduleRequest("Weekly"));
        Assert.AreEqual(HttpStatusCode.OK, scheduled.StatusCode);
        var updated = await scheduled.Content.ReadFromJsonAsync<ExternalProviderResponse>();
        Assert.IsNotNull(updated);
        Assert.AreEqual("Weekly", updated.RecheckSchedule);

        var invalid = await client.PutAsJsonAsync(
            $"/api/v1/admin/external-providers/{created.Id}/recheck-schedule",
            new SetExternalProviderRecheckScheduleRequest("Hourly"));
        Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [TestMethod]
    public async Task AFetchedCatalogsEntriesRoundTripThroughTheApi()
    {
        var fixture = WebTestFixture.Require(_fixture);
        const string catalogUrl = "http://catalog.test/providers.json";
        const string entriesJson = """
            [
                {
                    "id": "sample-provider",
                    "name": "Sample Provider",
                    "protocolVersion": "1.0",
                    "capabilities": ["search", "acquire"],
                    "license": "MIT",
                    "publisher": "Family Librarian",
                    "trustLabel": "official",
                    "homepageUrl": "https://example.test"
                }
            ]
            """;

        await using var factory = new FamilyLibrarianAppFactory(
            fixture.ConnectionString,
            services =>
            {
                services.RemoveAll<IProviderCatalogFetcher>();
                services.AddSingleton<IProviderCatalogFetcher>(new FakeProviderCatalogFetcher(catalogUrl, entriesJson));
            });

        using var admin = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var signIn = await admin.PostAsJsonAsync(
            "/api/auth/login",
            new FamilyLibrarian.Contracts.Authentication.LoginRequest
            {
                Email = FamilyLibrarianAppFactory.AdminEmail,
                Password = FamilyLibrarianAppFactory.AdminPassword
            });
        Assert.AreEqual(HttpStatusCode.NoContent, signIn.StatusCode);
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(admin);
        admin.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);

        var add = await admin.PostAsJsonAsync(
            "/api/v1/admin/provider-catalogs/", new AddProviderCatalogRequest(catalogUrl, "Test Catalog"));
        Assert.AreEqual(HttpStatusCode.OK, add.StatusCode);

        var catalogs = await admin.GetFromJsonAsync<ProviderCatalogResponse[]>("/api/v1/admin/provider-catalogs/");
        Assert.IsNotNull(catalogs);
        var catalog = catalogs.SingleOrDefault(c => c.Url == catalogUrl);
        Assert.IsNotNull(catalog);
        Assert.IsTrue(catalog.LastFetchSucceeded);
        Assert.AreEqual(1, catalog.Entries.Count);

        var entry = catalog.Entries[0];
        Assert.AreEqual("sample-provider", entry.Id);
        Assert.AreEqual("Sample Provider", entry.Name);
        Assert.AreEqual("1.0", entry.ProtocolVersion);
        CollectionAssert.AreEqual(ExpectedCatalogEntryCapabilities, entry.Capabilities.ToArray());
        Assert.AreEqual("MIT", entry.License);
        Assert.AreEqual("Family Librarian", entry.Publisher);
        Assert.AreEqual("official", entry.TrustLabel);
        Assert.AreEqual("https://example.test", entry.HomepageUrl);
    }

    /// <summary>Succeeds only for the one configured URL, mirroring the real fetcher's shape without any network access.</summary>
    private sealed class FakeProviderCatalogFetcher(string expectedUrl, string entriesJson) : IProviderCatalogFetcher
    {
        public Task<ProviderCatalogFetchOutcome> FetchAsync(string url, CancellationToken cancellationToken) =>
            Task.FromResult(string.Equals(url, expectedUrl, StringComparison.Ordinal)
                ? ProviderCatalogFetchOutcome.Success(entriesJson)
                : ProviderCatalogFetchOutcome.Failure("Unexpected catalog URL in test."));
    }
}
