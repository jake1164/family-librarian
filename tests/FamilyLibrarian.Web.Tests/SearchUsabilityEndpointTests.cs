using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Contracts.Authentication;
using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Web.Catalog;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Playwright;

namespace FamilyLibrarian.Web.Tests;

[TestClass]
public sealed class SearchUsabilityEndpointTests
{
    private static FamilyLibrarianAppFactory Factory(string connection) => new(connection, services =>
    {
        services.RemoveAll<IBookMetadataProvider>();
        services.AddSingleton<IBookMetadataProvider, VersionProvider>();
        services.AddHostedService<CatalogSearchRunHostedService>();
    });

    [TestMethod]
    public async Task ApiRetainsNovelAndCollectionIdentityAndReRanksContinuation()
    {
        await using var fixture = await WebTestFixture.CreateAsync();
        await using var factory = Factory(WebTestFixture.Require(fixture).ConnectionString);
        using var client = factory.CreateClient();
        Assert.AreEqual(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = WebTestFixture.UserEmail, Password = WebTestFixture.UserPassword })).StatusCode);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(client));
        var started = await StartAsync(client, new("Fahrenheit 451"));
        var first = await WaitAsync(client, started);
        Assert.IsTrue(first.Search.HasMore);
        Assert.IsTrue(first.Search.Results.Any(candidate => candidate.VersionKind == "Collection" && candidate.Title.Contains("Playground", StringComparison.Ordinal)));
        Assert.IsTrue(first.Search.Results.Any(candidate => candidate.VersionKind == "StudyGuide"));
        var secondId = await StartAsync(client, new("Fahrenheit 451", 2, started));
        var second = await WaitAsync(client, secondId);
        Assert.HasCount(3, second.Search.Results);
        Assert.AreEqual("novel", second.Search.Results[0].ExternalId);
        Assert.AreEqual("Novel", second.Search.Results[0].VersionKind);
        Assert.AreEqual("en", second.Search.Results[0].Language);
        Assert.IsFalse(second.Search.HasMore);
        Assert.AreEqual(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/v1/catalog/search/runs", new CatalogSearchRequest("Moby Dick", 2, started))).StatusCode);
    }

    private static async Task<Guid> StartAsync(HttpClient client, CatalogSearchRequest request)
    {
        var response = await client.PostAsJsonAsync("/api/v1/catalog/search/runs", request);
        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CatalogSearchRunStartedResponse>())!.RunId;
    }

    private static async Task<CatalogSearchRunResponse> WaitAsync(HttpClient client, Guid id)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var run = (await client.GetFromJsonAsync<CatalogSearchRunResponse>($"/api/v1/catalog/search/runs/{id}"))!;
            if (run.IsComplete) return run;
            await Task.Delay(20);
        }
        throw new TimeoutException("Search did not complete.");
    }

    [TestMethod]
    public async Task ReaderCanCompareBothNovelsAndRecognizeRelatedVersionsOnDesktopAndMobile()
    {
        if (Environment.GetEnvironmentVariable("FAMILY_LIBRARIAN_SEARCH_BROWSER_TESTS") != "1")
            Assert.Inconclusive("Set FAMILY_LIBRARIAN_SEARCH_BROWSER_TESTS=1 for the isolated search browser regression.");
        await using var fixture = await WebTestFixture.CreateAsync();
        await using var original = Factory(WebTestFixture.Require(fixture).ConnectionString);
        await using var factory = original.WithWebHostBuilder(builder => builder.UseStaticWebAssets());
        factory.UseKestrel(0);
        using var client = factory.CreateClient();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true, ExecutablePath = Environment.GetEnvironmentVariable("FAMILY_LIBRARIAN_E2E_CHROMIUM_EXECUTABLE") });
        var page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = 1440, Height = 1000 } });
        await page.GotoAsync(new Uri(client.BaseAddress!, "/search").ToString());
        await page.GetByLabel("Email", new() { Exact = true }).FillAsync(WebTestFixture.UserEmail);
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync(WebTestFixture.UserPassword);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await page.GetByLabel("Title, author, or ISBN").FillAsync("Fahrenheit 451");
        await page.GetByRole(AriaRole.Button, new() { Name = "Search", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByText("Collection or combined volume", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText("Study guide or criticism", new() { Exact = true })).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Load more results", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator("a.search-result-title").First).ToHaveAttributeAsync("href", "search/demo/novel");
        var output = Environment.GetEnvironmentVariable("FAMILY_LIBRARIAN_SEARCH_SCREENSHOTS");
        if (!string.IsNullOrWhiteSpace(output)) await page.ScreenshotAsync(new() { Path = Path.Combine(output, "fahrenheit-desktop.png"), FullPage = true });
        await page.Locator("a[href='search/demo/guide']").ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Send version request for review", Exact = true })).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "Back to search", Exact = true }).ClickAsync();
        await page.GetByLabel("Title, author, or ISBN").FillAsync("Moby Dick");
        await page.GetByRole(AriaRole.Button, new() { Name = "Search", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByText("Graphic adaptation", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText("Retelling or adaptation", new() { Exact = true })).ToBeVisibleAsync();
        await page.SetViewportSizeAsync(390, 844);
        await Assertions.Expect(page.Locator("a.search-result-title").First).ToHaveAttributeAsync("href", "search/demo/moby-novel");
        Assert.IsFalse(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth > window.innerWidth"));
        if (!string.IsNullOrWhiteSpace(output)) await page.ScreenshotAsync(new() { Path = Path.Combine(output, "moby-mobile.png"), FullPage = true });
    }

    private sealed class VersionProvider : IBookMetadataProvider
    {
        public string Id => "demo";
        public string DisplayName => "Test catalog";
        private static BookCandidate Book(string id, string title, string author, string? description) => new("demo", "Test catalog", id, title, [author], description, null, null, [], [], Language: "en");
        private static BookCandidate[] Fahrenheit =>
        [
            Book("guide", "Fahrenheit 451", "Samuel J. Umland", null) with { Publisher = "Cliffs Notes", Editions = [new("Notes on Bradbury's Fahrenheit 451", "9780822004585", "Paperback", null)] },
            Book("collection", "Fahrenheit 451", "Ray Bradbury", "Contains: The Playground and And the Rock Cried Out.") with { WorkTitle = "Fahrenheit 451 / Playground / Rock Cried Out" },
            Book("novel", "Fahrenheit 451", "Ray Bradbury", "A dystopian novel.")
        ];
        private static BookCandidate[] Moby =>
        [
            Book("moby-comic", "Moby Dick", "Herman Melville", "Presented in comic book format."),
            Book("moby-retelling", "Moby Dick", "Herman Melville", "Retells the story of Captain Ahab."),
            Book("moby-novel", "Moby Dick", "Herman Melville", "A novel about Captain Ahab.") with { Authors = ["Herman Melville", "Nigel Cliff"] }
        ];
        public Task<BookCandidateSearchPage> SearchAsync(BookSearchQuery query, CancellationToken cancellationToken) => Task.FromResult(
            query.Text.Contains("Moby", StringComparison.OrdinalIgnoreCase) ? new BookCandidateSearchPage(Moby, false) :
            new BookCandidateSearchPage(query.Page == 1 ? Fahrenheit[..2] : Fahrenheit[2..], query.Page == 1));
        public Task<BookCandidate?> GetDetailsAsync(string externalId, CancellationToken cancellationToken) =>
            Task.FromResult(Fahrenheit.Concat(Moby).FirstOrDefault(candidate => candidate.ExternalId == externalId));
    }
}
