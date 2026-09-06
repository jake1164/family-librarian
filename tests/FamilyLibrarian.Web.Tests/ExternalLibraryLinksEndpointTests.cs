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

    private static async Task ConfigureAndEnableCwaAsync(HttpClient client)
    {
        var settings = await client.PutAsJsonAsync(
            "/api/v1/admin/publishing/cwa/",
            new SetCwaSettingsRequest(
                "Local", "/data/cwa-ingest-test", null, null, null, null, "PrivateKey", "https://cwa.example.test", null));
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
