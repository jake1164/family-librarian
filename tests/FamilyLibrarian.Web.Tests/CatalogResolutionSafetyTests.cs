using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Contracts.Authentication;
using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Domain.Catalog;
using FamilyLibrarian.Domain.Following;
using FamilyLibrarian.Domain.Notifications;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// Real-repository (PostgreSQL) regressions for catalog resolution safety: a candidate whose ISBNs hit two Works, same-name
/// authors and series, and the rule that only an active provider may be asked for a record that is not already saved.
/// Every case boots the production host over its own migrated database; only the provider transport is a deterministic double.
/// </summary>
/// <remarks>
/// Provider ids are the registered ones ("demo" is enabled by the test host, "openlibrary" is disabled by it), so the production
/// enablement rule is exercised rather than bypassed.
/// </remarks>
[TestClass]
public sealed class CatalogResolutionSafetyTests
{
    private const string EnabledProvider = "demo";
    private const string DisabledProvider = "openlibrary";

    private static readonly DateTimeOffset Past = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A deterministic metadata source that counts the detail calls it receives.</summary>
    private sealed class ScriptedProvider(string id, params BookCandidate[] books) : IBookMetadataProvider
    {
        private int detailCalls;

        public Exception? Failure { get; set; }

        public int DetailCalls => Volatile.Read(ref detailCalls);

        public string Id => id;

        public string DisplayName => $"Scripted {id}";

        public Task<BookCandidateSearchPage> SearchAsync(BookSearchQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new BookCandidateSearchPage([], false));

        public Task<BookCandidate?> GetDetailsAsync(string externalId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref detailCalls);
            if (Failure is not null) throw Failure;
            return Task.FromResult(books.FirstOrDefault(book => book.ExternalId == externalId));
        }
    }

    private sealed class FixedUser(Guid? userId) : ICurrentUser
    {
        public Guid? UserId => userId;

        public string? DisplayName => null;
    }

    private static string Unique(string stem) => $"{stem} {Guid.NewGuid():N}";

    private static BookCandidate Book(
        string provider, string externalId, string title, string[] authors, BookEditionCandidate[]? editions = null, params BookSeriesCandidate[] series) =>
        new(provider, $"Scripted {provider}", externalId, title, authors, "Fictional.", null, new DateOnly(2020, 1, 1), editions ?? [], series);

    private static BookEditionCandidate Edition(string title, string isbn13) => new(title, isbn13, "Ebook", new DateOnly(2020, 1, 1));

    private static async Task<string> NewDatabaseAsync()
    {
        if (PostgresFixture.UnavailableReason is not null) Assert.Inconclusive(PostgresFixture.UnavailableReason);
        return await PostgresFixture.CreateSeededDatabaseAsync();
    }

    private static FamilyLibrarianAppFactory Host(
        string connection, ScriptedProvider[] providers, Action<IServiceCollection>? configure = null) =>
        new(connection, services =>
        {
            services.RemoveAll<IBookMetadataProvider>();
            foreach (var provider in providers) services.AddSingleton<IBookMetadataProvider>(provider);
            configure?.Invoke(services);
        });

    private static async Task<HttpClient> SignedInAsync(FamilyLibrarianAppFactory factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest { Email = WebTestFixture.UserEmail, Password = WebTestFixture.UserPassword });
        Assert.AreEqual(HttpStatusCode.NoContent, login.StatusCode);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(client));
        return client;
    }

    private static async Task<T> WithDatabaseAsync<T>(FamilyLibrarianAppFactory factory, Func<AppDbContext, Task<T>> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var result = await action(database);
        await database.SaveChangesAsync();
        return result;
    }

    private static async Task<Guid> ReaderIdAsync(FamilyLibrarianAppFactory factory) =>
        await WithDatabaseAsync(factory, database =>
            database.Users.Where(user => user.Email == WebTestFixture.UserEmail).Select(user => user.Id).SingleAsync());

    private static Work SeedWork(AppDbContext database, string title, params string[] isbn13s)
    {
        var work = new Work(title, null, null, null, PublicationStatus.Published, Past);
        foreach (var isbn13 in isbn13s) work.AddEdition(new Edition(work.Id, title, EditionFormat.Ebook, isbn13, null, Past));
        database.Works.Add(work);
        return work;
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var problem = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return problem.RootElement.GetProperty("code").GetString();
    }

    private static async Task<HttpResponseMessage> ResolveAsync(HttpClient client, string provider, string externalId) =>
        await client.PostAsync($"/api/v1/catalog/candidates/{provider}/{externalId}/resolve", null);

    // ---------------------------------------------------------------- multi-Work ISBN collision

    [TestMethod]
    public async Task ACandidateWhoseIsbnsHitTwoWorksIsAConflictThatWritesNothing()
    {
        var connection = await NewDatabaseAsync();
        var provider = new ScriptedProvider(EnabledProvider);
        await using var factory = Host(connection, [provider]);
        var firstIsbn = "9780000100011";
        var secondIsbn = "9780000100028";
        await WithDatabaseAsync(factory, database =>
        {
            SeedWork(database, Unique("Omnibus"), firstIsbn);
            SeedWork(database, Unique("Single novel"), secondIsbn);
            return Task.FromResult(0);
        });
        var candidate = Book(EnabledProvider, "collide-1", Unique("Both"), [Unique("Author")],
            [Edition("Both", firstIsbn), Edition("Both", secondIsbn)]);
        provider = new ScriptedProvider(EnabledProvider, candidate);
        await using var host = Host(connection, [provider]);
        var before = await WorkRowsAsync(host);
        using var client = await SignedInAsync(host);

        using var response = await ResolveAsync(client, EnabledProvider, "collide-1");

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode, "A multi-Work ISBN hit is a deliberate 409, not an unhandled 500.");
        Assert.AreEqual("catalog.isbn_collision", await ProblemCodeAsync(response));
        Assert.AreEqual(1, provider.DetailCalls, "The provider is asked once; the collision is found afterwards.");
        CollectionAssert.AreEqual(before, await WorkRowsAsync(host), "Every Work row value (timestamps, version, lengths) is unchanged.");
        Assert.AreEqual(0, await WithDatabaseAsync(host, database =>
            database.ExternalReferences.CountAsync(reference => reference.ExternalId == "collide-1")), "No reference was written.");
    }

    private static Task<string[]> WorkRowsAsync(FamilyLibrarianAppFactory factory) =>
        WithDatabaseAsync(factory, async database =>
        {
            var works = await database.Works.AsNoTracking()
                .OrderBy(work => work.Id)
                .Select(work => new { work.Id, work.CanonicalTitle, work.CatalogPageCount, work.UpdatedAtUtc, work.IsRetired })
                .ToListAsync();
            var editions = await database.Editions.AsNoTracking()
                .OrderBy(edition => edition.Id)
                .Select(edition => $"{edition.Id}|{edition.WorkId}|{edition.Isbn13}|{edition.UpdatedAtUtc:O}")
                .ToListAsync();
            var references = await database.ExternalReferences.AsNoTracking()
                .OrderBy(reference => reference.Id)
                .Select(reference => $"{reference.Id}|{reference.ProviderId}|{reference.ExternalId}|{reference.UpdatedAtUtc:O}")
                .ToListAsync();
            return works.Select(work => $"W|{work.Id}|{work.CanonicalTitle}|{work.CatalogPageCount}|{work.UpdatedAtUtc:O}|{work.IsRetired}")
                .Concat(editions.Select(row => "E|" + row))
                .Concat(references.Select(row => "R|" + row))
                .ToArray();
        });

    [TestMethod]
    public async Task AMultiWorkIsbnCollisionIsHandledInChatWithoutAnException()
    {
        var connection = await NewDatabaseAsync();
        var firstIsbn = "9780000200016";
        var secondIsbn = "9780000200023";
        var candidate = Book(EnabledProvider, "collide-chat", Unique("Both"), [Unique("Author")],
            [Edition("Both", firstIsbn), Edition("Both", secondIsbn)]);
        var provider = new ScriptedProvider(EnabledProvider, candidate);
        await using var seedHost = Host(connection, [provider]);
        await WithDatabaseAsync(seedHost, database =>
        {
            SeedWork(database, Unique("Omnibus"), firstIsbn);
            SeedWork(database, Unique("Single novel"), secondIsbn);
            return Task.FromResult(0);
        });
        var readerId = await ReaderIdAsync(seedHost);
        await using var host = Host(connection, [provider], services =>
        {
            services.RemoveAll<ICurrentUser>();
            services.AddScoped<ICurrentUser>(_ => new FixedUser(readerId));
        });
        await using var scope = host.Services.CreateAsyncScope();
        var tools = scope.ServiceProvider.GetRequiredService<FamilyLibrarian.Application.Conversation.IConversationTools>();

        var workId = await tools.ResolveWorkAsync(
            new FamilyLibrarian.Application.Conversation.ConversationChoiceRef(EnabledProvider, "collide-chat", "Both", "A", null),
            CancellationToken.None);

        Assert.IsNull(workId, "The chat's resolution result for a handled collision is no Work, not an unhandled exception.");
        Assert.AreEqual(0, await WithDatabaseAsync(host, database =>
            database.ExternalReferences.CountAsync(reference => reference.ExternalId == "collide-chat")));
    }

    // ---------------------------------------------------------------- ambiguous names (U1-1)

    private sealed record Namesakes(Guid AuthorOne, Guid AuthorTwo, Guid SeriesOne, Guid SeriesTwo, Guid Reader, int AuthorOneWorks, int AuthorTwoWorks);

    private static async Task<Namesakes> SeedNamesakesAsync(FamilyLibrarianAppFactory factory, string authorName, string seriesName, bool swapCreatedDates)
    {
        var reader = await ReaderIdAsync(factory);
        return await WithDatabaseAsync(factory, database =>
        {
            var older = Past;
            var newer = Past.AddDays(30);
            var authorOne = new Author(authorName, null, swapCreatedDates ? newer : older);
            var authorTwo = new Author(authorName, null, swapCreatedDates ? older : newer);
            var seriesOne = new Series(seriesName, SeriesStatus.Unknown, swapCreatedDates ? newer : older);
            var seriesTwo = new Series(seriesName, SeriesStatus.Unknown, swapCreatedDates ? older : newer);
            database.Authors.AddRange(authorOne, authorTwo);
            database.Series.AddRange(seriesOne, seriesTwo);
            var workOne = SeedWork(database, Unique("Prior one"));
            workOne.AddAuthor(authorOne, 0);
            workOne.AddSeriesEntry(new SeriesEntry(seriesOne, workOne, "1", 1m, true, Past));
            var workTwo = SeedWork(database, Unique("Prior two"));
            workTwo.AddAuthor(authorTwo, 0);
            workTwo.AddSeriesEntry(new SeriesEntry(seriesTwo, workTwo, "1", 1m, true, Past));
            foreach (var (type, id) in new[]
                     {
                         (FollowSubjectType.Author, authorOne.Id), (FollowSubjectType.Author, authorTwo.Id),
                         (FollowSubjectType.Series, seriesOne.Id), (FollowSubjectType.Series, seriesTwo.Id)
                     })
            {
                database.Follows.Add(new Follow(reader, type, id, Past));
            }

            return Task.FromResult(new Namesakes(authorOne.Id, authorTwo.Id, seriesOne.Id, seriesTwo.Id, reader, 1, 1));
        });
    }

    private static async Task<string[]> BrowsePagesAsync(HttpClient client, Namesakes namesakes)
    {
        var pages = new List<string>();
        foreach (var url in new[]
                 {
                     $"/api/v1/catalog/browse/authors/{namesakes.AuthorOne}", $"/api/v1/catalog/browse/authors/{namesakes.AuthorTwo}",
                     $"/api/v1/catalog/browse/series/{namesakes.SeriesOne}", $"/api/v1/catalog/browse/series/{namesakes.SeriesTwo}"
                 })
        {
            using var response = await client.GetAsync(url);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, url);
            pages.Add(await response.Content.ReadAsStringAsync());
        }

        return pages.ToArray();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ANewWorkCreditedToAnAmbiguousNameBindsToNeitherNamesakeWhicheverIsOlder(bool swapCreatedDates)
    {
        var connection = await NewDatabaseAsync();
        var authorName = Unique("Dana Namesake");
        var seriesName = Unique("Twin Series");
        var candidate = Book(EnabledProvider, "fresh-1", Unique("Fresh Book"), [authorName],
            [Edition("Fresh", "9780000300012")], new BookSeriesCandidate(seriesName, "3", true, IsCompleted: true));
        await using var factory = Host(connection, [new ScriptedProvider(EnabledProvider, candidate)]);
        var namesakes = await SeedNamesakesAsync(factory, authorName, seriesName, swapCreatedDates);
        using var client = await SignedInAsync(factory);
        var browseBefore = await BrowsePagesAsync(client, namesakes);

        Guid workId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<CatalogWorkResolver>()
                .ResolveAsync(EnabledProvider, "fresh-1", CancellationToken.None);
            Assert.IsTrue(result.WasCreated);
            Assert.AreEqual(1, result.Diagnostics.AmbiguousAuthors.Count, "The unbound credit is reported to the caller.");
            Assert.AreEqual(2, result.Diagnostics.AmbiguousAuthors[0].NamesakeAuthorIds.Count);
            Assert.AreEqual(seriesName, result.Diagnostics.OmittedSeriesMemberships.Single().ReportedName);
            workId = result.Work.Id;
        }

        await WithDatabaseAsync(factory, async database =>
        {
            var work = await database.Works.Include(w => w.Authors).ThenInclude(a => a.Author)
                .Include(w => w.SeriesEntries).SingleAsync(w => w.Id == workId);
            var credit = work.Authors.Single();
            Assert.AreNotEqual(namesakes.AuthorOne, credit.AuthorId, "Not bound to the first namesake.");
            Assert.AreNotEqual(namesakes.AuthorTwo, credit.AuthorId, "Not bound to the second namesake.");
            Assert.AreEqual(authorName, credit.Author.CanonicalName, "The reported credit name is kept.");
            Assert.IsEmpty(work.SeriesEntries, "An ambiguous series name creates no membership.");
            Assert.AreEqual(1, await database.Set<WorkAuthor>().CountAsync(link => link.AuthorId == namesakes.AuthorOne));
            Assert.AreEqual(1, await database.Set<WorkAuthor>().CountAsync(link => link.AuthorId == namesakes.AuthorTwo));
            Assert.AreEqual(1, await database.SeriesEntries.CountAsync(entry => entry.SeriesId == namesakes.SeriesOne));
            Assert.AreEqual(1, await database.SeriesEntries.CountAsync(entry => entry.SeriesId == namesakes.SeriesTwo));
            Assert.IsTrue(await database.Series.Where(series => series.NormalizedName == CatalogText.NormalizeForMatch(seriesName))
                .AllAsync(series => series.Status == SeriesStatus.Unknown), "The completed flag is not applied to a namesake.");
            Assert.AreEqual(0, await database.NotificationEvents.CountAsync(), "No follower of any namesake is notified.");
            return 0;
        });
        CollectionAssert.AreEqual(browseBefore, await BrowsePagesAsync(client, namesakes), "Existing browse responses are equivalent.");

        // Replaying the resolve adds nothing: the reference is now known.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var again = await scope.ServiceProvider.GetRequiredService<CatalogWorkResolver>()
                .ResolveAsync(EnabledProvider, "fresh-1", CancellationToken.None);
            Assert.IsFalse(again.WasCreated);
            Assert.AreEqual(workId, again.Work.Id);
        }

        Assert.AreEqual(3, await WithDatabaseAsync(factory, database =>
            database.Authors.CountAsync(author => author.NormalizedName == CatalogText.NormalizeForMatch(authorName))));
    }

    [TestMethod]
    public async Task AnUnambiguousExistingAuthorAndSeriesStillBindAndNotifyTheirFollowers()
    {
        var connection = await NewDatabaseAsync();
        var authorName = Unique("Solo Author");
        var seriesName = Unique("Solo Series");
        var candidate = Book(EnabledProvider, "solo-1", Unique("Solo Book"), [authorName],
            [Edition("Solo", "9780000400019")], new BookSeriesCandidate(seriesName, "2", true));
        await using var factory = Host(connection, [new ScriptedProvider(EnabledProvider, candidate)]);
        var reader = await ReaderIdAsync(factory);
        var ids = await WithDatabaseAsync(factory, database =>
        {
            var author = new Author(authorName, null, Past);
            var series = new Series(seriesName, SeriesStatus.Unknown, Past);
            database.Authors.Add(author);
            database.Series.Add(series);
            database.Follows.Add(new Follow(reader, FollowSubjectType.Author, author.Id, Past));
            database.Follows.Add(new Follow(reader, FollowSubjectType.Series, series.Id, Past));
            return Task.FromResult((Author: author.Id, Series: series.Id));
        });

        Guid workId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<CatalogWorkResolver>()
                .ResolveAsync(EnabledProvider, "solo-1", CancellationToken.None);
            Assert.IsTrue(result.WasCreated);
            workId = result.Work.Id;
        }

        await WithDatabaseAsync(factory, async database =>
        {
            Assert.AreEqual(1, await database.Set<WorkAuthor>().CountAsync(link => link.AuthorId == ids.Author && link.WorkId == workId));
            Assert.AreEqual(1, await database.SeriesEntries.CountAsync(entry => entry.SeriesId == ids.Series && entry.WorkId == workId));
            Assert.AreEqual(1, await database.NotificationEvents.CountAsync(e => e.Category == NotificationCategories.AuthorNewWorkDetected));
            Assert.AreEqual(1, await database.NotificationEvents.CountAsync(e => e.Category == NotificationCategories.SeriesNewEntryDetected));
            return 0;
        });
    }

    // ---------------------------------------------------------------- provider lifecycle and security

    [TestMethod]
    public async Task ADisabledProviderIsNeverCalledForAnUnknownReferenceButASavedReferenceStillResolves()
    {
        var connection = await NewDatabaseAsync();
        var disabled = new ScriptedProvider(DisabledProvider,
            Book(DisabledProvider, "ol-new", Unique("Disabled source book"), [Unique("Author")], [Edition("Disabled", "9780000500010")]));
        await using var factory = Host(connection, [disabled]);
        var savedWork = await WithDatabaseAsync(factory, database =>
        {
            var work = SeedWork(database, Unique("Saved before the source was disabled"));
            database.ExternalReferences.Add(new ExternalReference(DisabledProvider, ExternalReferenceEntityType.Work, work.Id, "ol-saved", Past));
            return Task.FromResult(work.Id);
        });
        using var client = await SignedInAsync(factory);

        using var unknown = await ResolveAsync(client, DisabledProvider, "ol-new");
        using var saved = await ResolveAsync(client, DisabledProvider, "ol-saved");

        Assert.AreEqual(HttpStatusCode.Conflict, unknown.StatusCode, "A registered but disabled provider is a typed 409.");
        Assert.AreEqual("catalog.provider_not_enabled", await ProblemCodeAsync(unknown));
        Assert.AreEqual(0, disabled.DetailCalls, "A disabled provider is never invoked, for the unknown or the saved reference.");
        Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, "Saved references resolve provider-free.");
        Assert.AreEqual(savedWork, (await saved.Content.ReadFromJsonAsync<CatalogWorkResponse>())!.Id);
        Assert.AreEqual(0, await WithDatabaseAsync(factory, database =>
            database.ExternalReferences.CountAsync(reference => reference.ExternalId == "ol-new")));
    }

    [TestMethod]
    public async Task AnUnregisteredProviderIdIsStillNotFound()
    {
        var connection = await NewDatabaseAsync();
        await using var factory = Host(connection, [new ScriptedProvider(EnabledProvider)]);
        using var client = await SignedInAsync(factory);

        using var response = await ResolveAsync(client, "no-such-provider", "anything");

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.AreEqual("catalog.provider_unknown", await ProblemCodeAsync(response));
    }

    [TestMethod]
    public async Task ProviderFailuresAndAMissingRecordAreMappedNotEscalated()
    {
        var connection = await NewDatabaseAsync();
        var provider = new ScriptedProvider(EnabledProvider);
        await using var factory = Host(connection, [provider]);
        using var client = await SignedInAsync(factory);

        using var missing = await ResolveAsync(client, EnabledProvider, "absent");
        provider.Failure = new HttpRequestException("The source is down.");
        using var down = await ResolveAsync(client, EnabledProvider, "absent");
        provider.Failure = new TaskCanceledException("The source timed out.");
        using var timedOut = await ResolveAsync(client, EnabledProvider, "absent");

        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.AreEqual("catalog.candidate_not_found", await ProblemCodeAsync(missing));
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, down.StatusCode);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, timedOut.StatusCode);
        Assert.AreEqual(0, await WithDatabaseAsync(factory, database => database.Works.CountAsync()));
    }

    [TestMethod]
    public async Task ResolveStillRequiresASignedInUser()
    {
        var connection = await NewDatabaseAsync();
        await using var factory = Host(connection, [new ScriptedProvider(EnabledProvider)]);
        using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await anonymous.PostAsync($"/api/v1/catalog/candidates/{EnabledProvider}/anything/resolve", null);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task ResolveStillRequiresTheAntiforgeryToken()
    {
        var connection = await NewDatabaseAsync();
        await using var factory = Host(connection, [new ScriptedProvider(EnabledProvider)]);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest { Email = WebTestFixture.UserEmail, Password = WebTestFixture.UserPassword });
        Assert.AreEqual(HttpStatusCode.NoContent, login.StatusCode);

        using var response = await ResolveAsync(client, EnabledProvider, "anything");

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, "A cookie-authenticated POST without the anti-forgery header is refused.");
    }

    // ---------------------------------------------------------------- known references stay a read

    [TestMethod]
    public async Task RepeatedResolveOfAKnownReferenceMakesNoProviderCallAndChangesNoRowValue()
    {
        var connection = await NewDatabaseAsync();
        var provider = new ScriptedProvider(EnabledProvider,
            Book(EnabledProvider, "known-1", Unique("Known book"), [Unique("Known Author")], [Edition("Known", "9780000700013")]));
        await using var factory = Host(connection, [provider]);
        using var client = await SignedInAsync(factory);
        using var created = await ResolveAsync(client, EnabledProvider, "known-1");
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
        Assert.IsNotNull(created.Headers.Location, "A newly created Work keeps its Location.");
        var callsAfterCreation = provider.DetailCalls;
        var before = await WorkRowsAsync(factory);

        for (var repeat = 0; repeat < 5; repeat++)
        {
            using var again = await ResolveAsync(client, EnabledProvider, "known-1");
            Assert.AreEqual(HttpStatusCode.OK, again.StatusCode, "Reuse stays a 200.");
        }

        Assert.AreEqual(callsAfterCreation, provider.DetailCalls, "A known reference is never refetched.");
        CollectionAssert.AreEqual(before, await WorkRowsAsync(factory), "No Work, edition or reference value (including timestamps) moved.");
    }
}
