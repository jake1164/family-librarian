using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Contracts.Authentication;
using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Web.Tests.Harness;
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

        var details = await client.GetAsync("/api/v1/catalog/candidates/demo/the-hobbit");
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, details.StatusCode);
        var problem = await details.Content.ReadFromJsonAsync<ProblemDetailsPayload>();
        Assert.IsNotNull(problem);
        Assert.AreEqual("Catalog provider unavailable", problem.Title);
        Assert.AreEqual("The selected catalog source is temporarily unavailable.", problem.Detail);
    }

    private sealed class FailingDemoProvider : IBookMetadataProvider
    {
        public string Id => "demo";

        public string DisplayName => "Family Librarian sample catalog";

        public Task<IReadOnlyList<BookCandidate>> SearchAsync(
            BookSearchQuery query,
            CancellationToken cancellationToken) =>
            Task.FromException<IReadOnlyList<BookCandidate>>(
                new HttpRequestException("The upstream service is unavailable."));

        public Task<BookCandidate?> GetDetailsAsync(
            string externalId,
            CancellationToken cancellationToken) =>
            Task.FromException<BookCandidate?>(
                new HttpRequestException("The upstream service is unavailable."));
    }

    private sealed record ProblemDetailsPayload(string? Title, string? Detail);
}
