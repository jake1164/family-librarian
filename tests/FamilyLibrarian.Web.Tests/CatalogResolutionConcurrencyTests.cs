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
    private sealed class RacingProvider(string id) : IBookMetadataProvider
    {
        public string Id => id;

        public string DisplayName => "Racing test source";

        public Task<BookCandidateSearchPage> SearchAsync(BookSearchQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new BookCandidateSearchPage([], false));

        public async Task<BookCandidate?> GetDetailsAsync(string externalId, CancellationToken cancellationToken)
        {
            await Task.Delay(75, cancellationToken);
            // The shared author and series are new to the catalog; the ISBN is unique per book, except that every "shared-" record
            // carries the one ISBN, whichever provider reports it, so two providers can describe the same book.
            var number = externalId.Replace("book-", string.Empty, StringComparison.Ordinal);
            var isbn = externalId.StartsWith("shared-", StringComparison.Ordinal) ? SharedIsbn : $"978000000{number.PadLeft(4, '0')}";
            return new BookCandidate(
                id, "Racing test source", externalId, $"Racing Title {externalId}",
                // Own author for the shared-ISBN case: the class shares one database, and a name already carried by several authors
                // is deliberately ambiguous, which would hide the follower notice this case asserts on.
                [externalId.StartsWith("shared-", StringComparison.Ordinal) ? "Shared Isbn Author" : "Racing Shared Author"], "Fictional.", null,
                new DateOnly(2020, 1, 1),
                [new BookEditionCandidate($"Racing Title {externalId}", isbn, "Ebook", new DateOnly(2020, 1, 1))],
                [new BookSeriesCandidate("Racing Shared Series", number, true)]);
        }
    }

    private const string SharedIsbn = "9780007700077";

    // Registered provider ids, so the production enablement rule applies: the test host enables "demo" and disables "openlibrary".
    private const string Provider = "demo";
    private const string SecondProvider = "openlibrary";

    private static FamilyLibrarianAppFactory Factory() => new(WebTestFixture.Require(fixture).ConnectionString, services =>
    {
        services.RemoveAll<IBookMetadataProvider>();
        services.AddSingleton<IBookMetadataProvider>(new RacingProvider(Provider));
        services.AddSingleton<IBookMetadataProvider>(new RacingProvider(SecondProvider));
    });

    private static Task<IReadOnlyList<CatalogWorkResolution>> ResolveConcurrentlyAsync(
        FamilyLibrarianAppFactory factory, IEnumerable<string> externalIds) =>
        ResolveConcurrentlyAsync(factory, externalIds.Select(externalId => (Provider, externalId)));

    private static async Task<IReadOnlyList<CatalogWorkResolution>> ResolveConcurrentlyAsync(
        FamilyLibrarianAppFactory factory, IEnumerable<(string ProviderId, string ExternalId)> references)
    {
        using var start = new ManualResetEventSlim();
        var tasks = references.Select(reference => Task.Run(async () =>
        {
            start.Wait();
            await using var scope = factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<CatalogWorkResolver>()
                .ResolveAsync(reference.ProviderId, reference.ExternalId, CancellationToken.None);
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
        Assert.AreEqual(1, await database.ExternalReferences.CountAsync(reference => reference.ProviderId == Provider && reference.ExternalId == "book-1"));
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
        await using (var setup = factory.Services.CreateAsyncScope())
        {
            var database = setup.ServiceProvider.GetRequiredService<AppDbContext>();
            // The second provider is registered but switched off by the test host; enable it the way an administrator would.
            var setting = new FamilyLibrarian.Domain.Providers.ProviderSetting(SecondProvider, DateTimeOffset.UtcNow);
            setting.SetEnabled(true, null, DateTimeOffset.UtcNow);
            database.Add(setting);
            var reader = await database.Users.Where(user => user.Email == WebTestFixture.UserEmail).Select(user => user.Id).SingleAsync();
            // A follower of the shared author, so a duplicate "added to the catalog" notice would be visible.
            var author = new FamilyLibrarian.Domain.Catalog.Author("Shared Isbn Author", null, DateTimeOffset.UtcNow);
            database.Authors.Add(author);
            database.Follows.Add(new FamilyLibrarian.Domain.Following.Follow(
                reader, FamilyLibrarian.Domain.Following.FollowSubjectType.Author, author.Id, DateTimeOffset.UtcNow));
            await database.SaveChangesAsync();
        }

        // Two genuinely different provider ids and external ids, one shared ISBN: the second must attach to the first's Work.
        var results = await ResolveConcurrentlyAsync(factory, [(Provider, "shared-demo-1"), (SecondProvider, "shared-ol-9")]);

        Assert.AreEqual(1, results.Select(result => result.Work.Id).Distinct().Count(), "One Work for the one ISBN.");
        Assert.AreEqual(1, results.Count(result => result.WasCreated), "Exactly one creation.");
        await using var scope = factory.Services.CreateAsyncScope();
        var verify = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.AreEqual(2, await verify.ExternalReferences.CountAsync(reference =>
            reference.ExternalId == "shared-demo-1" || reference.ExternalId == "shared-ol-9"), "Both references point at the one Work.");
        Assert.AreEqual(1, await verify.NotificationEvents.CountAsync(item =>
            item.Category == FamilyLibrarian.Domain.Notifications.NotificationCategories.AuthorNewWorkDetected),
            "The creation notifies the follower once; the attach does not notify a second time.");
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
            using var response = await client.PostAsync("/api/v1/catalog/candidates/demo/book-300/resolve", null);
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
