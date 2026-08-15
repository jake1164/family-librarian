using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Providers;
using FamilyLibrarian.Infrastructure.Integrations;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// Covers the two promises the Admin Integrations surface makes: a stored
/// credential is write-only, and every mutation is anti-forgery protected.
/// </summary>
[TestClass]
public sealed class MetadataIntegrationsEndpointTests
{
    private const string CredentialedProvider = "googlebooks";
    private const string KeylessProvider = "openlibrary";
    private const string SecretValue = "gb-live-key-do-not-leak-8675309";

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

    /// <summary>
    /// An admin client that already carries a valid anti-forgery header.
    /// </summary>
    private static async Task<HttpClient> CreateAdminClientWithTokenAsync(WebTestFixture fixture)
    {
        var client = await fixture.CreateAdminClientAsync();
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(client);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);
        return client;
    }

    [TestMethod]
    public async Task NoEndpointReturnsAStoredCredentialAfterItIsWritten()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateAdminClientWithTokenAsync(fixture);

        var write = await client.PutAsJsonAsync(
            $"/api/v1/admin/integrations/metadata/{CredentialedProvider}/credential",
            new SetProviderCredentialRequest(SecretValue));
        Assert.AreEqual(HttpStatusCode.OK, write.StatusCode);

        // Assert on raw response text, not the deserialized contract. A future
        // property carrying the secret would still deserialize into a type that
        // does not declare it, and the leak would pass unnoticed.
        var writeBody = await write.Content.ReadAsStringAsync();
        var listBody = await client.GetStringAsync("/api/v1/admin/integrations/metadata/");

        var testResponse = await client.PostAsync(
            $"/api/v1/admin/integrations/metadata/{CredentialedProvider}/test",
            content: null);
        var testBody = await testResponse.Content.ReadAsStringAsync();

        foreach (var (name, body) in new[]
        {
            ("write", writeBody),
            ("list", listBody),
            ("test", testBody)
        })
        {
            StringAssert.DoesNotMatch(
                body,
                new System.Text.RegularExpressions.Regex(SecretValue),
                $"The {name} response leaked the stored credential.");
        }
    }

    [TestMethod]
    public async Task AStoredCredentialIsReportedOnlyAsAHint()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateAdminClientWithTokenAsync(fixture);

        await client.PutAsJsonAsync(
            $"/api/v1/admin/integrations/metadata/{CredentialedProvider}/credential",
            new SetProviderCredentialRequest(SecretValue));

        var providers = await client.GetFromJsonAsync<ProviderListResponse>(
            "/api/v1/admin/integrations/metadata/");
        Assert.IsNotNull(providers);

        var google = providers.Providers.Single(provider =>
            provider.ProviderId == CredentialedProvider);

        Assert.IsTrue(google.HasStoredCredential);
        Assert.AreEqual(SecretValue[^4..], google.CredentialHint);
        Assert.IsNotNull(google.CredentialSetAtUtc);
    }

    [TestMethod]
    public async Task AStoredCredentialIsStillDecryptableAfterAHostRestart()
    {
        var fixture = WebTestFixture.Require(_fixture);

        using (var client = await CreateAdminClientWithTokenAsync(fixture))
        {
            var write = await client.PutAsJsonAsync(
                $"/api/v1/admin/integrations/metadata/{CredentialedProvider}/credential",
                new SetProviderCredentialRequest(SecretValue));
            Assert.AreEqual(HttpStatusCode.OK, write.StatusCode);
        }

        // A second host over the same database stands in for `docker compose
        // restart`. Asserting on HasStoredCredential would prove nothing — that
        // flag only reports that ciphertext exists. Unprotecting the value is what
        // shows the Data Protection key ring persisted rather than being
        // regenerated per process.
        await using var restarted = fixture.RestartHost();
        await using var scope = restarted.Services.CreateAsyncScope();
        var credentials = scope.ServiceProvider.GetRequiredService<MetadataCredentialSource>();

        var recovered = await credentials.GetCredentialAsync(
            CredentialedProvider,
            CancellationToken.None);

        Assert.AreEqual(SecretValue, recovered);
    }

    [TestMethod]
    public async Task ASessionCookieIssuedBeforeARestartStillAuthenticates()
    {
        var fixture = WebTestFixture.Require(_fixture);

        var cookie = await fixture.IssueIdentityCookieAsync(
            WebTestFixture.UserEmail,
            WebTestFixture.UserPassword);

        await using var restarted = fixture.RestartHost();
        using var client = restarted.CreateAnonymousClient();
        client.DefaultRequestHeaders.Add("Cookie", cookie);

        var response = await client.GetAsync("/api/v1/me");

        // The auth cookie is encrypted with the same key ring that protects
        // provider credentials, so this is the black-box half of the same
        // guarantee: a restart must not sign every family member out.
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task AMutationWithoutAnAntiforgeryTokenIsRejected()
    {
        var fixture = WebTestFixture.Require(_fixture);

        // Signed in as an administrator, but with no token header — the shape a
        // cross-site request takes, since the browser attaches the auth cookie by
        // itself and the attacker cannot read the token.
        using var client = await fixture.CreateAdminClientAsync();

        var enable = await client.PutAsJsonAsync(
            $"/api/v1/admin/integrations/metadata/{KeylessProvider}/enabled",
            new SetProviderEnabledRequest(true));

        var credential = await client.PutAsJsonAsync(
            $"/api/v1/admin/integrations/metadata/{CredentialedProvider}/credential",
            new SetProviderCredentialRequest("forged"));

        var clear = await client.DeleteAsync(
            $"/api/v1/admin/integrations/metadata/{CredentialedProvider}/credential");

        var test = await client.PostAsync(
            $"/api/v1/admin/integrations/metadata/{KeylessProvider}/test",
            content: null);

        Assert.AreEqual(HttpStatusCode.BadRequest, enable.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, credential.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, clear.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, test.StatusCode);
    }

    [TestMethod]
    public async Task AReadIsAllowedWithoutAnAntiforgeryToken()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await fixture.CreateAdminClientAsync();

        var response = await client.GetAsync("/api/v1/admin/integrations/metadata/");

        // The filter must not gate safe methods, or the page could never load.
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task AnUnknownProviderIdIsNotFound()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateAdminClientWithTokenAsync(fixture);

        // The registry is an allowlist, so no route can be pointed at an arbitrary
        // provider id.
        var enable = await client.PutAsJsonAsync(
            "/api/v1/admin/integrations/metadata/not-installed/enabled",
            new SetProviderEnabledRequest(true));

        var test = await client.PostAsync(
            "/api/v1/admin/integrations/metadata/not-installed/test",
            content: null);

        Assert.AreEqual(HttpStatusCode.NotFound, enable.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, test.StatusCode);
    }

    [TestMethod]
    public async Task ACredentialedProviderCannotBeEnabledWithoutAKey()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateAdminClientWithTokenAsync(fixture);

        await client.DeleteAsync(
            $"/api/v1/admin/integrations/metadata/{CredentialedProvider}/credential");

        var response = await client.PutAsJsonAsync(
            $"/api/v1/admin/integrations/metadata/{CredentialedProvider}/enabled",
            new SetProviderEnabledRequest(true));

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
