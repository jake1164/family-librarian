using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Contracts.Authentication;
using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Web.Tests.Harness;
using FamilyLibrarian.Web.Catalog;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// Verifies a failing metadata provider cannot take down a search or disclose
/// its implementation detail through a candidate endpoint.
/// </summary>
[TestClass]
public sealed class ProviderFailureEndpointTests
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
    public async Task SearchReportsAFailedProviderWhileCandidateDetailsReturnASafe503()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = new FamilyLibrarianAppFactory(
            fixture.ConnectionString,
            services =>
            {
                services.RemoveAll<IBookMetadataProvider>();
                services.AddSingleton<IBookMetadataProvider, FailingDemoProvider>();
                services.AddHostedService<CatalogSearchRunHostedService>();
            });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var signIn = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest
            {
                Email = WebTestFixture.UserEmail,
                Password = WebTestFixture.UserPassword
            });
        Assert.AreEqual(HttpStatusCode.NoContent, signIn.StatusCode);

        var search = await client.GetFromJsonAsync<CatalogSearchResponse>(
            "/api/v1/catalog/search?q=the%20hobbit");
        Assert.IsNotNull(search);
        Assert.HasCount(0, search.Results);
        Assert.HasCount(1, search.Providers);
        Assert.AreEqual("demo", search.Providers[0].ProviderId);
        Assert.IsFalse(search.Providers[0].Succeeded);

        client.DefaultRequestHeaders.Add(
            FamilyLibrarian.Web.AntiforgeryTokenEndpoint.HeaderName,
            await WebTestFixture.GetAntiforgeryTokenAsync(client));
        var start = await client.PostAsJsonAsync("/api/v1/catalog/search/runs", new CatalogSearchRequest("the hobbit"));
        Assert.AreEqual(HttpStatusCode.Accepted, start.StatusCode);
        var started = await start.Content.ReadFromJsonAsync<CatalogSearchRunStartedResponse>();
        Assert.IsNotNull(started);
        CatalogSearchRunResponse? run = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            run = await client.GetFromJsonAsync<CatalogSearchRunResponse>($"/api/v1/catalog/search/runs/{started.RunId}");
            if (run?.IsComplete == true) break;
            await Task.Delay(20);
        }
        Assert.IsNotNull(run);
        Assert.IsTrue(run.IsComplete);
        Assert.HasCount(1, run.Search.Providers);
        Assert.IsFalse(run.Search.Providers[0].Succeeded);

        var details = await client.GetAsync("/api/v1/catalog/candidates/demo/the-hobbit");
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, details.StatusCode);
        var problem = await details.Content.ReadFromJsonAsync<ProblemDetailsPayload>();
        Assert.IsNotNull(problem);
        Assert.AreEqual("Catalog provider unavailable", problem.Title);
        Assert.AreEqual("The selected catalog source is temporarily unavailable.", problem.Detail);
    }

    [TestMethod]
    public async Task ProgressiveSearchPublishesFastProviderBeforeSlowProviderCompletes()
    {
        var fixture = WebTestFixture.Require(_fixture);
        var slowProvider = new SlowDemoProvider();
        await using var factory = new FamilyLibrarianAppFactory(
            fixture.ConnectionString,
            services =>
            {
                services.RemoveAll<IBookMetadataProvider>();
                services.AddSingleton<IBookMetadataProvider, FastDemoProvider>();
                services.AddSingleton<IBookMetadataProvider>(slowProvider);
                services.AddHostedService<CatalogSearchRunHostedService>();
            });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var signIn = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = WebTestFixture.UserEmail,
            Password = WebTestFixture.UserPassword
        });
        Assert.AreEqual(HttpStatusCode.NoContent, signIn.StatusCode);
        client.DefaultRequestHeaders.Add(
            FamilyLibrarian.Web.AntiforgeryTokenEndpoint.HeaderName,
            await WebTestFixture.GetAntiforgeryTokenAsync(client));

        var start = await client.PostAsJsonAsync("/api/v1/catalog/search/runs", new CatalogSearchRequest("fast novel"));
        Assert.AreEqual(HttpStatusCode.Accepted, start.StatusCode);
        var started = await start.Content.ReadFromJsonAsync<CatalogSearchRunStartedResponse>();
        Assert.IsNotNull(started);
        CatalogSearchRunResponse? run = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            run = await client.GetFromJsonAsync<CatalogSearchRunResponse>($"/api/v1/catalog/search/runs/{started.RunId}");
            if (run?.Search.Results.Count > 0) break;
            await Task.Delay(20);
        }

        Assert.IsNotNull(run);
        Assert.IsFalse(run.IsComplete, "The slow provider should still be pending.");
        Assert.AreEqual("Fast Novel", run.Search.Results[0].Title);
        using var otherUser = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var otherSignIn = await otherUser.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = FamilyLibrarianAppFactory.AdminEmail,
            Password = FamilyLibrarianAppFactory.AdminPassword
        });
        Assert.AreEqual(HttpStatusCode.NoContent, otherSignIn.StatusCode);
        otherUser.DefaultRequestHeaders.Add(
            FamilyLibrarian.Web.AntiforgeryTokenEndpoint.HeaderName,
            await WebTestFixture.GetAntiforgeryTokenAsync(otherUser));
        Assert.AreEqual(HttpStatusCode.NotFound, (await otherUser.GetAsync($"/api/v1/catalog/search/runs/{started.RunId}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await otherUser.DeleteAsync($"/api/v1/catalog/search/runs/{started.RunId}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/catalog/search/runs/{started.RunId}")).StatusCode);
    }

    private sealed class FailingDemoProvider : IBookMetadataProvider
    {
        public string Id => "demo";

        public string DisplayName => "Family Librarian sample catalog";

        public Task<BookCandidateSearchPage> SearchAsync(
            BookSearchQuery query,
            CancellationToken cancellationToken) =>
            Task.FromException<BookCandidateSearchPage>(
                new HttpRequestException("The upstream service is unavailable."));

        public Task<BookCandidate?> GetDetailsAsync(
            string externalId,
            CancellationToken cancellationToken) =>
            Task.FromException<BookCandidate?>(
                new HttpRequestException("The upstream service is unavailable."));
    }

    private sealed class FastDemoProvider : IBookMetadataProvider
    {
        public string Id => "demo";
        public string DisplayName => "Fast provider";
        public Task<BookCandidateSearchPage> SearchAsync(BookSearchQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new BookCandidateSearchPage([
                new BookCandidate("demo", DisplayName, "fast", "Fast Novel", ["A. Author"], null, null, null, [], [])
            ], false));
        public Task<BookCandidate?> GetDetailsAsync(string externalId, CancellationToken cancellationToken) => Task.FromResult<BookCandidate?>(null);
    }

    private sealed class SlowDemoProvider : IBookMetadataProvider
    {
        public string Id => "demo";
        public string DisplayName => "Slow provider";
        public async Task<BookCandidateSearchPage> SearchAsync(BookSearchQuery query, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return new BookCandidateSearchPage([], false);
        }
        public Task<BookCandidate?> GetDetailsAsync(string externalId, CancellationToken cancellationToken) => Task.FromResult<BookCandidate?>(null);
    }

    private sealed record ProblemDetailsPayload(string? Title, string? Detail);
}
