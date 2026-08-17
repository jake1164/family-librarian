using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Authentication;
using FamilyLibrarian.Contracts.Providers;
using FamilyLibrarian.Contracts.Requests;
using FamilyLibrarian.Domain;
using FamilyLibrarian.Web.Tests.Harness;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// Proves the host — not the client — decides who may reach each endpoint.
/// </summary>
/// <remarks>
/// The Blazor WebAssembly client's route guards are presentation only. Every
/// assertion here bypasses the UI and calls the API directly, which is exactly
/// what a caller who ignores the UI would do.
/// </remarks>
[TestClass]
public sealed class EndpointAuthorizationTests
{
    private static readonly string[] InstalledProviderIds = ["demo", "openlibrary", "googlebooks", "gutendex"];

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
    [DataRow("/api/v1/me")]
    [DataRow("/api/v1/antiforgery/token")]
    [DataRow("/api/v1/admin/ping")]
    [DataRow("/api/v1/admin/integrations/metadata/")]
    [DataRow("/api/v1/admin/requests/")]
    [DataRow("/api/v1/admin/media-assets/")]
    [DataRow("/api/v1/admin/publishing/cwa/")]
    [DataRow("/api/v1/admin/publishing/audiobookshelf/")]
    [DataRow("/api/v1/admin/publishing/queue")]
    [DataRow("/api/v1/admin/policy/profiles")]
    [DataRow("/api/v1/admin/policy/settings")]
    [DataRow("/api/v1/admin/authentication/oidc/")]
    [DataRow("/api/v1/admin/external-providers/")]
    [DataRow("/api/v1/admin/private-egress-gateway/")]
    [DataRow("/api/v1/admin/provider-catalogs/")]
    public async Task AnAnonymousCallerIsChallengedOnAProtectedRoute(string route)
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = fixture.CreateAnonymousClient();

        var response = await client.GetAsync(route);

        // 401 rather than a redirect to a login page: the client parses JSON, and
        // MapFallbackToFile would answer a redirect with index.html.
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode, route);
    }

    [TestMethod]
    public async Task AnAnonymousCallerCannotLogOut()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = fixture.CreateAnonymousClient();

        var response = await client.PostAsJsonAsync("/api/auth/logout", new { });

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task WrongCredentialsAreRejectedWithoutRevealingWhetherTheAccountExists()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = fixture.CreateAnonymousClient();

        var unknownAccount = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest { Email = "nobody@family-librarian.example", Password = "Not-A-Real-Pass1!" });

        var wrongPassword = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest { Email = WebTestFixture.UserEmail, Password = "Wrong-Password-Pass1!" });

        Assert.AreEqual(HttpStatusCode.Unauthorized, unknownAccount.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
    }

    [TestMethod]
    public async Task ASignedInUserCanReadTheirOwnIdentity()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await fixture.CreateUserClientAsync();

        var me = await client.GetFromJsonAsync<CurrentUserResponse>("/api/v1/me");

        Assert.IsNotNull(me);
        Assert.AreEqual(WebTestFixture.UserEmail, me.Email);
        CollectionAssert.DoesNotContain(me.Roles.ToArray(), RoleNames.Admin);
    }

    [TestMethod]
    [DataRow("/api/v1/admin/ping")]
    [DataRow("/api/v1/admin/integrations/metadata/")]
    [DataRow("/api/v1/admin/requests/")]
    [DataRow("/api/v1/admin/media-assets/")]
    [DataRow("/api/v1/admin/publishing/cwa/")]
    [DataRow("/api/v1/admin/publishing/audiobookshelf/")]
    [DataRow("/api/v1/admin/publishing/queue")]
    [DataRow("/api/v1/admin/policy/profiles")]
    [DataRow("/api/v1/admin/policy/settings")]
    [DataRow("/api/v1/admin/authentication/oidc/")]
    [DataRow("/api/v1/admin/external-providers/")]
    [DataRow("/api/v1/admin/private-egress-gateway/")]
    [DataRow("/api/v1/admin/provider-catalogs/")]
    public async Task ANonAdminIsDeniedAnAdminRoute(string route)
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await fixture.CreateUserClientAsync();

        var response = await client.GetAsync(route);

        // 403, not 401: the caller is authenticated and simply lacks the role.
        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode, route);
    }

    [TestMethod]
    public async Task ANonAdminCannotMutateProviderConfiguration()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await fixture.CreateUserClientAsync();
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(client);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);

        // A valid anti-forgery token must not substitute for the Admin role. This
        // is the strongest form of the check: the request is well-formed in every
        // respect except authorization.
        var enable = await client.PutAsJsonAsync(
            "/api/v1/admin/integrations/metadata/openlibrary/enabled",
            new SetProviderEnabledRequest(true));

        var credential = await client.PutAsJsonAsync(
            "/api/v1/admin/integrations/metadata/googlebooks/credential",
            new SetProviderCredentialRequest("attempted-by-non-admin"));

        var clear = await client.DeleteAsync(
            "/api/v1/admin/integrations/metadata/googlebooks/credential");

        var test = await client.PostAsync(
            "/api/v1/admin/integrations/metadata/openlibrary/test",
            content: null);

        Assert.AreEqual(HttpStatusCode.Forbidden, enable.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, credential.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, clear.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, test.StatusCode);
    }

    [TestMethod]
    public async Task AnAdminReachesTheIntegrationsSurface()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await fixture.CreateAdminClientAsync();

        var providers = await client.GetFromJsonAsync<ProviderListResponse>(
            "/api/v1/admin/integrations/metadata/");

        Assert.IsNotNull(providers);
        CollectionAssert.AreEquivalent(
            InstalledProviderIds,
            providers.Providers.Select(provider => provider.ProviderId).ToArray());
    }

    [TestMethod]
    public async Task LivenessStaysAnonymousSoAProbeNeedsNoAccount()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = fixture.CreateAnonymousClient();

        var live = await client.GetAsync("/health/live");
        var ready = await client.GetAsync("/health/ready");

        Assert.AreEqual(HttpStatusCode.OK, live.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, ready.StatusCode);
    }

    [TestMethod]
    public async Task OidcSignInRoutesStayAnonymousSoTheLoginPageNeedsNoAccount()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = fixture.CreateAnonymousClient();

        // Nobody is signed in yet by definition — both must be reachable
        // without a session. The challenge route 404s (no provider configured
        // by default) rather than redirecting, which itself proves it was
        // reached rather than denied.
        var status = await client.GetAsync("/api/auth/oidc/status");
        var challenge = await client.GetAsync("/api/auth/oidc/challenge");

        Assert.AreEqual(HttpStatusCode.OK, status.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, challenge.StatusCode);
    }

    [TestMethod]
    [DataRow("/api/v1/catalog/search?q=dragon")]
    [DataRow("/api/v1/catalog/candidates/demo/the-hobbit")]
    [DataRow("/api/v1/catalog/works/00000000-0000-0000-0000-000000000001")]
    [DataRow("/api/v1/me/requests")]
    public async Task TheCatalogAndRequestsAreNoLongerAnonymous(string route)
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = fixture.CreateAnonymousClient();

        var response = await client.GetAsync(route);

        // The anonymous development window closed when requests arrived: a request
        // needs a server-verified owner, and searching now sends family search
        // terms to providers on an identified user's behalf.
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode, route);
    }

    [TestMethod]
    public async Task AnAnonymousCallerCannotCreateARequest()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = fixture.CreateAnonymousClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/requests/",
            new CreateBookRequestRequest(Guid.NewGuid(), ["Ebook"], null, false));

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task ARequestMutationWithoutAnAntiforgeryTokenIsRejected()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await fixture.CreateUserClientAsync();

        // Authenticated by cookie and otherwise well formed. Only the token is
        // missing, which is exactly the shape of a cross-site forgery.
        var response = await client.PostAsJsonAsync(
            "/api/v1/requests/",
            new CreateBookRequestRequest(Guid.NewGuid(), ["Ebook"], null, false));

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task EveryResponseCarriesTheBaselineSecurityHeaders()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = fixture.CreateAnonymousClient();

        var response = await client.GetAsync("/health/live");

        // F7: none of these existed at all before. CSP ships report-only (see
        // SecurityHeadersMiddleware's remarks on why), so only its presence is
        // asserted here, not enforcement.
        Assert.AreEqual("nosniff", Single(response, "X-Content-Type-Options"));
        Assert.AreEqual("DENY", Single(response, "X-Frame-Options"));
        Assert.AreEqual("strict-origin-when-cross-origin", Single(response, "Referrer-Policy"));
        StringAssert.Contains(Single(response, "Content-Security-Policy-Report-Only"), "frame-ancestors 'none'");

        static string Single(HttpResponseMessage response, string headerName)
        {
            Assert.IsTrue(response.Headers.TryGetValues(headerName, out var values), $"Missing header {headerName}.");
            return values.Single();
        }
    }

    [TestMethod]
    public async Task TheIdentityCookieIsHttpOnlyAndSameSiteStrict()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = fixture.CreateAnonymousClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest { Email = WebTestFixture.UserEmail, Password = WebTestFixture.UserPassword });
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);

        Assert.IsTrue(response.Headers.TryGetValues("Set-Cookie", out var setCookies));
        var identityCookie = setCookies.FirstOrDefault(cookie =>
            cookie.StartsWith(".AspNetCore.Identity.Application=", StringComparison.Ordinal));
        Assert.IsNotNull(identityCookie, "Sign-in did not set the Identity application cookie.");

        // F10: these used to be Identity's own defaults (SameSite=Lax,
        // SecurePolicy=SameAsRequest — itself only Secure once behind HTTPS),
        // not something this codebase had asserted. Secure is not checked here:
        // the test host is plain HTTP, so SameAsRequest correctly omits it —
        // that omission is the behavior, not a gap in it.
        var lowerCaseCookie = identityCookie.ToLowerInvariant();
        StringAssert.Contains(lowerCaseCookie, "httponly");
        StringAssert.Contains(lowerCaseCookie, "samesite=strict");
    }
}
