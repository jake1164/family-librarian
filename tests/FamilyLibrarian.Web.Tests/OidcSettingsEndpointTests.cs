using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Authentication;
using FamilyLibrarian.Web.Tests.Harness;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// Covers the OIDC settings panel: the same promise Admin Metadata
/// Integrations makes (a stored secret is write-only, every mutation is
/// anti-forgery protected), plus the "local sign-in can't be disabled
/// without a successful test" guardrail specific to this settings panel.
/// </summary>
[TestClass]
public sealed class OidcSettingsEndpointTests
{
    private const string SecretValue = "do-not-leak-8675309-client-secret";

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
    public async Task SettingsCanBeSavedAndReadBack()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateAdminClientWithTokenAsync(fixture);

        var write = await client.PutAsJsonAsync(
            "/api/v1/admin/authentication/oidc/",
            new SetOidcSettingsRequest(
                "Sign in with Test IdP", "https://issuer.test/", "client-abc",
                "openid profile email", "email", "groups", "family-admins", false));
        Assert.AreEqual(HttpStatusCode.OK, write.StatusCode);
        var written = await write.Content.ReadFromJsonAsync<OidcSettingsResponse>();
        Assert.IsNotNull(written);
        Assert.AreEqual("https://issuer.test/", written.Authority);
        Assert.AreEqual("client-abc", written.ClientId);

        var read = await client.GetFromJsonAsync<OidcSettingsResponse>("/api/v1/admin/authentication/oidc/");
        Assert.IsNotNull(read);
        Assert.AreEqual("client-abc", read.ClientId);
    }

    [TestMethod]
    public async Task NoEndpointEverReturnsTheStoredClientSecret()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateAdminClientWithTokenAsync(fixture);

        var write = await client.PutAsJsonAsync(
            "/api/v1/admin/authentication/oidc/client-secret", new SetOidcClientSecretRequest(SecretValue));
        Assert.AreEqual(HttpStatusCode.OK, write.StatusCode);

        var writeBody = await write.Content.ReadAsStringAsync();
        var getBody = await client.GetStringAsync("/api/v1/admin/authentication/oidc/");

        Assert.IsFalse(writeBody.Contains(SecretValue, StringComparison.Ordinal));
        Assert.IsFalse(getBody.Contains(SecretValue, StringComparison.Ordinal));

        var read = await client.GetFromJsonAsync<OidcSettingsResponse>("/api/v1/admin/authentication/oidc/");
        Assert.IsNotNull(read);
        Assert.IsTrue(read.HasClientSecret);
        Assert.AreEqual(SecretValue[^4..], read.ClientSecretHint);
    }

    [TestMethod]
    public async Task LocalLoginCannotBeDisabledWithoutASuccessfulTest()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateAdminClientWithTokenAsync(fixture);

        var disable = await client.PutAsJsonAsync(
            "/api/v1/admin/authentication/oidc/local-login-disabled", new SetOidcLocalLoginDisabledRequest(true));

        Assert.AreEqual(HttpStatusCode.BadRequest, disable.StatusCode);
    }

    [TestMethod]
    public async Task LocalLoginCanBeDisabledAfterATestSucceeds()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateAdminClientWithTokenAsync(fixture);

        var test = await client.PostAsync("/api/v1/admin/authentication/oidc/test", content: null);
        Assert.AreEqual(HttpStatusCode.OK, test.StatusCode);

        var disable = await client.PutAsJsonAsync(
            "/api/v1/admin/authentication/oidc/local-login-disabled", new SetOidcLocalLoginDisabledRequest(true));
        Assert.AreEqual(HttpStatusCode.OK, disable.StatusCode);

        var status = await client.GetFromJsonAsync<OidcSignInStatusResponse>("/api/auth/oidc/status");
        Assert.IsNotNull(status);
        Assert.IsTrue(status.LocalLoginDisabled);

        // Restore local sign-in: this fixture's database is shared across every
        // test in this class, and other tests need ordinary local accounts to
        // keep working.
        var restore = await client.PutAsJsonAsync(
            "/api/v1/admin/authentication/oidc/local-login-disabled", new SetOidcLocalLoginDisabledRequest(false));
        Assert.AreEqual(HttpStatusCode.OK, restore.StatusCode);
    }

    [TestMethod]
    public async Task ANonAdminIsForbiddenOnEveryOidcAdminRoute()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await fixture.CreateUserClientAsync();
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(client);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);

        var read = await client.GetAsync("/api/v1/admin/authentication/oidc/");
        var write = await client.PutAsJsonAsync(
            "/api/v1/admin/authentication/oidc/enabled", new SetOidcEnabledRequest(true));

        Assert.AreEqual(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, write.StatusCode);
    }
}
