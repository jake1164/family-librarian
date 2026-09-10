using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Contracts.Authentication;
using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// Covers the search-result availability badge check -- the raw-candidate
/// counterpart to <see cref="WorkFulfillmentOptionsEndpointTests"/>, which
/// requires no persisted Work/WorkId.
/// </summary>
[TestClass]
public sealed class CandidateAvailabilityEndpointTests
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
    public async Task ByDefaultNothingIsReportedAsAvailable()
    {
        // The shared fixture's default fakes (AlwaysEmptyCwaCatalogClient,
        // AlwaysEmptyAudiobookshelfApiClient) always report "not found," and no
        // Work needs to exist for this check to run.
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await fixture.CreateUserClientAsync();
        var userToken = await WebTestFixture.GetAntiforgeryTokenAsync(client);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, userToken);

        var response = await PostAvailabilityAsync(client, "A Book Nobody Owns", ["Some Author"]);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<CandidateAvailabilityResponse>();
        Assert.IsNotNull(body);
        Assert.AreEqual(0, body.Ebook.Count);
        Assert.AreEqual(0, body.Audiobook.Count);
    }

    [TestMethod]
    public async Task AnEbookFoundInCwaIsReportedAsOwnedWithoutResolvingAWork()
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

        var response = await PostAvailabilityAsync(client, "Clear and Present Danger", ["Tom Clancy"]);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<CandidateAvailabilityResponse>();
        Assert.IsNotNull(body);
        Assert.AreEqual(1, body.Ebook.Count);
        Assert.AreEqual("Owned", body.Ebook[0].OptionKind);
        Assert.AreEqual("cwa", body.Ebook[0].ProviderId);
        Assert.AreEqual(0, body.Audiobook.Count);
    }

    [TestMethod]
    public async Task ABlankTitleIsRejected()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await fixture.CreateUserClientAsync();
        var userToken = await WebTestFixture.GetAntiforgeryTokenAsync(client);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, userToken);

        var response = await PostAvailabilityAsync(client, "   ", []);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task ARequestWithoutAnAntiforgeryTokenIsRejected()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await fixture.CreateUserClientAsync();

        var response = await client.PostAsJsonAsync(
            "/api/v1/catalog/availability", new CandidateAvailabilityRequest("Some Title", [], []));

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static Task<HttpResponseMessage> PostAvailabilityAsync(
        HttpClient client, string title, IReadOnlyList<string> authors) =>
        client.PostAsJsonAsync("/api/v1/catalog/availability", new CandidateAvailabilityRequest(title, authors, []));

    private static async Task ConfigureCwaAsync(HttpClient client)
    {
        var settings = await client.PutAsJsonAsync(
            "/api/v1/admin/publishing/cwa/",
            new FamilyLibrarian.Contracts.Publishing.SetCwaSettingsRequest(
                "Local", "/data/cwa-ingest-test", null, null, null, null, "PrivateKey", "https://cwa.example.test", null, null, null));
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
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
    }

    private sealed class DeterministicCatalogClient(string? bookId) : ICwaCatalogClient
    {
        public Task<BookMatchResult> FindBookIdAsync(
            string title, string? author, IReadOnlyCollection<string> isbn13Candidates, CancellationToken cancellationToken) =>
            Task.FromResult(bookId is null
                ? BookMatchResult.NoMatchResult
                : BookMatchResult.Match(new CandidateBook(bookId, title, author)));
    }
}
