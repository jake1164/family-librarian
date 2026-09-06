using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Authentication;
using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Contracts.Publishing;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FamilyLibrarian.Web.Tests;

/// <summary>Covers the plain "open the library" links surfaced to any signed-in user in the nav.</summary>
[TestClass]
public sealed class ExternalLibraryLinksEndpointTests
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
    public async Task ByDefaultNeitherDestinationHasALink()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await fixture.CreateUserClientAsync();

        var response = await client.GetFromJsonAsync<ExternalLibraryLinksResponse>(
            "/api/v1/catalog/external-library-links");

        Assert.IsNotNull(response);
        Assert.IsNull(response.CwaUrl);
        Assert.IsNull(response.AudiobookshelfUrl);
    }

    [TestMethod]
    public async Task ARegularUserSeesTheCwaLinkOnceAnAdminEnablesIt()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = new FamilyLibrarianAppFactory(fixture.ConnectionString);
        using var userClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await SignInAsync(userClient, WebTestFixture.UserEmail, WebTestFixture.UserPassword);

        using var adminClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await SignInAsync(adminClient, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword);
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(adminClient);
        adminClient.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);
        await ConfigureAndEnableCwaAsync(adminClient);

        var response = await userClient.GetFromJsonAsync<ExternalLibraryLinksResponse>(
            "/api/v1/catalog/external-library-links");

        Assert.IsNotNull(response);
        Assert.AreEqual("https://cwa.example.test", response.CwaUrl);
        Assert.IsNull(response.AudiobookshelfUrl);
    }

    /// <summary>
    /// The connection URL (what Family Librarian's own backend uses to reach
    /// CWA) is routinely a Docker-internal hostname a family member's browser
    /// cannot resolve. Public URL exists precisely so the nav link a family
    /// member clicks does not have to be that address.
    /// </summary>
    [TestMethod]
    public async Task ARegularUserSeesThePublicUrlInsteadOfTheConnectionUrlWhenConfigured()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = new FamilyLibrarianAppFactory(fixture.ConnectionString);
        using var userClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await SignInAsync(userClient, WebTestFixture.UserEmail, WebTestFixture.UserPassword);

        using var adminClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await SignInAsync(adminClient, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword);
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(adminClient);
        adminClient.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);
        await ConfigureAndEnableCwaAsync(adminClient, publicUrl: "https://library.example.net");

        var response = await userClient.GetFromJsonAsync<ExternalLibraryLinksResponse>(
            "/api/v1/catalog/external-library-links");

        Assert.IsNotNull(response);
        Assert.AreEqual("https://library.example.net", response.CwaUrl);
    }

    private static Task ConfigureAndEnableCwaAsync(HttpClient client) =>
        ConfigureAndEnableCwaAsync(client, publicUrl: null);

    private static async Task ConfigureAndEnableCwaAsync(HttpClient client, string? publicUrl)
    {
        var settings = await client.PutAsJsonAsync(
            "/api/v1/admin/publishing/cwa/",
            new SetCwaSettingsRequest(
                "Local", "/data/cwa-ingest-test", null, null, null, null, "PrivateKey",
                "https://cwa.example.test", publicUrl, null));
        settings.EnsureSuccessStatusCode();

        var test = await client.PostAsJsonAsync("/api/v1/admin/publishing/cwa/test", new { });
        test.EnsureSuccessStatusCode();

        var enabled = await client.PutAsJsonAsync(
            "/api/v1/admin/publishing/cwa/enabled",
            new SetPublishingEnabledRequest(true));
        enabled.EnsureSuccessStatusCode();
    }

    private static async Task SignInAsync(HttpClient client, string email, string password)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = email, Password = password });
        Assert.AreEqual(System.Net.HttpStatusCode.NoContent, response.StatusCode);
    }
}
