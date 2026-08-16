using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Authentication;
using FamilyLibrarian.Web.Tests.Harness;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// Covers the one behavior that makes disabling local sign-in safe: every
/// ordinary account is refused, but the single break-glass bootstrap
/// administrator always still works.
/// </summary>
[TestClass]
public sealed class LocalLoginGateTests
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
    public async Task DisablingLocalLoginRefusesOrdinaryAccountsButNotTheBreakGlassAdmin()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var admin = await fixture.CreateAdminClientAsync();
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(admin);
        admin.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);

        var test = await admin.PostAsync("/api/v1/admin/authentication/oidc/test", content: null);
        Assert.AreEqual(HttpStatusCode.OK, test.StatusCode);

        var disable = await admin.PutAsJsonAsync(
            "/api/v1/admin/authentication/oidc/local-login-disabled", new SetOidcLocalLoginDisabledRequest(true));
        Assert.AreEqual(HttpStatusCode.OK, disable.StatusCode);

        using var anonymous = fixture.CreateAnonymousClient();

        var ordinaryAttempt = await anonymous.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest { Email = WebTestFixture.UserEmail, Password = WebTestFixture.UserPassword });
        Assert.AreEqual(HttpStatusCode.Unauthorized, ordinaryAttempt.StatusCode);

        var breakGlassAttempt = await anonymous.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest
            {
                Email = FamilyLibrarianAppFactory.AdminEmail,
                Password = FamilyLibrarianAppFactory.AdminPassword
            });
        Assert.AreEqual(HttpStatusCode.NoContent, breakGlassAttempt.StatusCode);
    }
}
