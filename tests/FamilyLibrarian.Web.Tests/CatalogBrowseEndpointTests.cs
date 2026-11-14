// One-shot expected-value arrays in assertions are clearer inline than as static fields; CA1861 targets repeated calls.
#pragma warning disable CA1861
using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Contracts.Authentication;
using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Contracts.Providers;
using FamilyLibrarian.Domain.Catalog;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Catalog;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// Real authenticated HTTP against a migrated PostgreSQL database: the saved-catalog projections, the owner-private
/// temporary previews, the provider-call budget and the proof that browsing writes nothing.
/// </summary>
[TestClass]
public sealed class CatalogBrowseEndpointTests
{
    private const string Base = "/api/v1/catalog/browse";
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
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

    // ---------------------------------------------------------------- test doubles

    /// <summary>Counts every call a metadata provider receives so a test can prove a browse made none.</summary>
    private sealed class ProviderSpy
    {
        private int searches;
        private int details;

        public int Searches => Volatile.Read(ref searches);

        public int Details => Volatile.Read(ref details);

        public volatile bool Offline;

        public void CountSearch() => Interlocked.Increment(ref searches);

        public void CountDetails() => Interlocked.Increment(ref details);
    }

    private sealed class ScriptedProvider(
        string id, string name, BookCandidate[] books, ProviderSpy spy, SemaphoreSlim? gate = null) : IBookMetadataProvider
    {
        public string Id => id;

        public string DisplayName => name;

        public async Task<BookCandidateSearchPage> SearchAsync(BookSearchQuery query, CancellationToken cancellationToken)
        {
            spy.CountSearch();
            if (gate is not null) await gate.WaitAsync(cancellationToken);
            return new BookCandidateSearchPage(books, false);
        }

        public Task<BookCandidate?> GetDetailsAsync(string externalId, CancellationToken cancellationToken)
        {
            spy.CountDetails();
            if (spy.Offline) throw new HttpRequestException("The source is down.");
            return Task.FromResult(books.FirstOrDefault(book => book.ExternalId == externalId));
        }
    }

    private sealed class PinnedClock(DateTimeOffset start) : IClock
    {
        private long ticks = start.UtcTicks;

        public DateTimeOffset UtcNow => new(Interlocked.Read(ref ticks), TimeSpan.Zero);

        public void Advance(TimeSpan by) => Interlocked.Add(ref ticks, by.Ticks);
    }

    /// <summary>Records the SQL EF actually sends, so a test can prove a bound is in the database query itself.</summary>
    private sealed class SqlCapture : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            lock (Commands) Commands.Add(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            lock (Commands) Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private static BookCandidate Book(
        string provider, string external, string title, string[] authors, params BookSeriesCandidate[] series) =>
        new(provider, provider == "demo" ? "Demo Source" : "Second Source", external, title, authors, "Not cached.", null, null, [], series);

    private static FamilyLibrarianAppFactory Factory(
        ProviderSpy spy, IBookMetadataProvider[] providers, IClock? clock = null, string? connection = null,
        Action<IServiceCollection>? configure = null) =>
        new(connection ?? WebTestFixture.Require(fixture).ConnectionString, services =>
        {
            services.RemoveAll<IBookMetadataProvider>();
            foreach (var provider in providers) services.AddSingleton(provider);
            services.AddHostedService<CatalogSearchRunHostedService>();
            services.AddSingleton(spy);
            if (clock is not null)
            {
                services.RemoveAll<IClock>();
                services.AddSingleton(clock);
            }

            configure?.Invoke(services);
        });

    private static FamilyLibrarianAppFactory FactoryFor(
        ProviderSpy spy, BookCandidate[] demo, BookCandidate[]? second = null, SemaphoreSlim? secondGate = null, IClock? clock = null,
        string? connection = null, Action<IServiceCollection>? configure = null)
    {
        var providers = new List<IBookMetadataProvider> { new ScriptedProvider("demo", "Demo Source", demo, spy) };
        if (second is not null) providers.Add(new ScriptedProvider("gutendex", "Second Source", second, spy, secondGate));
        return Factory(spy, providers.ToArray(), clock, connection, configure);
    }

    private static async Task<HttpClient> SignedInAsync(
        FamilyLibrarianAppFactory factory, string email = WebTestFixture.UserEmail, string password = WebTestFixture.UserPassword)
    {
        var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = email, Password = password });
        Assert.AreEqual(HttpStatusCode.NoContent, login.StatusCode);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(client));
        return client;
    }

    private static string Unique(string stem) => $"{stem} {Guid.NewGuid():N}";

    private static async Task<T> GetAsync<T>(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, $"{url}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    // ---------------------------------------------------------------- catalog seeding

    private sealed record SeededWork(Guid Id, string Title);

    private static async Task<T> WithDatabaseAsync<T>(FamilyLibrarianAppFactory factory, Func<AppDbContext, Task<T>> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var result = await action(database);
        await database.SaveChangesAsync();
        return result;
    }

    private static Work NewWork(AppDbContext database, string title, bool retired = false, DateOnly? published = null)
    {
        var work = new Work(title, null, null, published, PublicationStatus.Published, DateTimeOffset.UtcNow);
        if (retired) work.Retire(DateTimeOffset.UtcNow);
        database.Works.Add(work);
        return work;
    }

    private static void Join(Work work, Series series, string? label, decimal? sort, bool primary = true) =>
        work.AddSeriesEntry(new SeriesEntry(series, work, label, sort, primary, DateTimeOffset.UtcNow));

    // ---------------------------------------------------------------- saved author

    [TestMethod]
    public async Task SavedAuthorShowsKnownWorksGroupedBySeriesWithRolesMembershipsAndRetiredWorksExcluded()
    {
        var spy = new ProviderSpy();
        await using var factory = FactoryFor(spy, []);
        var name = Unique("Ada Saved");
        var sagaName = Unique("Saved Saga");
        var secondName = Unique("Second Saved Saga");
        var ids = await WithDatabaseAsync(factory, async database =>
        {
            var author = new Author(name, null, DateTimeOffset.UtcNow);
            var illustrator = new Author(Unique("Ivy Illustrator"), null, DateTimeOffset.UtcNow);
            var saga = new Series(sagaName, SeriesStatus.Active, DateTimeOffset.UtcNow);
            var second = new Series(secondName, SeriesStatus.Unknown, DateTimeOffset.UtcNow);
            database.Authors.AddRange(author, illustrator);
            database.Series.AddRange(saga, second);

            var one = NewWork(database, "Orbit One", published: new DateOnly(2001, 5, 6));
            one.AddAuthor(author, 0, "Author");
            one.AddAuthor(illustrator, 1, "Illustrator");
            Join(one, saga, "1", 1m);
            var half = NewWork(database, "Orbit Two Point Five");
            half.AddAuthor(author, 0);
            Join(half, saga, "2.5", 2.5m);
            var threeB = NewWork(database, "Orbit Three B");
            threeB.AddAuthor(author, 0);
            Join(threeB, saga, "3", 3m);
            var threeA = NewWork(database, "Orbit Three A");
            threeA.AddAuthor(author, 0);
            Join(threeA, saga, "3", 3m);
            var multi = NewWork(database, "Crossover");
            multi.AddAuthor(author, 0);
            Join(multi, saga, "4", 4m);
            Join(multi, second, "7 (reading order)", null, primary: false);
            var companion = NewWork(database, "Companion Guide");
            companion.AddAuthor(author, 0);
            Join(companion, saga, null, null);
            var standalone = NewWork(database, "Standalone Tale");
            standalone.AddAuthor(author, 0);
            var retired = NewWork(database, "Retired Tale", retired: true);
            retired.AddAuthor(author, 0);
            database.ExternalReferences.Add(new ExternalReference("demo", ExternalReferenceEntityType.Work, one.Id, "demo-one", new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)));
            await Task.CompletedTask;
            return (Author: author.Id, Saga: saga.Id, Second: second.Id, Illustrator: illustrator.Id, One: one.Id, Multi: multi.Id, Standalone: standalone.Id, Retired: retired.Id);
        });
        using var client = await SignedInAsync(factory);

        var page = await GetAsync<CatalogAuthorBrowseResponse>(client, $"{Base}/authors/{ids.Author}");

        Assert.AreEqual(name, page.Name);
        Assert.AreEqual(1, page.Page);
        Assert.IsFalse(page.HasMore);
        CollectionAssert.IsSubsetOf(new[] { "known-local-only", "grouping-page-local" }, page.Limitations.ToArray());
        Assert.HasCount(2, page.Groups);
        var saga = page.Groups.Single(group => group.SeriesId == ids.Saga);
        Assert.AreEqual(sagaName, saga.Name);
        CollectionAssert.AreEqual(
            new[] { "Orbit One", "Orbit Two Point Five", "Orbit Three A", "Orbit Three B", "Crossover", "Companion Guide" },
            saga.Entries.Select(entry => entry.Book.Title).ToArray(),
            "1, 2.5, tied 3s by title, 4, unnumbered last.");
        CollectionAssert.AreEqual(new decimal?[] { 1m, 2.5m, 3m, 3m, 4m, null }, saga.Entries.Select(entry => entry.Position).ToArray());
        var secondGroup = page.Groups.Single(group => group.SeriesId == ids.Second);
        var crossover = secondGroup.Entries.Single();
        Assert.AreEqual("7 (reading order)", crossover.PositionLabel, "The full ordering label is kept verbatim.");
        Assert.IsNull(crossover.Position, "No number is invented for a qualified label.");
        Assert.HasCount(2, crossover.Book.Memberships, "Multiple memberships survive.");
        CollectionAssert.AreEqual(new[] { "Standalone Tale" }, page.NoKnownSeries.Select(entry => entry.Book.Title).ToArray());
        var allTitles = page.Groups.SelectMany(group => group.Entries).Concat(page.NoKnownSeries).Select(entry => entry.Book.Title).ToArray();
        CollectionAssert.DoesNotContain(allTitles, "Retired Tale");

        var one = saga.Entries[0].Book;
        Assert.AreEqual(ids.One, one.WorkId);
        Assert.AreEqual(new DateOnly(2001, 5, 6), one.PublicationDate);
        Assert.AreEqual("Author", one.Credits[0].Role);
        Assert.AreEqual("Illustrator", one.Credits[1].Role);
        Assert.AreEqual(ids.Illustrator, one.Credits[1].AuthorId);
        Assert.IsNull(saga.Entries[1].Book.Credits[0].Role, "A role is shown only where supplied.");
        Assert.AreEqual("demo", one.Sources.Single().ProviderId);
        Assert.AreEqual(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), one.Sources.Single().ObservedAtUtc);
        Assert.AreEqual(0, spy.Searches + spy.Details);
    }

    [TestMethod]
    public async Task AnAuthorWithNoKnownWorksSucceedsEmptyAndAMissingAuthorOrSeriesIsNotFound()
    {
        await using var factory = FactoryFor(new ProviderSpy(), []);
        var emptyAuthor = await WithDatabaseAsync(factory, database =>
        {
            var author = new Author(Unique("Lonely"), null, DateTimeOffset.UtcNow);
            var series = new Series(Unique("Hollow"), SeriesStatus.Unknown, DateTimeOffset.UtcNow);
            database.Authors.Add(author);
            database.Series.Add(series);
            return Task.FromResult((Author: author.Id, Series: series.Id));
        });
        using var client = await SignedInAsync(factory);

        var author = await GetAsync<CatalogAuthorBrowseResponse>(client, $"{Base}/authors/{emptyAuthor.Author}");
        var series = await GetAsync<CatalogSeriesBrowseResponse>(client, $"{Base}/series/{emptyAuthor.Series}");

        Assert.IsEmpty(author.Groups);
        Assert.IsEmpty(author.NoKnownSeries);
        Assert.IsFalse(author.HasMore);
        Assert.IsEmpty(series.Entries);
        Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync($"{Base}/authors/{Guid.NewGuid()}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync($"{Base}/series/{Guid.NewGuid()}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync($"{Base}/authors/{emptyAuthor.Series}")).StatusCode, "A series id is not an author id.");
    }

    [TestMethod]
    public async Task AuthorPagingIsFiftyStableDistinctWorksWithHasMoreFromOneExtraRowAndTiesKeepAStableOrder()
    {
        await using var factory = FactoryFor(new ProviderSpy(), []);
        var ids = await WithDatabaseAsync(factory, database =>
        {
            var big = new Author(Unique("Prolific"), null, DateTimeOffset.UtcNow);
            var exact = new Author(Unique("Exactly Fifty"), null, DateTimeOffset.UtcNow);
            database.Authors.AddRange(big, exact);
            for (var index = 0; index < 50; index++) NewWork(database, $"Book {index:00}").AddAuthor(big, 0);
            // Two works with an identical title: only the id can order them, and it must do so identically every time.
            NewWork(database, "Zed Twin").AddAuthor(big, 0);
            NewWork(database, "Zed Twin").AddAuthor(big, 0);
            for (var index = 0; index < 50; index++) NewWork(database, $"Exact {index:00}").AddAuthor(exact, 0);
            return Task.FromResult((Big: big.Id, Exact: exact.Id));
        });
        using var client = await SignedInAsync(factory);

        var first = await GetAsync<CatalogAuthorBrowseResponse>(client, $"{Base}/authors/{ids.Big}");
        var second = await GetAsync<CatalogAuthorBrowseResponse>(client, $"{Base}/authors/{ids.Big}?page=2");
        var third = await GetAsync<CatalogAuthorBrowseResponse>(client, $"{Base}/authors/{ids.Big}?page=3");
        var repeat = await GetAsync<CatalogAuthorBrowseResponse>(client, $"{Base}/authors/{ids.Big}?page=2");

        Assert.HasCount(50, first.NoKnownSeries);
        Assert.IsTrue(first.HasMore);
        Assert.HasCount(2, second.NoKnownSeries);
        Assert.IsFalse(second.HasMore);
        Assert.IsEmpty(third.NoKnownSeries);
        Assert.IsFalse(third.HasMore);
        var all = first.NoKnownSeries.Concat(second.NoKnownSeries).Select(entry => entry.Book.WorkId).ToArray();
        Assert.HasCount(52, all.Distinct(), "No work is repeated or skipped across pages.");
        CollectionAssert.AreEqual(
            second.NoKnownSeries.Select(entry => entry.Book.WorkId).ToArray(),
            repeat.NoKnownSeries.Select(entry => entry.Book.WorkId).ToArray(), "Ties order identically on every request.");

        var exactly = await GetAsync<CatalogAuthorBrowseResponse>(client, $"{Base}/authors/{ids.Exact}");
        Assert.HasCount(50, exactly.NoKnownSeries);
        Assert.IsFalse(exactly.HasMore, "Exactly one page has no more.");
    }

    [TestMethod]
    [DataRow("page=0")]
    [DataRow("page=-3")]
    [DataRow("page=101")]
    [DataRow("page=abc")]
    public async Task BadPageInputIsRejectedOnEverySavedRouteWithoutLeakingDetail(string query)
    {
        await using var factory = FactoryFor(new ProviderSpy(), []);
        using var client = await SignedInAsync(factory);
        var anyId = Guid.NewGuid();

        foreach (var url in new[]
                 {
                     $"{Base}/authors/{anyId}?{query}", $"{Base}/series/{anyId}?{query}",
                     $"{Base}/contexts/{anyId}/authors/{anyId}?{query}", $"{Base}/contexts/{anyId}/series/{anyId}?{query}"
                 })
        {
            var response = await client.GetAsync(url);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, url);
            var body = await response.Content.ReadAsStringAsync();
            Assert.IsFalse(body.Contains("Exception", StringComparison.OrdinalIgnoreCase) || body.Contains(" at ", StringComparison.Ordinal), body);
        }
    }

    // ---------------------------------------------------------------- saved series

    [TestMethod]
    public async Task SavedSeriesListsKnownMembersByRecordedPositionKeepingTiesUnknownsAndQualifiedLabels()
    {
        await using var factory = FactoryFor(new ProviderSpy(), []);
        var ids = await WithDatabaseAsync(factory, database =>
        {
            var author = new Author(Unique("Series Author"), null, DateTimeOffset.UtcNow);
            var series = new Series(Unique("Ordered Saga"), SeriesStatus.Completed, DateTimeOffset.UtcNow);
            var other = new Series(Unique("Elsewhere"), SeriesStatus.Unknown, DateTimeOffset.UtcNow);
            database.Authors.Add(author);
            database.Series.AddRange(series, other);
            Work Add(string title, string? label, decimal? sort, bool retired = false)
            {
                var work = NewWork(database, title, retired);
                work.AddAuthor(author, 0);
                Join(work, series, label, sort);
                return work;
            }

            Add("Tail Unknown B", null, null);
            Add("Three B", "3", 3m);
            Add("Half", "2.5", 2.5m);
            Add("One", "1", 1m);
            Add("Three A", "3", 3m);
            Add("Tail Unknown A", null, null);
            Add("Gone", "5", 5m, retired: true);
            var both = Add("Also Elsewhere", "9", 9m);
            Join(both, other, "2", 2m, primary: false);
            return Task.FromResult(series.Id);
        });
        using var client = await SignedInAsync(factory);

        var page = await GetAsync<CatalogSeriesBrowseResponse>(client, $"{Base}/series/{ids}");

        CollectionAssert.AreEqual(
            new[] { "One", "Half", "Three A", "Three B", "Also Elsewhere", "Tail Unknown A", "Tail Unknown B" },
            page.Entries.Select(entry => entry.Book.Title).ToArray());
        CollectionAssert.AreEqual(new decimal?[] { 1m, 2.5m, 3m, 3m, 9m, null, null }, page.Entries.Select(entry => entry.Position).ToArray());
        CollectionAssert.Contains(page.Limitations.ToArray(), "known-local-only");
        Assert.IsFalse(page.Limitations.Contains("complete"), "Completion is never claimed.");
        Assert.HasCount(2, page.Entries.Single(entry => entry.Book.Title == "Also Elsewhere").Book.Memberships);
        Assert.IsFalse(page.HasMore);
    }

    [TestMethod]
    public async Task SavedSeriesPagingBoundariesUseOneExtraRow()
    {
        await using var factory = FactoryFor(new ProviderSpy(), []);
        var ids = await WithDatabaseAsync(factory, database =>
        {
            var author = new Author(Unique("Paging Author"), null, DateTimeOffset.UtcNow);
            var fifty = new Series(Unique("Fifty"), SeriesStatus.Unknown, DateTimeOffset.UtcNow);
            var fiftyOne = new Series(Unique("Fifty One"), SeriesStatus.Unknown, DateTimeOffset.UtcNow);
            database.Authors.Add(author);
            database.Series.AddRange(fifty, fiftyOne);
            for (var index = 1; index <= 50; index++) { var work = NewWork(database, $"F {index:00}"); work.AddAuthor(author, 0); Join(work, fifty, index.ToString(CultureInfo.InvariantCulture), index); }
            for (var index = 1; index <= 51; index++) { var work = NewWork(database, $"G {index:00}"); work.AddAuthor(author, 0); Join(work, fiftyOne, index.ToString(CultureInfo.InvariantCulture), index); }
            return Task.FromResult((Fifty: fifty.Id, FiftyOne: fiftyOne.Id));
        });
        using var client = await SignedInAsync(factory);

        var exact = await GetAsync<CatalogSeriesBrowseResponse>(client, $"{Base}/series/{ids.Fifty}");
        var firstPage = await GetAsync<CatalogSeriesBrowseResponse>(client, $"{Base}/series/{ids.FiftyOne}");
        var secondPage = await GetAsync<CatalogSeriesBrowseResponse>(client, $"{Base}/series/{ids.FiftyOne}?page=2");

        Assert.HasCount(50, exact.Entries);
        Assert.IsFalse(exact.HasMore);
        Assert.HasCount(50, firstPage.Entries);
        Assert.IsTrue(firstPage.HasMore);
        Assert.HasCount(1, secondPage.Entries);
        Assert.AreEqual(51m, secondPage.Entries[0].Position);
        Assert.IsFalse(secondPage.HasMore);
    }

    // ---------------------------------------------------------------- observed previews

    private static async Task<Guid> StartRunAsync(HttpClient client, string text)
    {
        var response = await client.PostAsJsonAsync("/api/v1/catalog/search/runs", new CatalogSearchRequest(text));
        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CatalogSearchRunStartedResponse>())!.RunId;
    }

    private static async Task<CatalogSearchRunResponse> PollUntilAsync(
        HttpClient client, Guid runId, Func<CatalogSearchRunResponse, bool> done)
    {
        for (var attempt = 0; attempt < 300; attempt++)
        {
            var run = (await client.GetFromJsonAsync<CatalogSearchRunResponse>($"/api/v1/catalog/search/runs/{runId}"))!;
            if (done(run)) return run;
            await Task.Delay(20);
        }

        throw new TimeoutException("The search run did not reach the expected state.");
    }

    [TestMethod]
    public async Task ObservedPreviewIsSourceScopedUnverifiedAndOffersEverySavedNamesakeAsASeparateChoice()
    {
        var spy = new ProviderSpy();
        var shared = Unique("Pat Namesake");
        var seriesLabel = Unique("Namesake Saga");
        await using var factory = FactoryFor(spy,
            demo:
            [
                Book("demo", "d-1", "Observed One", [shared], new BookSeriesCandidate(seriesLabel, "1", true)),
                Book("demo", "d-2", "Observed Two", [shared, "Co Author"], new BookSeriesCandidate(seriesLabel, "2.5", true), new BookSeriesCandidate(seriesLabel + " (reading order)", "3", false)),
                Book("demo", "d-3", "Observed Standalone", [shared]),
                Book("demo", "d-4", "Someone Else's", ["Different Person"], new BookSeriesCandidate(seriesLabel, "7", true))
            ],
            second: [Book("gutendex", "g-1", "Other Source Book", [shared], new BookSeriesCandidate(seriesLabel, "99", true))]);
        var saved = await WithDatabaseAsync(factory, database =>
        {
            var first = new Author(shared, null, DateTimeOffset.UtcNow);
            var second = new Author(shared.ToUpperInvariant(), null, DateTimeOffset.UtcNow);
            var series = new Series(seriesLabel, SeriesStatus.Unknown, DateTimeOffset.UtcNow);
            var otherSeries = new Series(seriesLabel.ToLowerInvariant(), SeriesStatus.Unknown, DateTimeOffset.UtcNow);
            database.Authors.AddRange(first, second);
            database.Series.AddRange(series, otherSeries);
            var work = NewWork(database, "Saved Namesake Book");
            work.AddAuthor(first, 0);
            Join(work, series, "1", 1m);
            return Task.FromResult((First: first.Id, Second: second.Id, Series: series.Id, OtherSeries: otherSeries.Id));
        });
        using var client = await SignedInAsync(factory);
        var run = await PollUntilAsync(client, await StartRunAsync(client, "Namesake"), r => r.IsComplete);
        var demo = run.Search.Results.Single(result => result.ProviderId == "demo" && result.ExternalId == "d-2");
        var authorRef = demo.Navigation!.Authors.Single(reference => reference.Label == shared);
        var seriesRef = demo.Navigation.Series.Single(reference => reference.Label == seriesLabel);
        Assert.AreEqual(0, authorRef.Ordinal);
        Assert.AreEqual(0, seriesRef.Ordinal);

        var author = await GetAsync<CatalogObservedAuthorBrowseResponse>(client, $"{Base}/contexts/{demo.Navigation.ContextId}/authors/{authorRef.SubjectId}");

        Assert.AreEqual(shared, author.Label);
        Assert.AreEqual("demo", author.ProviderId);
        CollectionAssert.IsSubsetOf(new[] { "source-sample", "identity-unverified" }, author.Limitations.ToArray());
        var listed = author.Groups.SelectMany(group => group.Entries).Concat(author.NoKnownSeries).Select(entry => entry.Book.ExternalId).Distinct().OrderBy(id => id).ToArray();
        CollectionAssert.AreEqual(new[] { "d-1", "d-2", "d-3" }, listed, "Only this source's records with the exact credit; not 'Different Person', not the other source.");
        Assert.IsTrue(author.Groups.All(group => group.SeriesId is null), "No saved series identity is attributed to observed groups.");
        Assert.IsTrue(author.Groups.Select(group => group.Name).SequenceEqual(new[] { seriesLabel, seriesLabel + " (reading order)" }.OrderBy(name => name, StringComparer.Ordinal)),
            "The full label, ordering qualifier included, defines a group.");
        Assert.AreEqual("d-3", author.NoKnownSeries.Single().Book.ExternalId);
        Assert.HasCount(2, author.NameChoices.Choices, "Both saved namesakes are separate choices; neither is chosen for the user.");
        CollectionAssert.AreEquivalent(new[] { saved.First, saved.Second }, author.NameChoices.Choices.Select(choice => choice.Id).ToArray());
        Assert.IsFalse(author.NameChoices.Truncated);
        Assert.AreEqual(1, author.NameChoices.Choices.Single(choice => choice.Id == saved.First).KnownBookCount);
        CollectionAssert.AreEqual(new[] { "Saved Namesake Book" }, author.NameChoices.Choices.Single(choice => choice.Id == saved.First).SampleTitles.ToArray());

        var seriesPreview = await GetAsync<CatalogObservedSeriesBrowseResponse>(client, $"{Base}/contexts/{demo.Navigation.ContextId}/series/{seriesRef.SubjectId}");
        CollectionAssert.AreEqual(new[] { "d-1", "d-2", "d-4" }, seriesPreview.Entries.Select(entry => entry.Book.ExternalId).ToArray(),
            "Only this source's exact label, in numeric position order, with 2.5 after 1 and the other source's 99 absent.");
        CollectionAssert.AreEqual(new decimal?[] { 1m, 2.5m, 7m }, seriesPreview.Entries.Select(entry => entry.Position).ToArray());
        Assert.HasCount(2, seriesPreview.NameChoices.Choices, "Two saved series differing only by case remain two choices.");
        CollectionAssert.IsSubsetOf(new[] { "source-sample", "identity-unverified" }, seriesPreview.Limitations.ToArray());
    }

    [TestMethod]
    public async Task MoreThanTwentySavedNamesakesAreCappedWithATruncationIndicator()
    {
        var shared = Unique("Crowded Name");
        await using var factory = FactoryFor(new ProviderSpy(), [Book("demo", "d-1", "Observed", [shared])]);
        await WithDatabaseAsync(factory, database =>
        {
            for (var index = 0; index < 25; index++) database.Authors.Add(new Author(shared, null, DateTimeOffset.UtcNow));
            return Task.FromResult(0);
        });
        using var client = await SignedInAsync(factory);
        var run = await PollUntilAsync(client, await StartRunAsync(client, "Crowded"), r => r.IsComplete);
        var navigation = run.Search.Results.Single().Navigation!;

        var author = await GetAsync<CatalogObservedAuthorBrowseResponse>(client,
            $"{Base}/contexts/{navigation.ContextId}/authors/{navigation.Authors.Single().SubjectId}");

        Assert.HasCount(20, author.NameChoices.Choices);
        Assert.IsTrue(author.NameChoices.Truncated);
        Assert.HasCount(20, author.NameChoices.Choices.Select(choice => choice.Id).Distinct());
        CollectionAssert.Contains(author.Limitations.ToArray(), "truncated");
    }

    [TestMethod]
    public async Task NavigationIsIssuedWhileAnotherProviderIsStillRunningAndTheIssuedPreviewSurvivesCancellation()
    {
        var spy = new ProviderSpy();
        var gate = new SemaphoreSlim(0);
        var shared = Unique("Fast Author");
        await using var factory = FactoryFor(spy,
            demo: [Book("demo", "fast-1", "Fast One", [shared], new BookSeriesCandidate("Fast Saga", "1", true)), Book("demo", "fast-2", "Fast Two", [shared], new BookSeriesCandidate("Fast Saga", "2", true))],
            second: [Book("gutendex", "slow-1", "Slow One", ["Slow Author"])],
            secondGate: gate);
        using var client = await SignedInAsync(factory);
        var runId = await StartRunAsync(client, "Fast");

        var partial = await PollUntilAsync(client, runId, r => r.Search.Results.Any(result => result.Navigation is not null));
        Assert.IsFalse(partial.IsComplete, "A provider is still running.");
        var navigation = partial.Search.Results.First(result => result.ProviderId == "demo").Navigation!;
        var authorSubject = navigation.Authors.Single().SubjectId;
        var callsBefore = (spy.Searches, spy.Details);

        var cancelled = await client.DeleteAsync($"/api/v1/catalog/search/runs/{runId}");
        Assert.AreEqual(HttpStatusCode.NoContent, cancelled.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/catalog/search/runs/{runId}")).StatusCode, "The run itself is gone.");

        var preview = await GetAsync<CatalogObservedAuthorBrowseResponse>(client, $"{Base}/contexts/{navigation.ContextId}/authors/{authorSubject}");
        var book = await GetAsync<CatalogObservedBookResponse>(client, $"{Base}/contexts/{navigation.ContextId}/books/demo?externalId=fast-2");
        gate.Release();

        CollectionAssert.AreEquivalent(new[] { "fast-1", "fast-2" },
            preview.Groups.SelectMany(group => group.Entries).Select(entry => entry.Book.ExternalId).Distinct().ToArray());
        Assert.AreEqual("Fast Two", book.Book.Title);
        Assert.AreEqual(callsBefore, (spy.Searches, spy.Details), "Browsing after cancellation made no provider call.");
    }

    [TestMethod]
    public async Task LaterResultsAppendWithoutChangingIssuedSubjectsAndPollingNeverGrowsTheStore()
    {
        var spy = new ProviderSpy();
        var gate = new SemaphoreSlim(0);
        var shared = Unique("Stable Author");
        await using var factory = FactoryFor(spy,
            demo: [Book("demo", "early", "Early Book", [shared], new BookSeriesCandidate("Stable Saga", "1", true))],
            second: [Book("gutendex", "late", "Late Book", [shared], new BookSeriesCandidate("Stable Saga", "9", true))],
            secondGate: gate);
        using var client = await SignedInAsync(factory);
        var store = factory.Services.GetRequiredService<CatalogBrowseContextStore>();
        var runId = await StartRunAsync(client, "Stable");

        var early = await PollUntilAsync(client, runId, r => r.Search.Results.Any(result => result.Navigation is not null));
        var first = early.Search.Results.Single().Navigation!;
        var contexts = store.Statistics();
        for (var poll = 0; poll < 25; poll++) await client.GetAsync($"/api/v1/catalog/search/runs/{runId}");
        Assert.AreEqual(contexts, store.Statistics(), "Repeated polling reuses one context and adds no index entries.");

        gate.Release();
        var done = await PollUntilAsync(client, runId, r => r.IsComplete);
        var demoAfter = done.Search.Results.Single(result => result.ProviderId == "demo").Navigation!;
        var lateNavigation = done.Search.Results.Single(result => result.ProviderId == "gutendex").Navigation!;

        Assert.AreEqual(first.ContextId, demoAfter.ContextId);
        Assert.AreEqual(first.Authors.Single().SubjectId, demoAfter.Authors.Single().SubjectId, "An issued subject keeps its meaning.");
        Assert.AreEqual(first.Series.Single().SubjectId, demoAfter.Series.Single().SubjectId);
        Assert.AreEqual(first.ContextId, lateNavigation.ContextId, "One context per run.");
        Assert.AreNotEqual(first.Authors.Single().SubjectId, lateNavigation.Authors.Single().SubjectId, "The same label from another source is another subject.");
        Assert.AreEqual(first.ExpiresAtUtc, demoAfter.ExpiresAtUtc, "Expiry is absolute: appending and polling do not renew it.");

        var preview = await GetAsync<CatalogObservedAuthorBrowseResponse>(client, $"{Base}/contexts/{first.ContextId}/authors/{first.Authors.Single().SubjectId}");
        CollectionAssert.AreEqual(new[] { "early" },
            preview.Groups.SelectMany(group => group.Entries).Select(entry => entry.Book.ExternalId).ToArray(),
            "The late source's record never joins the early source's preview.");
        Assert.AreEqual(1, store.Statistics().Contexts);
    }

    [TestMethod]
    public async Task AGroupedSearchRepresentativeLinksOnlyToItsOwnSourcesRelationships()
    {
        var spy = new ProviderSpy();
        var shared = Unique("Grouped Author");
        await using var factory = FactoryFor(spy,
            demo: [Book("demo", "d", "Grouped Book", [shared], new BookSeriesCandidate("Demo Saga", "1", true))],
            second: [Book("gutendex", "g", "Grouped Book", [shared], new BookSeriesCandidate("Second Saga", "5", true))]);
        using var client = await SignedInAsync(factory);

        var run = await PollUntilAsync(client, await StartRunAsync(client, "Grouped"), r => r.IsComplete);

        var grouped = run.Search.Results.Single();
        Assert.HasCount(2, grouped.Sources, "Search still groups the two sources into one row.");
        var navigation = grouped.Navigation!;
        Assert.AreEqual(grouped.Series.Single().Name, navigation.Series.Single().Label, "The link is the representative's own label.");
        var preview = await GetAsync<CatalogObservedSeriesBrowseResponse>(client,
            $"{Base}/contexts/{navigation.ContextId}/series/{navigation.Series.Single().SubjectId}");
        Assert.AreEqual(grouped.ProviderId, preview.ProviderId);
        Assert.AreEqual(grouped.ExternalId, preview.Entries.Single().Book.ExternalId, "The other source's relationships are not borrowed.");
        Assert.AreEqual(grouped.Series.Single().PositionLabel, preview.Entries.Single().PositionLabel);
    }

    [TestMethod]
    public async Task ClickingADuplicateLabelReturnsTheRecordAndOrdinalThatIssuedIt()
    {
        var spy = new ProviderSpy();
        var shared = Unique("Shared Person");
        await using var factory = FactoryFor(spy,
            demo:
            [
                Book("demo", "a", "Book A", [shared]),
                Book("demo", "b", "Book B", ["Other Person", shared]),
                Book("demo", "c", "Book C", [shared, shared])
            ]);
        using var client = await SignedInAsync(factory);

        var run = await PollUntilAsync(client, await StartRunAsync(client, "Shared"), r => r.IsComplete);
        var bookA = run.Search.Results.Single(result => result.ExternalId == "a").Navigation!;
        var bookB = run.Search.Results.Single(result => result.ExternalId == "b").Navigation!;
        var bookC = run.Search.Results.Single(result => result.ExternalId == "c").Navigation!;
        var aShared = bookA.Authors.Single(reference => reference.Label == shared);
        var bShared = bookB.Authors.Single(reference => reference.Ordinal == 1);
        Assert.AreEqual(shared, bShared.Label);
        Assert.AreNotEqual(aShared.SubjectId, bShared.SubjectId,
            "The same label at another record is a different source observation.");

        var fromB = await GetAsync<CatalogObservedAuthorBrowseResponse>(client,
            $"{Base}/contexts/{bookB.ContextId}/authors/{bShared.SubjectId}");
        Assert.AreEqual("b", fromB.AnchorExternalId, "The selected credit returns the record that issued it.");
        CollectionAssert.AreEquivalent(new[] { "a", "b", "c" },
            fromB.Groups.SelectMany(group => group.Entries).Concat(fromB.NoKnownSeries)
                .Select(entry => entry.Book.ExternalId).Distinct().ToArray(),
            "The preview list still aggregates the exact label in this source as an unverified association.");

        Assert.HasCount(2, bookC.Authors);
        CollectionAssert.AreEqual(new[] { 0, 1 }, bookC.Authors.Select(reference => reference.Ordinal).ToArray());
        Assert.AreNotEqual(bookC.Authors[0].SubjectId, bookC.Authors[1].SubjectId,
            "Duplicate labels at two ordinals of one record are two observations.");
        var fromCSecond = await GetAsync<CatalogObservedAuthorBrowseResponse>(client,
            $"{Base}/contexts/{bookC.ContextId}/authors/{bookC.Authors[1].SubjectId}");
        Assert.AreEqual("c", fromCSecond.AnchorExternalId);
    }

    [TestMethod]
    public async Task SavedAssociationsAreCappedInTheDatabaseWithATruthfulTruncationFlag()
    {
        await using var factory = FactoryFor(new ProviderSpy(), []);
        var ids = await WithDatabaseAsync(factory, async database =>
        {
            var author = new Author(Unique("Heavy Author"), null, DateTimeOffset.UtcNow);
            database.Authors.Add(author);
            var heavy = NewWork(database, "Heavy Work");
            heavy.AddAuthor(author, 0);
            for (var index = 1; index < 25; index++)
            {
                var coauthor = new Author(Unique($"Coauthor {index}"), null, DateTimeOffset.UtcNow);
                database.Authors.Add(coauthor);
                heavy.AddAuthor(coauthor, index);
            }

            for (var index = 0; index < 25; index++)
            {
                var series = new Series(Unique($"Heavy Saga {index:00}"), SeriesStatus.Unknown, DateTimeOffset.UtcNow);
                database.Series.Add(series);
                Join(heavy, series, (index + 1).ToString(CultureInfo.InvariantCulture), index + 1);
            }

            for (var index = 0; index < 12; index++)
            {
                database.ExternalReferences.Add(new ExternalReference(
                    $"provider-{index:00}", ExternalReferenceEntityType.Work, heavy.Id, Unique("heavy-ext"), DateTimeOffset.UtcNow));
            }

            database.Editions.Add(new Edition(heavy.Id, "Heavy ebook", EditionFormat.Ebook, null, null, DateTimeOffset.UtcNow));
            database.Editions.Add(new Edition(heavy.Id, "Heavy audio", EditionFormat.Audiobook, null, null, DateTimeOffset.UtcNow));

            var light = NewWork(database, "Light Work");
            light.AddAuthor(author, 0);
            await Task.CompletedTask;
            return (Author: author.Id, Heavy: heavy.Id, Light: light.Id);
        });
        using var client = await SignedInAsync(factory);

        var page = await GetAsync<CatalogAuthorBrowseResponse>(client, $"{Base}/authors/{ids.Author}");
        var heavyBook = page.Groups.SelectMany(group => group.Entries)
            .Select(entry => entry.Book)
            .First(book => book.WorkId == ids.Heavy);

        Assert.HasCount(20, heavyBook.Credits, "Credits are capped per Work.");
        Assert.HasCount(20, heavyBook.Memberships, "Memberships are capped per Work; omitted ones are disclosed, not implied absent.");
        Assert.HasCount(10, heavyBook.Sources, "Source summaries are capped per Work.");
        Assert.IsTrue(heavyBook.AssociationsTruncated);
        CollectionAssert.AreEquivalent(new[] { "Ebook", "Audiobook" }, heavyBook.Formats.ToArray(),
            "Known Edition formats survive to the row.");
        CollectionAssert.Contains(page.Limitations.ToArray(), CatalogBrowseLimitations.Truncated);

        var lightBook = page.NoKnownSeries.Single(entry => entry.Book.WorkId == ids.Light).Book;
        Assert.IsFalse(lightBook.AssociationsTruncated);
        Assert.IsEmpty(lightBook.Formats);
        Assert.IsNull(lightBook.VersionKind, "No version identity is inferred for a saved Work.");

        // The bound is in the SQL, not a slice after materialization, and the query count does not grow with page content.
        var capture = new SqlCapture();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(WebTestFixture.Require(fixture).ConnectionString)
            .AddInterceptors(capture)
            .Options;
        await using var direct = new AppDbContext(options);
        var repository = new CatalogBrowseRepository(direct);
        var works = await repository.GetAuthorWorksAsync(ids.Author, 0, 51, CancellationToken.None);
        var commandsForTwoWorks = capture.Commands.Count;
        capture.Commands.Clear();
        await repository.GetAuthorWorksAsync(ids.Author, 0, 1, CancellationToken.None);
        var commandsForOneWork = capture.Commands.Count;

        Assert.AreEqual(commandsForOneWork, commandsForTwoWorks, "Association reads use a fixed query count per page.");
        var heavyWork = works.Single(work => work.WorkId == ids.Heavy);
        Assert.HasCount(20, heavyWork.Credits);
        Assert.HasCount(20, heavyWork.Memberships);
        Assert.HasCount(10, heavyWork.Sources);
        Assert.IsTrue(heavyWork.AssociationsTruncated);
        CollectionAssert.AreEquivalent(new[] { "Ebook", "Audiobook" }, heavyWork.Formats.ToArray());
        var associationSql = capture.Commands.Where(sql => sql.Contains("row_number()", StringComparison.Ordinal)).ToArray();
        Assert.HasCount(4, associationSql, "One bounded query each for credits, memberships, sources and formats.");
        Assert.IsTrue(associationSql.All(sql => sql.Contains("\"Rank\" <=", StringComparison.Ordinal)),
            "Every association query caps rows per Work inside the database query.");
    }

    [TestMethod]
    public async Task ObservedAndCachedBookRowsKeepKnownFormatsAndTheExistingVersionAssessment()
    {
        var spy = new ProviderSpy();
        var collection = new BookCandidate(
            "demo", "Demo Source", "d-1", "Alpha", ["Ada Example"], null, null, null,
            [new BookEditionCandidate("Alpha", null, "Ebook", null), new BookEditionCandidate("Alpha audio", null, "Audiobook", null)],
            [], WorkTitle: "Alpha Omnibus");
        var plain = Book("demo", "d-2", "Plain", ["Ada Example"]);
        await using var factory = FactoryFor(spy, demo: [collection, plain]);
        using var client = await SignedInAsync(factory);

        var run = await PollUntilAsync(client, await StartRunAsync(client, "Alpha"), r => r.IsComplete);
        var navigation = run.Search.Results.Single(result => result.ExternalId == "d-1").Navigation!;
        var preview = await GetAsync<CatalogObservedAuthorBrowseResponse>(client,
            $"{Base}/contexts/{navigation.ContextId}/authors/{navigation.Authors.Single().SubjectId}");
        var cached = await GetAsync<CatalogObservedBookResponse>(client,
            $"{Base}/contexts/{navigation.ContextId}/books/demo?externalId=d-1");

        foreach (var book in new[] { preview.NoKnownSeries.Single(entry => entry.Book.ExternalId == "d-1").Book, cached.Book })
        {
            CollectionAssert.AreEquivalent(new[] { "Ebook", "Audiobook" }, book.Formats.ToArray());
            Assert.AreEqual("Collection", book.VersionKind);
            Assert.AreEqual("Collection or combined volume", book.VersionLabel);
        }

        var plainBook = preview.NoKnownSeries.Single(entry => entry.Book.ExternalId == "d-2").Book;
        Assert.IsEmpty(plainBook.Formats);
        Assert.AreEqual("Unspecified", plainBook.VersionKind, "Unknown stays the existing 'version unknown' assessment.");
    }

    // ---------------------------------------------------------------- candidate detail

    [TestMethod]
    public async Task CandidateDetailAddsNavigationFromItsOneProviderFetchAndBrowsingWorksWhileTheProviderIsDown()
    {
        var spy = new ProviderSpy();
        var shared = Unique("Detail Author");
        await using var factory = FactoryFor(spy, [Book("demo", "d-1", "Detail Book", [shared], new BookSeriesCandidate("Detail Saga", "3", true)),
            Book("demo", "d-2", "Detail Sibling", [shared], new BookSeriesCandidate("Detail Saga", "4", true))]);
        using var client = await SignedInAsync(factory);

        var detail = await GetAsync<CatalogBookCandidateResponse>(client, "/api/v1/catalog/candidates/demo/d-1");

        Assert.AreEqual(1, spy.Details, "One provider fetch, and navigation came from that same record.");
        Assert.AreEqual(0, spy.Searches);
        var navigation = detail.Navigation!;
        Assert.IsNotNull(navigation.ContextId);
        Assert.AreEqual(shared, navigation.Authors.Single().Label);
        Assert.AreEqual("Detail Saga", navigation.Series.Single().Label);

        spy.Offline = true;
        var detailsBefore = spy.Details;
        var series = await GetAsync<CatalogObservedSeriesBrowseResponse>(client,
            $"{Base}/contexts/{navigation.ContextId}/series/{navigation.Series.Single().SubjectId}");
        var cached = await GetAsync<CatalogObservedBookResponse>(client, $"{Base}/contexts/{navigation.ContextId}/books/demo?externalId=d-1");

        CollectionAssert.AreEqual(new[] { "d-1" }, series.Entries.Select(entry => entry.Book.ExternalId).ToArray(),
            "A direct detail entry legitimately exposes only its anchor.");
        Assert.AreEqual("Detail Book", cached.Book.Title);
        Assert.AreEqual(3m, cached.Book.Memberships.Single().Position);
        Assert.AreEqual(detailsBefore, spy.Details, "Browsing made no provider call while the provider was down.");
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/api/v1/catalog/candidates/demo/d-1")).StatusCode,
            "Existing provider-outage behavior of the detail route is unchanged.");
    }

    [TestMethod]
    public async Task ASecondDetailViewReusesItsContextInsteadOfAllocatingAnother()
    {
        var spy = new ProviderSpy();
        await using var factory = FactoryFor(spy, [Book("demo", "d-1", "Detail Book", ["Reuse Author"])]);
        using var client = await SignedInAsync(factory);
        var store = factory.Services.GetRequiredService<CatalogBrowseContextStore>();

        var first = (await GetAsync<CatalogBookCandidateResponse>(client, "/api/v1/catalog/candidates/demo/d-1")).Navigation!;
        var second = (await GetAsync<CatalogBookCandidateResponse>(client, "/api/v1/catalog/candidates/demo/d-1")).Navigation!;

        Assert.AreEqual(first.ContextId, second.ContextId);
        Assert.AreEqual(first.ExpiresAtUtc, second.ExpiresAtUtc);
        Assert.AreEqual(1, store.Statistics().Contexts);
    }

    [TestMethod]
    public async Task LegacyAggregateSearchAttachesNavigationFromItsExistingProviderResultsOnly()
    {
        var spy = new ProviderSpy();
        await using var factory = FactoryFor(spy, [Book("demo", "d-1", "Legacy Book", ["Legacy Author"], new BookSeriesCandidate("Legacy Saga", "1", true))]);
        using var client = await SignedInAsync(factory);

        var response = await GetAsync<CatalogSearchResponse>(client, "/api/v1/catalog/search?q=Legacy");

        Assert.AreEqual(1, spy.Searches);
        Assert.AreEqual(0, spy.Details);
        var navigation = response.Results.Single().Navigation!;
        Assert.AreEqual("Legacy Author", navigation.Authors.Single().Label);
        var preview = await GetAsync<CatalogObservedAuthorBrowseResponse>(client,
            $"{Base}/contexts/{navigation.ContextId}/authors/{navigation.Authors.Single().SubjectId}");
        Assert.AreEqual("d-1", preview.Groups.Single().Entries.Single().Book.ExternalId);
        Assert.AreEqual(1, spy.Searches, "The preview made no provider call.");
    }

    [TestMethod]
    public async Task ACacheThatCannotAdmitAContextLeavesSearchAndDetailSuccessfulWithASafeExplanation()
    {
        var spy = new ProviderSpy();
        await using var factory = FactoryFor(spy, [Book("demo", "d-1", "Uncached", ["Uncached Author"])],
            configure: services =>
            {
                services.RemoveAll<CatalogBrowseContextStore>();
                services.AddSingleton(new CatalogBrowseContextStore(new CatalogBrowseContextLimits { MaxContextsTotal = 0 }));
            });
        using var client = await SignedInAsync(factory);

        var run = await PollUntilAsync(client, await StartRunAsync(client, "Uncached"), r => r.IsComplete);
        var detail = await GetAsync<CatalogBookCandidateResponse>(client, "/api/v1/catalog/candidates/demo/d-1");

        Assert.AreEqual("Uncached", run.Search.Results.Single().Title, "Search itself is unaffected.");
        foreach (var navigation in new[] { run.Search.Results.Single().Navigation!, detail.Navigation! })
        {
            Assert.IsNull(navigation.ContextId);
            Assert.IsNull(navigation.ExpiresAtUtc);
            Assert.IsEmpty(navigation.Authors, "No fake link is issued.");
            CollectionAssert.AreEqual(new[] { "navigation-unavailable" }, navigation.Limitations.ToArray());
        }
    }

    // ---------------------------------------------------------------- provider independence

    [TestMethod]
    public async Task SavedBrowsingAndAnIssuedPreviewKeepWorkingAfterEveryProviderIsDisabledWithoutAnyProviderCall()
    {
        var spy = new ProviderSpy();
        var shared = Unique("Offline Author");
        // Disabling providers is persisted, so this test owns a database instead of changing the class's shared one.
        await using var isolated = await WebTestFixture.CreateAsync();
        await using var factory = FactoryFor(spy, [Book("demo", "d-1", "Offline Book", [shared])],
            [Book("gutendex", "g-1", "Offline Two", ["Another"])], connection: WebTestFixture.Require(isolated).ConnectionString);
        var ids = await WithDatabaseAsync(factory, database =>
        {
            var author = new Author(Unique("Saved Offline"), null, DateTimeOffset.UtcNow);
            var series = new Series(Unique("Saved Offline Saga"), SeriesStatus.Unknown, DateTimeOffset.UtcNow);
            database.Authors.Add(author);
            database.Series.Add(series);
            var work = NewWork(database, "Saved Offline Book");
            work.AddAuthor(author, 0);
            Join(work, series, "1", 1m);
            return Task.FromResult((Author: author.Id, Series: series.Id));
        });
        using var client = await SignedInAsync(factory);
        var run = await PollUntilAsync(client, await StartRunAsync(client, "Offline"), r => r.IsComplete);
        var navigation = run.Search.Results.First(result => result.ProviderId == "demo").Navigation!;
        using var admin = await SignedInAsync(factory, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword);
        foreach (var provider in new[] { "demo", "gutendex" })
        {
            var disabled = await admin.PutAsJsonAsync($"/api/v1/admin/integrations/metadata/{provider}/enabled", new SetProviderEnabledRequest(false));
            Assert.IsTrue(disabled.IsSuccessStatusCode, $"{provider}: {disabled.StatusCode}");
        }

        var before = (spy.Searches, spy.Details);
        var author = await GetAsync<CatalogAuthorBrowseResponse>(client, $"{Base}/authors/{ids.Author}");
        var series = await GetAsync<CatalogSeriesBrowseResponse>(client, $"{Base}/series/{ids.Series}");
        var preview = await GetAsync<CatalogObservedAuthorBrowseResponse>(client,
            $"{Base}/contexts/{navigation.ContextId}/authors/{navigation.Authors.Single().SubjectId}");

        Assert.AreEqual("Saved Offline Book", author.Groups.Single().Entries.Single().Book.Title);
        Assert.AreEqual("Saved Offline Book", series.Entries.Single().Book.Title);
        Assert.AreEqual("Offline Book", preview.Groups.SelectMany(group => group.Entries).Concat(preview.NoKnownSeries).Single().Book.Title);
        Assert.AreEqual(before, (spy.Searches, spy.Details), "No provider was called, enabled or not.");
    }

    // ---------------------------------------------------------------- no-write proof

    private static readonly string[] SnapshotTables =
    [
        "catalog.authors", "catalog.works", "catalog.work_authors", "catalog.editions", "catalog.series",
        "catalog.series_entries", "catalog.external_references", "requests.book_requests", "requests.request_participants",
        "requests.request_formats", "requests.request_status_history", "following.follows", "notifications.notification_events",
        "notifications.notification_receipts", "communications.outbound_communications",
        "communications.outbound_communication_deliveries", "audit.audit_events"
    ];

    /// <summary>A hash of every column of every row, so counts, values, timestamps and version columns are all covered.</summary>
    private static async Task<Dictionary<string, string>> SnapshotAsync()
    {
        await using var connection = new NpgsqlConnection(WebTestFixture.Require(fixture).ConnectionString);
        await connection.OpenAsync();
        var result = new Dictionary<string, string>();
        foreach (var table in SnapshotTables)
        {
            await using var command = new NpgsqlCommand(
                $"SELECT count(*)::text || ':' || coalesce(md5(string_agg(t::text, '|' ORDER BY t::text)), '') FROM {table} t", connection);
            result[table] = (string)(await command.ExecuteScalarAsync())!;
        }

        return result;
    }

    [TestMethod]
    public async Task BrowsingSearchingAndOpeningDetailsLeavesEveryCatalogRequestFollowAndNotificationRowUntouched()
    {
        var spy = new ProviderSpy();
        var shared = Unique("Readonly Author");
        await using var factory = FactoryFor(spy,
            [Book("demo", "d-1", "Readonly One", [shared], new BookSeriesCandidate("Readonly Saga", "1", true)),
             Book("demo", "d-2", "Readonly Two", [shared], new BookSeriesCandidate("Readonly Saga", "2", true))]);
        var ids = await WithDatabaseAsync(factory, database =>
        {
            var author = new Author(shared, null, DateTimeOffset.UtcNow);
            var series = new Series("Readonly Saga", SeriesStatus.Unknown, DateTimeOffset.UtcNow);
            database.Authors.Add(author);
            database.Series.Add(series);
            var work = NewWork(database, "Readonly One");
            work.AddAuthor(author, 0);
            Join(work, series, "1", 1m);
            return Task.FromResult((Author: author.Id, Series: series.Id));
        });
        using var client = await SignedInAsync(factory);
        var before = await SnapshotAsync();
        Assert.IsGreaterThan(0, int.Parse(before["catalog.works"].Split(':')[0], CultureInfo.InvariantCulture), "The snapshot sees real data.");

        var run = await PollUntilAsync(client, await StartRunAsync(client, "Readonly"), r => r.IsComplete);
        var navigation = run.Search.Results.Single(result => result.ExternalId == "d-1").Navigation!;
        await GetAsync<CatalogBookCandidateResponse>(client, "/api/v1/catalog/candidates/demo/d-2");
        await GetAsync<CatalogAuthorBrowseResponse>(client, $"{Base}/authors/{ids.Author}");
        await GetAsync<CatalogSeriesBrowseResponse>(client, $"{Base}/series/{ids.Series}");
        await GetAsync<CatalogObservedAuthorBrowseResponse>(client, $"{Base}/contexts/{navigation.ContextId}/authors/{navigation.Authors.Single().SubjectId}");
        await GetAsync<CatalogObservedSeriesBrowseResponse>(client, $"{Base}/contexts/{navigation.ContextId}/series/{navigation.Series.Single().SubjectId}");
        await GetAsync<CatalogObservedBookResponse>(client, $"{Base}/contexts/{navigation.ContextId}/books/demo?externalId=d-2");
        await GetAsync<CatalogSearchResponse>(client, "/api/v1/catalog/search?q=Readonly");

        var after = await SnapshotAsync();
        foreach (var table in SnapshotTables) Assert.AreEqual(before[table], after[table], $"{table} changed.");
    }

    [TestMethod]
    public async Task ExplicitFollowStillRequiresAnAntiForgeryTokenAndAuthenticationAndChangesOnlyFollows()
    {
        await using var factory = FactoryFor(new ProviderSpy(), []);
        var authorId = await WithDatabaseAsync(factory, database =>
        {
            var author = new Author(Unique("Followed"), null, DateTimeOffset.UtcNow);
            database.Authors.Add(author);
            return Task.FromResult(author.Id);
        });
        using var anonymous = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await anonymous.PostAsync($"/api/v1/me/follows/authors/{authorId}", null)).StatusCode);

        using var noToken = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await noToken.PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = WebTestFixture.UserEmail, Password = WebTestFixture.UserPassword });
        var before = await SnapshotAsync();
        Assert.AreEqual(HttpStatusCode.BadRequest, (await noToken.PostAsync($"/api/v1/me/follows/authors/{authorId}", null)).StatusCode,
            "A cookie-authenticated mutation without a token is refused.");
        Assert.AreEqual(before["following.follows"], (await SnapshotAsync())["following.follows"]);

        using var client = await SignedInAsync(factory);
        var followed = await client.PostAsync($"/api/v1/me/follows/authors/{authorId}", null);
        Assert.IsTrue(followed.IsSuccessStatusCode, followed.StatusCode.ToString());
        var after = await SnapshotAsync();
        Assert.AreNotEqual(before["following.follows"], after["following.follows"], "The explicit action did write.");
        foreach (var table in SnapshotTables.Where(table => table is not "following.follows" and not "audit.audit_events"))
            Assert.AreEqual(before[table], after[table], $"{table} changed beyond the follow.");
    }

    [TestMethod]
    public async Task ExplicitRequestCreationStillRequiresAuthenticationAndAnAntiForgeryToken()
    {
        await using var factory = FactoryFor(new ProviderSpy(), []);
        using var anonymous = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var noToken = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await noToken.PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = WebTestFixture.UserEmail, Password = WebTestFixture.UserPassword });
        var body = JsonContent.Create(new { workId = Guid.NewGuid(), mediaType = "Ebook" });

        Assert.AreEqual(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/v1/requests/", body)).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await noToken.PostAsync("/api/v1/requests/", JsonContent.Create(new { workId = Guid.NewGuid(), mediaType = "Ebook" }))).StatusCode);
    }

    // ---------------------------------------------------------------- authorization and isolation

    [TestMethod]
    public async Task AnonymousCallersAreChallengedOnEveryBrowseRoute()
    {
        await using var factory = FactoryFor(new ProviderSpy(), []);
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var id = Guid.NewGuid();

        foreach (var url in new[]
                 {
                     $"{Base}/authors/{id}", $"{Base}/series/{id}", $"{Base}/contexts/{id}/authors/{id}",
                     $"{Base}/contexts/{id}/series/{id}", $"{Base}/contexts/{id}/books/demo?externalId=x"
                 })
        {
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.GetAsync(url)).StatusCode, url);
        }
    }

    [TestMethod]
    public async Task AnotherUsersContextWrongKindUnknownAndEvictedContextsAreIndistinguishable404s()
    {
        var spy = new ProviderSpy();
        await using var factory = FactoryFor(spy, [Book("demo", "d-1", "Private One", ["Private Author"], new BookSeriesCandidate("Private Saga", "1", true))]);
        using var reader = await SignedInAsync(factory);
        using var admin = await SignedInAsync(factory, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword);
        var run = await PollUntilAsync(reader, await StartRunAsync(reader, "Private"), r => r.IsComplete);
        var navigation = run.Search.Results.Single().Navigation!;
        var author = navigation.Authors.Single().SubjectId;
        var series = navigation.Series.Single().SubjectId;
        var ctx = navigation.ContextId;
        Assert.AreEqual(HttpStatusCode.OK, (await reader.GetAsync($"{Base}/contexts/{ctx}/authors/{author}")).StatusCode);

        var denied = new[]
        {
            await admin.GetAsync($"{Base}/contexts/{ctx}/authors/{author}"),
            await admin.GetAsync($"{Base}/contexts/{ctx}/series/{series}"),
            await admin.GetAsync($"{Base}/contexts/{ctx}/books/demo?externalId=d-1"),
            await reader.GetAsync($"{Base}/contexts/{ctx}/series/{author}"),
            await reader.GetAsync($"{Base}/contexts/{ctx}/authors/{series}"),
            await reader.GetAsync($"{Base}/contexts/{Guid.NewGuid()}/authors/{author}"),
            await reader.GetAsync($"{Base}/contexts/{ctx}/authors/{Guid.NewGuid()}"),
            await reader.GetAsync($"{Base}/contexts/{ctx}/books/demo?externalId=unknown"),
            await reader.GetAsync($"{Base}/contexts/{ctx}/books/other-source?externalId=d-1")
        };

        foreach (var response in denied) Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode, response.RequestMessage!.RequestUri!.ToString());
        var bodies = await Task.WhenAll(denied.Select(response => response.Content.ReadAsStringAsync()));
        Assert.HasCount(1, bodies.Distinct(), "Every refusal has the same body, so none reveals which condition failed.");
    }

    [TestMethod]
    public async Task ContextsExpireAbsolutelyEvenWhenReadRepeatedlyAndWithoutAnyInsert()
    {
        var clock = new PinnedClock(new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero));
        await using var factory = FactoryFor(new ProviderSpy(), [Book("demo", "d-1", "Timed", ["Timed Author"])], clock: clock);
        using var client = await SignedInAsync(factory);
        var navigation = (await GetAsync<CatalogBookCandidateResponse>(client, "/api/v1/catalog/candidates/demo/d-1")).Navigation!;
        var url = $"{Base}/contexts/{navigation.ContextId}/authors/{navigation.Authors.Single().SubjectId}";
        Assert.AreEqual(clock.UtcNow.AddMinutes(10), navigation.ExpiresAtUtc);

        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync(url)).StatusCode);
        clock.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59));
        Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync(url)).StatusCode, "Still inside the original ten minutes.");
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync(url)).StatusCode, "Reads did not slide the expiry.");
        Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync($"{Base}/contexts/{navigation.ContextId}/books/demo?externalId=d-1")).StatusCode);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("?externalId=")]
    public async Task ACachedBookRequestNeedsAnExternalReference(string query)
    {
        await using var factory = FactoryFor(new ProviderSpy(), []);
        using var client = await SignedInAsync(factory);

        Assert.AreEqual(HttpStatusCode.BadRequest, (await client.GetAsync($"{Base}/contexts/{Guid.NewGuid()}/books/demo{query}")).StatusCode);
    }

    [TestMethod]
    public async Task OversizedReferencesAreRejectedAndReservedCharactersRoundTrip()
    {
        var spy = new ProviderSpy();
        var odd = "id/with space?&=#+é";
        await using var factory = FactoryFor(spy, [Book("demo", odd, "Odd Id Book", ["Odd Author"])]);
        using var client = await SignedInAsync(factory);
        var context = Guid.NewGuid();

        Assert.AreEqual(HttpStatusCode.BadRequest, (await client.GetAsync($"{Base}/contexts/{context}/books/demo?externalId={new string('e', 513)}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await client.GetAsync($"{Base}/contexts/{context}/books/{new string('p', 129)}?externalId=x")).StatusCode);

        var run = await PollUntilAsync(client, await StartRunAsync(client, "Odd"), r => r.IsComplete);
        var navigation = run.Search.Results.Single().Navigation!;
        var book = await GetAsync<CatalogObservedBookResponse>(client,
            $"{Base}/contexts/{navigation.ContextId}/books/demo?externalId={Uri.EscapeDataString(odd)}");
        Assert.AreEqual(odd, book.Book.ExternalId, "Punctuation and Unicode in an external id survive the query parameter.");
    }

    [TestMethod]
    public async Task ADisabledAccountLosesItsSessionAndCannotReachBrowse()
    {
        await using var isolated = await WebTestFixture.CreateAsync();
        var disposable = WebTestFixture.Require(isolated);
        using var reader = await disposable.CreateUserClientAsync();
        using var admin = await disposable.CreateAdminClientAsync();
        admin.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(admin));
        var me = await reader.GetFromJsonAsync<CurrentUserResponse>("/api/v1/me");
        var anyId = Guid.NewGuid();
        Assert.AreEqual(HttpStatusCode.NotFound, (await reader.GetAsync($"{Base}/authors/{anyId}")).StatusCode, "Signed in: reaches the handler.");

        var disabled = await admin.PutAsJsonAsync($"/api/v1/admin/accounts/{me!.Id}/status", new { Status = "Disabled" });
        Assert.IsTrue(disabled.IsSuccessStatusCode, await disabled.Content.ReadAsStringAsync());

        Assert.AreEqual(HttpStatusCode.Unauthorized, (await reader.GetAsync($"{Base}/authors/{anyId}")).StatusCode);
        using var fresh = disposable.CreateAnonymousClient();
        var login = await fresh.PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = WebTestFixture.UserEmail, Password = WebTestFixture.UserPassword });
        Assert.AreNotEqual(HttpStatusCode.NoContent, login.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await fresh.GetAsync($"{Base}/authors/{anyId}")).StatusCode);
    }

    // ---------------------------------------------------------------- compatibility

    [TestMethod]
    public void ExistingCandidateResponseConstructionAndJsonRemainCompatible()
    {
        var legacy = new CatalogBookCandidateResponse(
            "p", "P", "e", "T", ["A"], null, null, null, [], [], null, null, [], null, "Exact", null, "en", "Novel", "Novel", "Explained");
        Assert.IsNull(legacy.Navigation);
        Assert.IsEmpty(legacy.Sources);

        const string oldJson = """
            {"providerId":"p","providerName":"P","externalId":"e","title":"T","authors":["A"],"description":null,"coverUrl":null,
             "publicationDate":null,"editions":[],"series":[{"name":"S","positionLabel":"1","isPrimary":true,"id":null}],
             "publisher":null,"pageCount":null,"subjects":[],"sourceUrl":null,"matchKind":"Exact","sources":[],"language":null,
             "versionKind":"Novel","versionLabel":"Novel","versionDescription":"Explained"}
            """;
        var parsed = JsonSerializer.Deserialize<CatalogBookCandidateResponse>(oldJson, WebJson)!;
        Assert.IsNull(parsed.Navigation, "A payload from before navigation existed still reads.");
        Assert.AreEqual("S", parsed.Series.Single().Name);

        var navigation = new CatalogBookNavigationResponse(
            Guid.NewGuid(), DateTimeOffset.UtcNow, [new CatalogNavigationReferenceResponse(0, "A", Guid.NewGuid())], [], []);
        var roundTrip = JsonSerializer.Deserialize<CatalogBookCandidateResponse>(
            JsonSerializer.Serialize(legacy with { Navigation = navigation }, WebJson),
            WebJson)!;
        Assert.AreEqual(navigation.ContextId, roundTrip.Navigation!.ContextId);
        Assert.AreEqual("A", roundTrip.Navigation.Authors.Single().Label);
    }
}
