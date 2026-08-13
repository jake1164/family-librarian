using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Authentication;
using FamilyLibrarian.Domain;
using FamilyLibrarian.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyLibrarian.Web.Tests.Harness;

/// <summary>
/// A booted host plus the clients a test needs to reach it.
/// </summary>
internal sealed class WebTestFixture : IAsyncDisposable
{
    internal const string UserEmail = "reader@family-librarian.example";
    internal const string UserPassword = "Family-Reader-Pass1!";

    /// <summary>The cookie ASP.NET Core Identity issues for an application session.</summary>
    private const string IdentityCookieName = ".AspNetCore.Identity.Application";

    private readonly FamilyLibrarianAppFactory _factory;
    private readonly string _connectionString;

    private WebTestFixture(string connectionString)
    {
        _connectionString = connectionString;
        _factory = new FamilyLibrarianAppFactory(connectionString);
    }

    /// <summary>
    /// Creates a fixture over a new database, or <c>null</c> when no PostgreSQL
    /// container is available.
    /// </summary>
    internal static async Task<WebTestFixture?> CreateAsync()
    {
        if (PostgresFixture.UnavailableReason is not null)
        {
            return null;
        }

        var fixture = new WebTestFixture(await PostgresFixture.CreateMigratedDatabaseAsync());
        await fixture.SeedNonAdminUserAsync();
        return fixture;
    }

    /// <summary>
    /// Returns the fixture, or ends the test as inconclusive when Docker was
    /// unavailable. A skipped test must not read as a passing one.
    /// </summary>
    internal static WebTestFixture Require(WebTestFixture? fixture)
    {
        if (fixture is null)
        {
            Assert.Inconclusive(
                PostgresFixture.UnavailableReason ?? "No PostgreSQL test container is available.");
        }

        return fixture;
    }

    /// <summary>
    /// Boots a second host over the same database, standing in for a Compose
    /// restart. Users and provider settings are already present, so nothing is
    /// re-seeded.
    /// </summary>
    internal WebTestFixture RestartHost() => new(_connectionString);

    internal IServiceProvider Services => _factory.Services;

    /// <summary>
    /// A client with no session, following no redirects so a 401 stays visible.
    /// </summary>
    internal HttpClient CreateAnonymousClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

    internal Task<HttpClient> CreateAdminClientAsync() =>
        CreateSignedInClientAsync(
            FamilyLibrarianAppFactory.AdminEmail,
            FamilyLibrarianAppFactory.AdminPassword);

    internal Task<HttpClient> CreateUserClientAsync() =>
        CreateSignedInClientAsync(UserEmail, UserPassword);

    /// <summary>
    /// Signs in through the real login endpoint so the client carries a genuine
    /// Identity cookie rather than a fabricated principal.
    /// </summary>
    private async Task<HttpClient> CreateSignedInClientAsync(string email, string password)
    {
        var client = CreateAnonymousClient();
        var response = await SignInAsync(client, email, password);

        Assert.AreEqual(
            System.Net.HttpStatusCode.NoContent,
            response.StatusCode,
            $"Test setup could not sign in as {email}.");

        return client;
    }

    private static Task<HttpResponseMessage> SignInAsync(
        HttpClient client,
        string email,
        string password) =>
        client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest { Email = email, Password = password });

    /// <summary>
    /// Signs in and returns the raw Identity session cookie, so a test can replay
    /// it against a different host instance.
    /// </summary>
    internal async Task<string> IssueIdentityCookieAsync(string email, string password)
    {
        using var client = CreateAnonymousClient();
        var response = await SignInAsync(client, email, password);
        Assert.AreEqual(System.Net.HttpStatusCode.NoContent, response.StatusCode);

        Assert.IsTrue(
            response.Headers.TryGetValues("Set-Cookie", out var setCookies),
            "Sign-in did not set a cookie.");

        var identityCookie = setCookies.FirstOrDefault(cookie =>
            cookie.StartsWith(IdentityCookieName + "=", StringComparison.Ordinal));

        Assert.IsNotNull(identityCookie, $"Sign-in did not set {IdentityCookieName}.");

        // Keep only the name=value pair; the attributes after the first ';' are
        // response-side directives a request must not echo back.
        return identityCookie.Split(';', 2)[0];
    }

    /// <summary>
    /// Mints an anti-forgery token for a signed-in client and returns the header
    /// value its paired cookie expects.
    /// </summary>
    internal static async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        var response = await client.GetAsync("/api/v1/antiforgery/token");
        response.EnsureSuccessStatusCode();

        var token = await response.Content.ReadFromJsonAsync<AntiforgeryTokenResponse>();
        Assert.IsNotNull(token);
        return token.Token;
    }

    /// <summary>
    /// Adds an ordinary family member. Program.cs bootstraps the administrator, but
    /// nothing seeds a plain user, and the denial tests need one.
    /// </summary>
    private async Task SeedNonAdminUserAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

        var user = new AppUser
        {
            UserName = UserEmail,
            Email = UserEmail,
            EmailConfirmed = true,
            DisplayName = "Reader",
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        var created = await userManager.CreateAsync(user, UserPassword);
        Assert.IsTrue(
            created.Succeeded,
            $"Test setup could not create the non-admin user: {string.Join(", ", created.Errors.Select(error => error.Description))}");

        var roleAdded = await userManager.AddToRoleAsync(user, RoleNames.User);
        Assert.IsTrue(roleAdded.Succeeded, "Test setup could not assign the User role.");
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();
}
