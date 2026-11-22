using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Providers;
using FamilyLibrarian.Web.Tests.Harness;

namespace FamilyLibrarian.Web.Tests;

/// <summary>Covers the admin-facing external-provider registration CRUD surface.</summary>
[TestClass]
public sealed class ExternalProviderEndpointTests
{
    private const string SecretValue = "do-not-leak-8675309-api-key";

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
    public async Task AnAdminCanChooseThePerProviderRecheckSchedule()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateAdminClientWithTokenAsync(fixture);

        var create = await client.PostAsJsonAsync(
            "/api/v1/admin/external-providers/",
            new CreateExternalProviderRequest("scheduled-provider", "Scheduled Provider", "http://provider.test"));
        var created = await create.Content.ReadFromJsonAsync<ExternalProviderResponse>();
        Assert.IsNotNull(created);
        Assert.AreEqual("Daily", created.RecheckSchedule);

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

    // Download confidence decides what may be fetched; there is no separate automatic-download switch.
    [TestMethod]
    public async Task ANewSourceIsRecheckedDailyAndHasNoAutomaticDownloadSwitch()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateAdminClientWithTokenAsync(fixture);

        var create = await client.PostAsJsonAsync(
            "/api/v1/admin/external-providers/",
            new CreateExternalProviderRequest("daily-by-default-provider", "Daily By Default", "http://provider.test"));
        var created = await create.Content.ReadFromJsonAsync<ExternalProviderResponse>();
        Assert.IsNotNull(created);
        Assert.AreEqual("Daily", created.RecheckSchedule, "A source that found nothing is asked again daily unless an administrator says otherwise.");
        using var payload = System.Text.Json.JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        Assert.IsFalse(payload.RootElement.TryGetProperty("autoAcquireEnabled", out _), "The removed switch must not remain in the admin contract.");

        var removed = await client.PutAsJsonAsync(
            $"/api/v1/admin/external-providers/{created.Id}/auto-acquire", new { enabled = false });
        Assert.IsFalse(removed.IsSuccessStatusCode, "The automatic-download switch no longer exists.");

        var manual = await client.PutAsJsonAsync(
            $"/api/v1/admin/external-providers/{created.Id}/recheck-schedule",
            new SetExternalProviderRecheckScheduleRequest("Manual"));
        Assert.AreEqual(HttpStatusCode.OK, manual.StatusCode, "Manual stays available as a choice.");
    }
}
