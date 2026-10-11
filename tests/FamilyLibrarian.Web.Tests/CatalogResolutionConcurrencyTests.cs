using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Contracts.Authentication;
using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// Regression for concurrent first-time resolution of catalog candidates: several callers resolving the same new book, or
/// different books sharing a new author or series, used to collide on the catalog's unique indexes and fail one caller.
/// </summary>
[TestClass]
public sealed class CatalogResolutionConcurrencyTests
{
    private static WebTestFixture? fixture;

    [ClassInitialize]
    public static async Task InitializeAsync(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        fixture = await WebTestFixture.CreateAsync();
    }

    [ClassCleanup]
    public static async Task CleanupAsync()
    {
        if (fixture is not null) await fixture.DisposeAsync();
    }

    /// <summary>Slow on purpose so concurrent callers genuinely overlap before any of them has written.</summary>
    private sealed class RacingProvider : IBookMetadataProvider
    {
        public string Id => "racer";

        public string DisplayName => "Racing test source";

        public Task<BookCandidateSearchPage> SearchAsync(BookSearchQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new BookCandidateSearchPage([], false));

        public async Task<BookCandidate?> GetDetailsAsync(string externalId, CancellationToken cancellationToken)
        {
            await Task.Delay(75, cancellationToken);
            // The shared author and series are new to the catalog; the ISBN is unique per book.
            var number = externalId.Replace("book-", string.Empty, StringComparison.Ordinal);
            return new BookCandidate(
                "racer", "Racing test source", externalId, $"Racing Title {externalId}", ["Racing Shared Author"], "Fictional.", null,
                new DateOnly(2020, 1, 1),
                [new BookEditionCandidate($"Racing Title {externalId}", $"978000000{number.PadLeft(4, '0')}", "Ebook", new DateOnly(2020, 1, 1))],
                [new BookSeriesCandidate("Racing Shared Series", number, true)]);
        }
    }

    private static FamilyLibrarianAppFactory Factory() => new(WebTestFixture.Require(fixture).ConnectionString, services =>
    {
        services.RemoveAll<IBookMetadataProvider>();
        services.AddSingleton<IBookMetadataProvider, RacingProvider>();
    });

    private static async Task<IReadOnlyList<CatalogWorkResolution>> ResolveConcurrentlyAsync(
        FamilyLibrarianAppFactory factory, IEnumerable<string> externalIds)
    {
        using var start = new ManualResetEventSlim();
        var tasks = externalIds.Select(externalId => Task.Run(async () =>
        {
            start.Wait();
            await using var scope = factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<CatalogWorkResolver>().ResolveAsync("racer", externalId, CancellationToken.None);
        })).ToArray();
        start.Set();
        return await Task.WhenAll(tasks);
    }

    [TestMethod]
    public async Task ManyCallersResolvingTheSameNewBookGetOneWorkAndOneCreation()
    {
        await using var factory = Factory();

        var results = await ResolveConcurrentlyAsync(factory, Enumerable.Repeat("book-1", 12));

        Assert.AreEqual(1, results.Select(result => result.Work.Id).Distinct().Count(), "Everyone got the same work.");
        Assert.AreEqual(1, results.Count(result => result.WasCreated), "Exactly one caller created it.");
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.AreEqual(1, await database.ExternalReferences.CountAsync(reference => reference.ProviderId == "racer" && reference.ExternalId == "book-1"));
    }

    [TestMethod]
    public async Task DifferentNewBooksSharingANewAuthorAndSeriesAllResolveWithoutCollision()
    {
        await using var factory = Factory();

        var results = await ResolveConcurrentlyAsync(factory, Enumerable.Range(10, 10).Select(number => $"book-{number}"));

        Assert.AreEqual(10, results.Select(result => result.Work.Id).Distinct().Count());
        Assert.IsTrue(results.All(result => result.WasCreated));
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.AreEqual(1, await database.Authors.CountAsync(author => author.NormalizedName == "racing shared author"), "One shared author.");
        Assert.AreEqual(1, await database.Series.CountAsync(series => series.NormalizedName == "racing shared series"), "One shared series.");
        var series = await database.Series.Include(item => item.Entries).SingleAsync(item => item.NormalizedName == "racing shared series");
        Assert.AreEqual(10, series.Entries.Count(entry => entry.PositionSort is >= 10 and <= 19), "Every concurrently created book joined the shared series.");
    }

    [TestMethod]
    public async Task TheSameBookReachedByTwoSourcesAtOnceCollapsesToOneWorkByIsbn()
    {
        await using var factory = Factory();

        // Two different provider ids for one ISBN: the second must attach to the first's work, not create a duplicate.
        var results = await ResolveConcurrentlyAsync(factory, ["book-77", "book-77"]);

        Assert.AreEqual(1, results.Select(result => result.Work.Id).Distinct().Count());
    }

    [TestMethod]
    public async Task TheHttpResolveEndpointSurvivesConcurrentFirstTimeCalls()
    {
        await using var factory = Factory();
        var clients = new List<HttpClient>();
        for (var index = 0; index < 8; index++)
        {
            var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            using var login = await client.PostAsJsonAsync("/api/auth/login",
                new LoginRequest { Email = WebTestFixture.UserEmail, Password = WebTestFixture.UserPassword });
            Assert.AreEqual(HttpStatusCode.NoContent, login.StatusCode);
            client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(client));
            clients.Add(client);
        }

        using var start = new ManualResetEventSlim();
        var calls = clients.Select(client => Task.Run(async () =>
        {
            start.Wait();
            using var response = await client.PostAsync("/api/v1/catalog/candidates/racer/book-300/resolve", null);
            return (response.StatusCode, Work: response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<CatalogWorkResponse>() : null);
        })).ToArray();
        start.Set();
        var outcomes = await Task.WhenAll(calls);
        foreach (var client in clients) client.Dispose();

        Assert.IsTrue(outcomes.All(outcome => outcome.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created),
            string.Join(",", outcomes.Select(outcome => outcome.StatusCode)));
        Assert.AreEqual(1, outcomes.Select(outcome => outcome.Work!.Id).Distinct().Count());
        Assert.AreEqual(1, outcomes.Count(outcome => outcome.StatusCode == HttpStatusCode.Created));
    }
}
