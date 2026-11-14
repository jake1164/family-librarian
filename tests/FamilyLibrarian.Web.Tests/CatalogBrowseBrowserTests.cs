// Playwright locator text lists and expected-value arrays are one-shot literals; CA1861 targets repeated calls.
#pragma warning disable CA1861
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using IClock = FamilyLibrarian.Application.Abstractions.IClock;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Domain.Catalog;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Catalog;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Playwright;
using Npgsql;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// Real-browser acceptance for author and series navigation: the whole stack (migrated PostgreSQL, the real host and
/// Blazor client served from Kestrel on a loopback port, Chromium) with deterministic invented metadata providers.
/// Nothing here talks to a real metadata service or uses household data. Opt in with
/// <c>FAMILY_LIBRARIAN_CATALOG_BROWSE_BROWSER_TESTS=1</c>; the class is also in the Release category.
/// </summary>
[TestClass]
[TestCategory("Release")]
public sealed class CatalogBrowseBrowserTests
{
    private const string OptInVariable = "FAMILY_LIBRARIAN_CATALOG_BROWSE_BROWSER_TESTS";
    private static readonly Regex Guid36 = new("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", RegexOptions.Compiled);
    private static WebTestFixture? fixture;

    [ClassInitialize]
    public static async Task InitializeAsync(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (Environment.GetEnvironmentVariable(OptInVariable) == "1") fixture = await WebTestFixture.CreateAsync();
    }

    [ClassCleanup]
    public static async Task CleanupAsync()
    {
        if (fixture is not null) await fixture.DisposeAsync();
    }

    private static void RequireOptIn()
    {
        if (Environment.GetEnvironmentVariable(OptInVariable) != "1")
            Assert.Inconclusive($"Set {OptInVariable}=1 for the isolated catalog-browse browser acceptance tests.");
    }

    // ---------------------------------------------------------------- deterministic metadata providers

    /// <summary>Counts every metadata-provider call so a test can prove that browsing made none.</summary>
    private sealed class Spy
    {
        private int searches;
        private int details;
        private int cancelledSearches;

        public int Searches => Volatile.Read(ref searches);

        public int Details => Volatile.Read(ref details);

        public int CancelledSearches => Volatile.Read(ref cancelledSearches);

        public volatile bool Offline;

        public Dictionary<string, BookCandidate> DetailOverrides { get; } = [];

        public void CountSearch() => Interlocked.Increment(ref searches);

        public void CountDetails() => Interlocked.Increment(ref details);

        public void CountCancelled() => Interlocked.Increment(ref cancelledSearches);
    }

    private sealed class ScriptedProvider(string id, string name, BookCandidate[] books, Spy spy, SemaphoreSlim? gate = null)
        : IBookMetadataProvider
    {
        public string Id => id;

        public string DisplayName => name;

        public async Task<BookCandidateSearchPage> SearchAsync(BookSearchQuery query, CancellationToken cancellationToken)
        {
            spy.CountSearch();
            if (gate is not null)
            {
                try
                {
                    await gate.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    spy.CountCancelled();
                    throw;
                }
            }

            return new BookCandidateSearchPage(books, false);
        }

        public Task<BookCandidate?> GetDetailsAsync(string externalId, CancellationToken cancellationToken)
        {
            spy.CountDetails();
            if (spy.Offline) throw new HttpRequestException("The source is down.");
            if (spy.DetailOverrides.TryGetValue(externalId, out var overridden)) return Task.FromResult<BookCandidate?>(overridden);
            return Task.FromResult(books.FirstOrDefault(book => book.ExternalId == externalId));
        }
    }

    private sealed class PinnedClock(DateTimeOffset start) : IClock
    {
        private long ticks = start.UtcTicks;

        public DateTimeOffset UtcNow => new(Interlocked.Read(ref ticks), TimeSpan.Zero);

        public void Advance(TimeSpan by) => Interlocked.Add(ref ticks, by.Ticks);
    }

    private static BookCandidate Book(
        string provider, string external, string title, string[] authors, string? description = null,
        BookEditionCandidate[]? editions = null, params BookSeriesCandidate[] series) =>
        new(provider, provider == "demo" ? "Demo Source" : "Second Source", external, title, authors, description, null, null,
            editions ?? [], series);

    private static BookSeriesCandidate InSeries(string name, string? label, bool primary = true) => new(name, label, primary);

    /// <summary>Invented, deterministic source records: ties, a fractional position, an unknown one and a source-defined ordering.</summary>
    private static BookCandidate[] DemoBooks() =>
    [
        Book("demo", "h-1", "The Lantern Keeper", ["Mara Vell"], "A lighthouse story.",
            [new BookEditionCandidate("The Lantern Keeper", "9781111111111", "Ebook", null)],
            InSeries("Tidewatch Saga", "1"), InSeries("Tidewatch Saga (Chronological)", "3", false)),
        Book("demo", "h-2", "Salt and Signal", ["Mara Vell"], "A second story.", null, InSeries("Tidewatch Saga", "2.5")),
        Book("demo", "h-3", "Beacon Tied A", ["Mara Vell"], "Tied.", null, InSeries("Tidewatch Saga", "3")),
        Book("demo", "h-4", "Beacon Tied B", ["Mara Vell"], "Tied.", null, InSeries("Tidewatch Saga", "3")),
        Book("demo", "h-5", "Unnumbered Tale", ["Mara Vell"], "No position given.", null, InSeries("Tidewatch Saga", null)),
        Book("demo", "h-6", "Dawn of Tides", ["Mara Vell"], "A prequel.", null, InSeries("Tidewatch Saga (Chronological)", "1")),
        Book("demo", "h-7", "Solo Voyage", ["Mara Vell", "Pip Pilot"], "Presented in comic book format."),
        Book("demo", "bare-1", "Untitled Sketch", [], null)
    ];

    private static BookCandidate[] SecondBooks() =>
    [
        Book("gutendex", "dup-1", "The Lantern Keeper", ["Mara Vell"], "A lighthouse story.",
            [new BookEditionCandidate("The Lantern Keeper", "9782222222222", "Audiobook", null)])
    ];

    // ---------------------------------------------------------------- browser harness

    private sealed class Harness : IAsyncDisposable
    {
        public required WebApplicationFactory<Program> Factory { get; init; }

        public required FamilyLibrarianAppFactory Original { get; init; }

        public required Uri BaseUri { get; init; }

        public required IPlaywright Playwright { get; init; }

        public required IBrowser Browser { get; init; }

        public required IPage Page { get; init; }

        public required Spy Spy { get; init; }

        public List<string> ClientErrors { get; } = [];

        public Uri Url(string path) => new(BaseUri, path);

        public async ValueTask DisposeAsync()
        {
            await Browser.DisposeAsync();
            Playwright.Dispose();
            await Factory.DisposeAsync();
            await Original.DisposeAsync();
        }
    }

    private static async Task<Harness> StartAsync(
        Spy? spy = null, SemaphoreSlim? secondGate = null, bool withProviders = true, IClock? clock = null,
        int width = 1440, bool signIn = true)
    {
        RequireOptIn();
        spy ??= new Spy();
        var original = new FamilyLibrarianAppFactory(WebTestFixture.Require(fixture).ConnectionString, services =>
        {
            services.RemoveAll<IBookMetadataProvider>();
            if (withProviders)
            {
                services.AddSingleton<IBookMetadataProvider>(new ScriptedProvider("demo", "Demo Source", DemoBooks(), spy));
                services.AddSingleton<IBookMetadataProvider>(new ScriptedProvider(
                    "gutendex", "Second Source", SecondBooks(), spy, secondGate ?? new SemaphoreSlim(0, 1)));
            }

            services.AddHostedService<CatalogSearchRunHostedService>();
            if (clock is not null)
            {
                services.RemoveAll<IClock>();
                services.AddSingleton(clock);
            }
        });
        var factory = original.WithWebHostBuilder(builder => builder.UseStaticWebAssets());
        factory.UseKestrel(0);
        using var client = factory.CreateClient();
        var baseUri = client.BaseAddress!;
        var playwright = await Playwright.CreateAsync();
        var browser = await playwright.Chromium.LaunchAsync(new()
        {
            Headless = true,
            ExecutablePath = Environment.GetEnvironmentVariable("FAMILY_LIBRARIAN_E2E_CHROMIUM_EXECUTABLE")
        });
        var page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = width, Height = 1000 }, TimezoneId = "UTC", Locale = "en-US" });
        var harness = new Harness { Factory = factory, Original = original, BaseUri = baseUri, Playwright = playwright, Browser = browser, Page = page, Spy = spy };
        page.PageError += (_, message) => harness.ClientErrors.Add($"pageerror: {message}");
        page.Console += (_, message) =>
        {
            // A 4xx/5xx the page provokes on purpose (an expired preview) is logged by the browser as a failed resource.
            if (message.Type == "error" && !message.Text.Contains("Failed to load resource", StringComparison.Ordinal) &&
                !message.Text.Contains("report-only policy", StringComparison.Ordinal))
                harness.ClientErrors.Add($"console: {message.Text}");
        };
        if (signIn) await SignInAsync(page, baseUri, WebTestFixture.UserEmail, WebTestFixture.UserPassword);
        return harness;
    }

    private static async Task SignInAsync(IPage page, Uri baseUri, string email, string password)
    {
        await page.GotoAsync(new Uri(baseUri, "/search").ToString());
        await page.GetByLabel("Email", new() { Exact = true }).FillAsync(email);
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync(password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByLabel("Title, author, or ISBN")).ToBeVisibleAsync();
    }

    private static async Task SearchAsync(IPage page, string text)
    {
        await page.GetByLabel("Title, author, or ISBN").FillAsync(text);
        await page.GetByRole(AriaRole.Button, new() { Name = "Search", Exact = true }).ClickAsync();
    }

    private static ILocator Link(IPage page, string name) =>
        page.GetByRole(AriaRole.Link, new() { Name = name, Exact = true });

    private static async Task ExpectUrlAsync(IPage page, string pattern) =>
        await Assertions.Expect(page).ToHaveURLAsync(new Regex(pattern));

    private static async Task AssertNoHorizontalOverflowAsync(IPage page) =>
        Assert.IsFalse(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth > window.innerWidth"),
            "The page scrolls sideways.");

    private static async Task EventuallyAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"Timed out waiting for: {because}");
            await Task.Delay(50);
        }
    }

    private static void AssertNoClientErrors(Harness harness) =>
        Assert.IsEmpty(harness.ClientErrors, string.Join(Environment.NewLine, harness.ClientErrors));

    // ---------------------------------------------------------------- catalog seeding (fixture data, not navigation)

    private sealed record SavedCatalog(
        Guid NoraA, Guid NoraB, Guid ChronicleOne, Guid ChronicleTwo, Guid ChronicleChrono,
        Guid Dawn, Guid Noon, Guid TieOne, Guid TieTwo, Guid Unknown, Guid Standalone, Guid OtherNora, Guid Retired);

    private static async Task<T> WithDatabaseAsync<T>(WebApplicationFactory<Program> factory, Func<AppDbContext, T> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var result = action(database);
        await database.SaveChangesAsync();
        return result;
    }

    private static Work NewWork(AppDbContext database, string title, Author? author, DateOnly? published = null, bool retired = false)
    {
        var work = new Work(title, null, null, published, PublicationStatus.Published, DateTimeOffset.UtcNow);
        if (retired) work.Retire(DateTimeOffset.UtcNow);
        if (author is not null) work.AddAuthor(author, 0, "Author");
        database.Works.Add(work);
        return work;
    }

    private static void Join(Work work, Series series, string? label, decimal? sort) =>
        work.AddSeriesEntry(new SeriesEntry(series, work, label, sort, true, DateTimeOffset.UtcNow));

    /// <summary>
    /// Two saved authors named alike, three series (two alike, one source-defined chronological order), a work in two
    /// series, ties, an unknown position, an unseries'd work, a retired work and a source reference/edition formats.
    /// </summary>
    private static Task<SavedCatalog> SeedAsync(WebApplicationFactory<Program> factory, string suffix) =>
        WithDatabaseAsync(factory, database =>
        {
            var noraA = new Author($"Nora Tamsin{suffix}", null, DateTimeOffset.UtcNow);
            var noraB = new Author($"Nora Tamsin{suffix}", null, DateTimeOffset.UtcNow);
            var one = new Series($"Harbor Chronicles{suffix}", SeriesStatus.Active, DateTimeOffset.UtcNow);
            var two = new Series($"Harbor Chronicles{suffix}", SeriesStatus.Unknown, DateTimeOffset.UtcNow);
            var chrono = new Series($"Harbor Chronicles{suffix} (Chronological)", SeriesStatus.Unknown, DateTimeOffset.UtcNow);
            database.Authors.AddRange(noraA, noraB);
            database.Series.AddRange(one, two, chrono);

            var dawn = NewWork(database, $"Saved Dawn{suffix}", noraA, new DateOnly(2001, 3, 4));
            Join(dawn, one, "1", 1m);
            var noon = NewWork(database, $"Saved Noon{suffix}", noraA);
            Join(noon, one, "2.5", 2.5m);
            Join(noon, chrono, "2", 2m);
            var tieOne = NewWork(database, $"Saved Tie One{suffix}", noraA);
            Join(tieOne, one, "3", 3m);
            var tieTwo = NewWork(database, $"Saved Tie Two{suffix}", noraA);
            Join(tieTwo, one, "3", 3m);
            var unknown = NewWork(database, $"Saved Unknown{suffix}", noraA);
            Join(unknown, one, null, null);
            var standalone = NewWork(database, $"Saved Standalone{suffix}", noraA);
            var other = NewWork(database, $"Other Nora Book{suffix}", noraB);
            Join(other, two, "1", 1m);
            var retired = NewWork(database, $"Retired Book{suffix}", noraA, retired: true);
            Join(retired, one, "9", 9m);

            database.Editions.Add(new Edition(dawn.Id, "Saved Dawn ebook", EditionFormat.Ebook, null, null, DateTimeOffset.UtcNow));
            database.Editions.Add(new Edition(noon.Id, "Saved Noon audio", EditionFormat.Audiobook, null, null, DateTimeOffset.UtcNow));
            database.ExternalReferences.Add(new ExternalReference(
                "demo", ExternalReferenceEntityType.Work, dawn.Id, $"saved-dawn{suffix}", new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)));

            return new SavedCatalog(noraA.Id, noraB.Id, one.Id, two.Id, chrono.Id,
                dawn.Id, noon.Id, tieOne.Id, tieTwo.Id, unknown.Id, standalone.Id, other.Id, retired.Id);
        });

    private static readonly string[] SnapshotTables =
    [
        "catalog.authors", "catalog.works", "catalog.work_authors", "catalog.editions", "catalog.series",
        "catalog.series_entries", "catalog.external_references", "requests.book_requests", "requests.request_participants",
        "requests.request_formats", "requests.request_status_history", "following.follows", "notifications.notification_events",
        "notifications.notification_receipts", "communications.outbound_communications",
        "communications.outbound_communication_deliveries"
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

    private static void AssertUnchanged(Dictionary<string, string> before, Dictionary<string, string> after, params string[] allowedToChange)
    {
        foreach (var table in SnapshotTables.Where(table => !allowedToChange.Contains(table)))
            Assert.AreEqual(before[table], after[table], $"{table} changed during navigation.");
    }

    // ================================================================ journey 1: saved work -> author

    [TestMethod]
    [DataRow(1440)]
    [DataRow(390)]
    public async Task SavedWorkToAuthorGroupsKnownWorksBySeriesThenAnotherWorkBackToAuthorAndRefreshWithoutProviders(int width)
    {
        var spy = new Spy();
        await using var app = await StartAsync(spy, withProviders: false, width: width);
        var suffix = $" {Guid.NewGuid():N}"[..9];
        var saved = await SeedAsync(app.Factory, suffix);
        var page = app.Page;
        var before = await SnapshotAsync();

        await page.GotoAsync(app.Url($"/works/{saved.Dawn}").ToString());
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = $"Saved Dawn{suffix}" })).ToBeVisibleAsync();
        // Keyboard: the author name is a real link and Enter follows it.
        var authorLink = Link(page, $"Nora Tamsin{suffix}");
        await authorLink.FocusAsync();
        await page.Keyboard.PressAsync("Enter");

        await ExpectUrlAsync(page, $"/authors/{saved.NoraA}$");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = $"Nora Tamsin{suffix}", Level = 1 })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText("Known books by this author")).ToBeVisibleAsync();

        // Grouped by saved series; the source-defined chronological order stays its own group; no-series books say so.
        await Assertions.Expect(page.Locator("section.browse-group h2")).ToHaveTextAsync(
            [$"Harbor Chronicles{suffix}", $"Harbor Chronicles{suffix} (Chronological)", "No known series"]);
        var first = page.Locator("section.browse-group").First;
        await Assertions.Expect(first.Locator(".browse-book-title")).ToHaveTextAsync(
            [$"Saved Dawn{suffix}", $"Saved Noon{suffix}", $"Saved Tie One{suffix}", $"Saved Tie Two{suffix}", $"Saved Unknown{suffix}"]);
        await Assertions.Expect(first.Locator(".browse-position")).ToHaveTextAsync(
            ["Position 1", "Position 2.5", "Position 3", "Position 3", "Position unknown"]);
        await Assertions.Expect(first.Locator(".browse-tie")).ToHaveCountAsync(2);
        await Assertions.Expect(page.Locator(".browse-no-series .browse-book-title")).ToHaveTextAsync([$"Saved Standalone{suffix}"]);
        await Assertions.Expect(page.GetByText("does not mean they stand alone")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText($"Retired Book{suffix}")).ToHaveCountAsync(0);
        // Known formats and the source are shown; unknowns stay unknown; the list is qualified.
        await Assertions.Expect(first.Locator(".browse-book-row").First.GetByText("Ebook", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(first.Locator(".browse-book-row").First.GetByText("Source: demo (seen 2 Jan 2026)")).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".browse-notes")).ToContainTextAsync("not a complete bibliography");
        if (width == 390) await AssertNoHorizontalOverflowAsync(page);

        // Another saved work, then back to the author, then to search.
        await Link(page, $"Saved Noon{suffix}").First.ClickAsync();
        await ExpectUrlAsync(page, $"/works/{saved.Noon}\\?back=author&bid={saved.NoraA}$");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = $"Saved Noon{suffix}" })).ToBeVisibleAsync();
        await Assertions.Expect(Link(page, "Back to search")).ToBeVisibleAsync();
        await Link(page, "Back to author").ClickAsync();
        await ExpectUrlAsync(page, $"/authors/{saved.NoraA}$");

        // Direct refresh of a persisted route works from saved data with providers off.
        await page.ReloadAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = $"Nora Tamsin{suffix}", Level = 1 })).ToBeVisibleAsync();
        await Assertions.Expect(first.Locator(".browse-book-title")).ToHaveCountAsync(5);
        if (width == 390) await AssertNoHorizontalOverflowAsync(page);

        Assert.AreEqual((0, 0), (spy.Searches, spy.Details), "Saved browsing never calls a provider.");
        AssertUnchanged(before, await SnapshotAsync());
        AssertNoClientErrors(app);
    }

    // ================================================================ journey 2: saved work -> each series

    [TestMethod]
    [DataRow(1440)]
    [DataRow(390)]
    public async Task SavedWorkToEachOfItsSeriesShowsRecordedOrderTiesAndUnknownsThenAnotherBookAndBack(int width)
    {
        await using var app = await StartAsync(withProviders: false, width: width);
        var suffix = $" {Guid.NewGuid():N}"[..9];
        var saved = await SeedAsync(app.Factory, suffix);
        var page = app.Page;

        await page.GotoAsync(app.Url($"/works/{saved.Noon}").ToString());
        // Both memberships are separate links, the full ordering label kept.
        await Assertions.Expect(page.Locator(".work-series-link")).ToHaveCountAsync(2);
        CollectionAssert.AreEquivalent(
            new[] { $"Harbor Chronicles{suffix}", $"Harbor Chronicles{suffix} (Chronological)" },
            (await page.Locator(".work-series-link").AllInnerTextsAsync()).ToArray());

        await Link(page, $"Harbor Chronicles{suffix}").ClickAsync();
        await ExpectUrlAsync(page, $"/series/{saved.ChronicleOne}$");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = $"Harbor Chronicles{suffix}", Level = 1 })).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".browse-book-title")).ToHaveTextAsync(
            [$"Saved Dawn{suffix}", $"Saved Noon{suffix}", $"Saved Tie One{suffix}", $"Saved Tie Two{suffix}", $"Saved Unknown{suffix}"]);
        await Assertions.Expect(page.Locator(".browse-position")).ToHaveTextAsync(
            ["Position 1", "Position 2.5", "Position 3", "Position 3", "Position unknown"]);
        await Assertions.Expect(page.Locator(".browse-tie")).ToHaveCountAsync(2);
        await Assertions.Expect(page.GetByText("Unknown positions are listed last.")).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".browse-notes")).ToContainTextAsync("not a complete bibliography");
        // Never claims completeness or a latest entry.
        var text = await page.Locator("main, .mud-main-content").First.InnerTextAsync();
        foreach (var banned in new[] { "all books", "complete series", "latest" })
            Assert.DoesNotContain(banned, text, StringComparison.OrdinalIgnoreCase);
        if (width == 390) await AssertNoHorizontalOverflowAsync(page);

        await Link(page, $"Saved Tie Two{suffix}").ClickAsync();
        await ExpectUrlAsync(page, $"/works/{saved.TieTwo}\\?back=series&bid={saved.ChronicleOne}$");
        await Link(page, "Back to series").ClickAsync();
        await ExpectUrlAsync(page, $"/series/{saved.ChronicleOne}$");

        // The second membership: the chronological ordering, not merged with the first series.
        await page.GotoAsync(app.Url($"/series/{saved.ChronicleChrono}").ToString());
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = $"Harbor Chronicles{suffix} (Chronological)", Level = 1 })).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".browse-book-title")).ToHaveTextAsync([$"Saved Noon{suffix}"]);
        await Assertions.Expect(page.Locator(".browse-position")).ToHaveTextAsync(["Position 2"]);

        // A same-named saved series is a different record with its own books.
        await page.GotoAsync(app.Url($"/series/{saved.ChronicleTwo}").ToString());
        await Assertions.Expect(page.Locator(".browse-book-title")).ToHaveTextAsync([$"Other Nora Book{suffix}"]);
        AssertNoClientErrors(app);
    }

    // ================================================================ journey 3: search candidate -> preview, slow provider pending

    [TestMethod]
    [DataRow(1440)]
    [DataRow(390)]
    public async Task SearchCandidateToAuthorAndSeriesPreviewToAnotherBookAndBackWhileASlowProviderIsStillPending(int width)
    {
        var spy = new Spy();
        await using var app = await StartAsync(spy, secondGate: new SemaphoreSlim(0, 1), width: width);
        var page = app.Page;
        var before = await SnapshotAsync();

        await SearchAsync(page, "Mara Vell");
        await Assertions.Expect(page.Locator("a.search-result-title").First).ToBeVisibleAsync();
        // The second source is deliberately still pending: the button still says the search is running.
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Search again", Exact = true })).ToBeVisibleAsync();

        await page.Locator(".search-result-authors a").First.ClickAsync();
        await ExpectUrlAsync(page, $"/browse/authors/{Guid36}/{Guid36}$");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Mara Vell", Level = 1 })).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".browse-source-context")).ToContainTextAsync("Preview from Demo Source");
        await Assertions.Expect(page.Locator(".browse-notes")).ToContainTextAsync("Matched by exact name only");
        await Assertions.Expect(page.Locator(".browse-notes")).ToContainTextAsync("not everything it holds");
        await Assertions.Expect(page.Locator("section.browse-group h2")).ToHaveTextAsync(
            ["Tidewatch Saga", "Tidewatch Saga (Chronological)", "No known series"]);
        await Assertions.Expect(page.Locator("section.browse-group").Nth(0).Locator(".browse-book-title")).ToHaveTextAsync(
            ["The Lantern Keeper", "Salt and Signal", "Beacon Tied A", "Beacon Tied B", "Unnumbered Tale"]);
        await Assertions.Expect(page.Locator("section.browse-group").Nth(0).Locator(".browse-position")).ToHaveTextAsync(
            ["Position 1", "Position 2.5", "Position 3", "Position 3", "Position unknown"]);
        await Assertions.Expect(page.Locator("section.browse-group").Nth(1).Locator(".browse-book-title")).ToHaveTextAsync(
            ["Dawn of Tides", "The Lantern Keeper"]);
        await Assertions.Expect(page.Locator(".browse-no-series .browse-book-title")).ToHaveTextAsync(["Solo Voyage"]);
        // Known facts only: the book with an ebook edition shows it, the comic-format record shows its version.
        await Assertions.Expect(page.Locator(".browse-book-row").First.GetByText("Ebook", new() { Exact = true })).ToBeVisibleAsync();
        // The shared media-type chip carries its tooltip, as docs/07-ui-conventions.md requires.
        await page.Locator(".browse-book-row").First.Locator(".mud-chip", new() { HasText = "Ebook" }).HoverAsync();
        await Assertions.Expect(page.Locator(".mud-tooltip.mud-popover-open")).ToHaveTextAsync("Ebook");
        await Assertions.Expect(page.Locator(".browse-no-series")).ToContainTextAsync("Graphic adaptation");
        if (width == 390) await AssertNoHorizontalOverflowAsync(page);

        // Leaving Search cancelled its still-running second provider, without killing the issued preview.
        await EventuallyAsync(() => spy.CancelledSearches >= 1, "the pending second-source search to be cancelled");
        await page.ReloadAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Mara Vell", Level = 1 })).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator("section.browse-group").First.Locator(".browse-book-title")).ToHaveCountAsync(5);

        // The series preview, in its own recorded order, with the ordering-label series kept separate.
        await Link(page, "Tidewatch Saga").First.ClickAsync();
        await ExpectUrlAsync(page, $"/browse/series/{Guid36}/{Guid36}$");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Tidewatch Saga", Level = 1 })).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".browse-book-title")).ToHaveTextAsync(
            ["The Lantern Keeper", "Salt and Signal", "Beacon Tied A", "Beacon Tied B", "Unnumbered Tale"]);
        await Assertions.Expect(page.Locator(".browse-tie")).ToHaveCountAsync(2);

        // Another already-observed book opens the existing details page and returns to the series preview.
        await Link(page, "Salt and Signal").ClickAsync();
        await ExpectUrlAsync(page, $"/search/demo/h-2\\?back=oseries&bid={Guid36}&bsub={Guid36}$");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Salt and Signal" })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText("Known editions")).ToBeVisibleAsync();
        await Link(page, "Back to series").ClickAsync();
        await ExpectUrlAsync(page, $"/browse/series/{Guid36}/{Guid36}$");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Tidewatch Saga", Level = 1 })).ToBeVisibleAsync();

        // Nothing was written, imported or requested by any of that.
        AssertUnchanged(before, await SnapshotAsync());
        AssertNoClientErrors(app);
    }

    [TestMethod]
    public async Task ReturningToSearchFromAPreviewKeepsTheSearchTextAndResults()
    {
        await using var app = await StartAsync(secondGate: new SemaphoreSlim(1, 1));
        var page = app.Page;

        await SearchAsync(page, "Mara Vell");
        await Assertions.Expect(page.Locator("a.search-result-title").First).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Search", Exact = true })).ToBeVisibleAsync();
        await page.Locator(".search-result-authors a").First.ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Mara Vell", Level = 1 })).ToBeVisibleAsync();

        // The browser Back button returns to the same search without losing it...
        await page.GoBackAsync();
        await ExpectUrlAsync(page, "/search$");
        await Assertions.Expect(page.GetByLabel("Title, author, or ISBN")).ToHaveValueAsync("Mara Vell");
        await Assertions.Expect(page.Locator("a.search-result-title").First).ToBeVisibleAsync();

        // ...and so does the explicit Back to search link on the preview.
        await page.Locator(".search-result-authors a").First.ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Mara Vell", Level = 1 })).ToBeVisibleAsync();
        await Link(page, "Back to search").ClickAsync();

        await ExpectUrlAsync(page, "/search$");
        await Assertions.Expect(page.GetByLabel("Title, author, or ISBN")).ToHaveValueAsync("Mara Vell");
        await Assertions.Expect(page.Locator("a.search-result-title").First).ToBeVisibleAsync();
        AssertNoClientErrors(app);
    }

    // ================================================================ journey 4: direct candidate detail, sparse metadata

    [TestMethod]
    public async Task DirectCandidateDetailOffersItsAnchorContextAndLocalNameChoicesWithoutImportingAnything()
    {
        await using var app = await StartAsync();
        var suffix = $" {Guid.NewGuid():N}"[..9];
        var page = app.Page;
        // One saved namesake of the observed credit: shown as a choice, never chosen or merged automatically.
        var namesake = await WithDatabaseAsync(app.Factory, database =>
        {
            var author = new Author("Mara Vell", null, DateTimeOffset.UtcNow);
            database.Authors.Add(author);
            var work = NewWork(database, $"Saved Mara Book{suffix}", author);
            return (Author: author.Id, Work: work.Id);
        });
        var before = await SnapshotAsync();

        await page.GotoAsync(app.Url("/search/demo/h-1").ToString());
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "The Lantern Keeper" })).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".book-author-link")).ToHaveTextAsync(["Mara Vell"]);
        await Assertions.Expect(page.Locator(".book-series-link")).ToHaveTextAsync(["Tidewatch Saga", "Tidewatch Saga (Chronological)"]);

        await page.Locator(".book-author-link").First.ClickAsync();
        await ExpectUrlAsync(page, $"/browse/authors/{Guid36}/{Guid36}$");
        // Only the anchor was observed: a useful, qualified small list.
        // A book with two recorded memberships is listed once under each, so the single anchor appears in both groups.
        await Assertions.Expect(page.Locator("section.browse-group h2")).ToHaveTextAsync(["Tidewatch Saga", "Tidewatch Saga (Chronological)"]);
        await Assertions.Expect(page.Locator(".browse-book-title")).ToHaveTextAsync(["The Lantern Keeper", "The Lantern Keeper"]);
        await Assertions.Expect(page.Locator(".browse-notes")).ToContainTextAsync("only the books seen");
        // The local namesake is a separate, explicit choice with the identity warning; nothing redirected.
        await Assertions.Expect(page.Locator(".browse-name-choices")).ToContainTextAsync("A matching name is not proof of a match");
        await Assertions.Expect(page.Locator(".browse-name-choice")).ToHaveCountAsync(1);
        await ExpectUrlAsync(page, "/browse/authors/");
        await page.Locator(".browse-name-choice a").ClickAsync();
        await ExpectUrlAsync(page, $"/authors/{namesake.Author}$");
        await Assertions.Expect(page.Locator(".browse-book-title")).ToHaveTextAsync([$"Saved Mara Book{suffix}"]);

        AssertUnchanged(before, await SnapshotAsync());
        AssertNoClientErrors(app);
    }

    [TestMethod]
    public async Task WhenTheHostSaysNavigationIsUnavailableThePagesExplainItAndOfferNoFakeLinks()
    {
        await using var app = await StartAsync(secondGate: new SemaphoreSlim(1, 1));
        var page = app.Page;

        // The host's own statement for "the temporary cache could not admit this context", injected into the two responses
        // that carry candidate navigation; everything else is the real host.
        static string WithoutContext(string body, bool isRun)
        {
            var root = JsonNode.Parse(body)!;
            var unavailable = () => JsonNode.Parse("""{"contextId":null,"expiresAtUtc":null,"authors":[],"series":[],"limitations":["navigation-unavailable"]}""");
            if (isRun)
            {
                foreach (var result in root["search"]!["results"]!.AsArray()) result!["navigation"] = unavailable();
            }
            else
            {
                root["navigation"] = unavailable();
            }

            return root.ToJsonString();
        }

        await page.RouteAsync("**/api/v1/catalog/search/runs/*", async route =>
        {
            if (route.Request.Method != "GET") { await route.ContinueAsync(); return; }
            var response = await route.FetchAsync();
            await route.FulfillAsync(new() { Response = response, Body = WithoutContext(await response.TextAsync(), isRun: true) });
        });
        await page.RouteAsync("**/api/v1/catalog/candidates/demo/h-1", async route =>
        {
            var response = await route.FetchAsync();
            await route.FulfillAsync(new() { Response = response, Body = WithoutContext(await response.TextAsync(), isRun: false) });
        });

        await SearchAsync(page, "Mara Vell");
        await Assertions.Expect(page.Locator("a.search-result-title").First).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".search-result-links-unavailable").First).ToContainTextAsync("not available for this result right now");
        await Assertions.Expect(page.Locator(".search-result-authors a, .search-result-series a")).ToHaveCountAsync(0);

        await page.GotoAsync(app.Url("/search/demo/h-1").ToString());
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "The Lantern Keeper" })).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".book-links-unavailable")).ToContainTextAsync("not available for this record right now");
        await Assertions.Expect(page.Locator(".book-author-link, .book-series-link")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByText("Mara Vell")).ToBeVisibleAsync();
        AssertNoClientErrors(app);
    }
    [TestMethod]
    public async Task CandidateWithNoAuthorOrSeriesHasNoLinksAndSaysSo()
    {
        await using var app = await StartAsync();
        await app.Page.GotoAsync(app.Url("/search/demo/bare-1").ToString());
        await Assertions.Expect(app.Page.GetByRole(AriaRole.Heading, new() { Name = "Untitled Sketch" })).ToBeVisibleAsync();
        await Assertions.Expect(app.Page.GetByText("Author not reported — check the edition before requesting.")).ToBeVisibleAsync();
        await Assertions.Expect(app.Page.Locator(".book-author-link, .book-series-link")).ToHaveCountAsync(0);
        AssertNoClientErrors(app);
    }

    [TestMethod]
    public async Task EmptySavedAuthorAndSeriesShowQualifiedEmptyStatesAndALaterPageSaysThereIsNothingMore()
    {
        await using var app = await StartAsync(withProviders: false);
        var suffix = $" {Guid.NewGuid():N}"[..9];
        var ids = await WithDatabaseAsync(app.Factory, database =>
        {
            var author = new Author($"Empty Author{suffix}", null, DateTimeOffset.UtcNow);
            var series = new Series($"Empty Series{suffix}", SeriesStatus.Unknown, DateTimeOffset.UtcNow);
            database.Authors.Add(author);
            database.Series.Add(series);
            return (Author: author.Id, Series: series.Id);
        });
        var page = app.Page;

        await page.GotoAsync(app.Url($"/authors/{ids.Author}").ToString());
        await Assertions.Expect(page.Locator(".browse-empty")).ToContainTextAsync("No books are known for this author yet.");
        await page.GotoAsync(app.Url($"/series/{ids.Series}").ToString());
        await Assertions.Expect(page.Locator(".browse-empty")).ToContainTextAsync("No books are known for this series yet.");
        await page.GotoAsync(app.Url($"/series/{ids.Series}?page=2").ToString());
        await Assertions.Expect(page.Locator(".browse-empty")).ToContainTextAsync("no more known entries");
        await page.GotoAsync(app.Url($"/authors/{Guid.NewGuid()}").ToString());
        await Assertions.Expect(page.Locator(".browse-not-found")).ToContainTextAsync("could not be found");
        await Assertions.Expect(Link(page, "Search for a book")).ToBeVisibleAsync();
        AssertNoClientErrors(app);
    }

    // ================================================================ journey 5: namesakes and alternative ordering

    [TestMethod]
    [DataRow(1440)]
    [DataRow(390)]
    public async Task SameNameSavedChoicesStayDistinctWarningsStayVisibleAndNothingIsAutoAssigned(int width)
    {
        var spy = new Spy();
        await using var app = await StartAsync(spy, width: width);
        var page = app.Page;
        // Saved records named exactly like the observed series, so the choices have something to show.
        var seeded = await SeedObservedNamesakesAsync(app.Factory);
        var before = await SnapshotAsync();

        await page.GotoAsync(app.Url("/search/demo/h-1").ToString());
        await Link(page, "Tidewatch Saga").First.ClickAsync();
        await ExpectUrlAsync(page, $"/browse/series/{Guid36}/{Guid36}$");
        var previewUrl = page.Url;
        await Assertions.Expect(page.Locator(".browse-name-choices")).ToContainTextAsync("Matching names in your catalog");
        await Assertions.Expect(page.Locator(".browse-name-choices")).ToContainTextAsync("may be different series");
        await Assertions.Expect(page.Locator(".browse-name-choice")).ToHaveCountAsync(2);
        await Assertions.Expect(page.Locator(".browse-name-choice a")).ToHaveTextAsync(["Tidewatch Saga", "Tidewatch Saga"]);
        var hrefs = await page.Locator(".browse-name-choice a").EvaluateAllAsync<string[]>("els => els.map(e => e.getAttribute('href'))");
        CollectionAssert.AreEquivalent(new[] { $"series/{seeded.One}", $"series/{seeded.Two}" }, hrefs);
        // Distinguishing information is visible, and the identity warning is in the page body.
        await Assertions.Expect(page.Locator(".browse-name-choice").First).ToContainTextAsync("saved record …");
        await Assertions.Expect(page.Locator(".browse-notes")).ToContainTextAsync("books here may belong to a different person or series");
        Assert.AreEqual(previewUrl, page.Url, "No automatic redirect to a saved record.");
        if (width == 390) await AssertNoHorizontalOverflowAsync(page);

        // The chronological ordering label is a different subject: its own preview and only its own saved choice.
        await page.GotoAsync(app.Url("/search/demo/h-1").ToString());
        await Link(page, "Tidewatch Saga (Chronological)").ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Tidewatch Saga (Chronological)", Level = 1 })).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".browse-name-choice")).ToHaveCountAsync(1);
        await Assertions.Expect(page.Locator(".browse-name-choice a")).ToHaveTextAsync(["Tidewatch Saga (Chronological)"]);

        AssertUnchanged(before, await SnapshotAsync());
        AssertNoClientErrors(app);
    }

    private sealed record NamesakeSeries(Guid One, Guid Two, Guid Chrono);

    // Idempotent: the DataRow runs of one test share a database, so a second call must not add more namesakes.
    private static Task<NamesakeSeries> SeedObservedNamesakesAsync(WebApplicationFactory<Program> factory) =>
        WithDatabaseAsync(factory, database =>
        {
            var existing = database.Series.Where(series => series.Name == "Tidewatch Saga" || series.Name == "Tidewatch Saga (Chronological)")
                .OrderBy(series => series.Name).ThenBy(series => series.Id).ToList();
            if (existing.Count == 3)
                return new NamesakeSeries(existing[0].Id, existing[1].Id, existing[2].Id);

            var one = new Series("Tidewatch Saga", SeriesStatus.Active, DateTimeOffset.UtcNow);
            var two = new Series("Tidewatch Saga", SeriesStatus.Unknown, DateTimeOffset.UtcNow);
            var chrono = new Series("Tidewatch Saga (Chronological)", SeriesStatus.Unknown, DateTimeOffset.UtcNow);
            database.Series.AddRange(one, two, chrono);
            var work = NewWork(database, "Saved Tidewatch Book", null);
            Join(work, one, "1", 1m);
            var other = NewWork(database, "Other Tidewatch Book", null);
            Join(other, two, "1", 1m);
            return new NamesakeSeries(one.Id, two.Id, chrono.Id);
        });

    // ================================================================ journey 6: expiry, owner, changed labels, bad return context

    [TestMethod]
    public async Task AnExpiredPreviewShowsRecoveryAndAnUnexpiredOneRefreshesAndASecondUserGetsTheSameRecovery()
    {
        var clock = new PinnedClock(DateTimeOffset.UtcNow);
        await using var app = await StartAsync(clock: clock, secondGate: new SemaphoreSlim(1, 1));
        var page = app.Page;

        await SearchAsync(page, "Mara Vell");
        await Assertions.Expect(page.Locator("a.search-result-title").First).ToBeVisibleAsync();
        await page.Locator(".search-result-authors a").First.ClickAsync();
        await ExpectUrlAsync(page, $"/browse/authors/{Guid36}/{Guid36}$");
        var previewUrl = page.Url;
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Mara Vell", Level = 1 })).ToBeVisibleAsync();

        // Unexpired: refresh still works for its owner.
        clock.Advance(TimeSpan.FromMinutes(9));
        await page.ReloadAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Mara Vell", Level = 1 })).ToBeVisibleAsync();

        // Another signed-in user cannot read it: same recovery as an unknown context.
        await using var other = await app.Browser.NewContextAsync();
        var otherPage = await other.NewPageAsync();
        await SignInAsync(otherPage, app.BaseUri, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword);
        await otherPage.GotoAsync(previewUrl);
        await Assertions.Expect(otherPage.Locator(".browse-expired")).ToContainTextAsync("expired or is no longer available");
        await Assertions.Expect(otherPage.GetByRole(AriaRole.Heading, new() { Name = "Mara Vell" })).ToHaveCountAsync(0);

        // Expired: recovery with a Search path, no automatic lookup, nothing imported.
        var callsBefore = (app.Spy.Searches, app.Spy.Details);
        clock.Advance(TimeSpan.FromMinutes(2));
        await page.ReloadAsync();
        await Assertions.Expect(page.Locator(".browse-expired")).ToContainTextAsync("expired or is no longer available");
        await Assertions.Expect(page.Locator(".browse-expired")).ToContainTextAsync("Nothing was saved, requested or imported.");
        Assert.AreEqual(callsBefore, (app.Spy.Searches, app.Spy.Details), "Expiry triggers no automatic provider lookup.");
        await Link(page, "Search again").ClickAsync();
        await ExpectUrlAsync(page, "/search$");

        // Wrong context/subject combinations behave the same.
        await page.GotoAsync(app.Url($"/browse/series/{Guid.NewGuid()}/{Guid.NewGuid()}").ToString());
        await Assertions.Expect(page.Locator(".browse-expired")).ToBeVisibleAsync();
        AssertNoClientErrors(app);
    }

    [TestMethod]
    public async Task ChangedSourceLabelsNeverReuseAStaleSubjectFromTheSearchCard()
    {
        var spy = new Spy();
        await using var app = await StartAsync(spy, secondGate: new SemaphoreSlim(1, 1));
        var page = app.Page;
        // The source now describes the same record with a revised series label.
        spy.DetailOverrides["h-1"] = DemoBooks()[0] with
        {
            Series = [InSeries("Tidewatch Saga (Revised)", "1")]
        };

        await SearchAsync(page, "Mara Vell");
        await Assertions.Expect(page.Locator("a.search-result-title").First).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".search-result-series-link").First).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "The Lantern Keeper", Exact = true }).First.ClickAsync();
        await ExpectUrlAsync(page, "/search/demo/h-1$");

        await Assertions.Expect(page.Locator(".book-series-link")).ToHaveTextAsync(["Tidewatch Saga (Revised)"]);
        await page.Locator(".book-series-link").First.ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Tidewatch Saga (Revised)", Level = 1 })).ToBeVisibleAsync();
        // Only the fresh observation of this one record: the old label's other books are not attributed to the new label.
        await Assertions.Expect(page.Locator(".browse-book-title")).ToHaveTextAsync(["The Lantern Keeper"]);
        AssertNoClientErrors(app);
    }

    [TestMethod]
    public async Task InvalidReturnContextCannotBreakTheBookPageOrBecomeAnExternalRedirect()
    {
        await using var app = await StartAsync();
        var page = app.Page;

        foreach (var query in new[]
        {
            "?back=author&bid=not-a-guid",
            "?back=https%3A%2F%2Fevil.example%2F&bid=" + Guid.NewGuid(),
            "?back=author",
            "?back=oauthor&bid=" + Guid.NewGuid(),
            "?back=series&bid=" + Guid.NewGuid() + "&bp=999"
        })
        {
            await page.GotoAsync(app.Url("/search/demo/h-1" + query).ToString());
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "The Lantern Keeper" })).ToBeVisibleAsync();
            await Assertions.Expect(Link(page, "Back to search")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".browse-return-link")).ToHaveCountAsync(0);
            Assert.AreEqual(0, await page.Locator("a[href*='evil.example']").CountAsync());
        }

        // A well-formed typed context is a local browse route only; a missing author just shows recovery.
        var missing = Guid.NewGuid();
        await page.GotoAsync(app.Url($"/search/demo/h-1?back=author&bid={missing}").ToString());
        await Assertions.Expect(page.Locator(".browse-return-link")).ToHaveAttributeAsync("href", $"authors/{missing}");
        await page.Locator(".browse-return-link").ClickAsync();
        await Assertions.Expect(page.Locator(".browse-not-found")).ToBeVisibleAsync();
        Assert.IsTrue(new Uri(page.Url).Host == app.BaseUri.Host, "Stayed on this origin.");

        await page.GotoAsync(app.Url($"/works/{Guid.NewGuid()}?back=https%3A%2F%2Fevil.example%2F&bid=x").ToString());
        await Assertions.Expect(page.GetByText("This catalog Work could not be found.")).ToBeVisibleAsync();
        AssertNoClientErrors(app);
    }

    // ================================================================ journey 7: existing detail behavior preserved

    [TestMethod]
    public async Task GroupedEditionsSourcesVersionLabelsAndSearchRankingStillRenderAndLinksCoexistWithActions()
    {
        await using var app = await StartAsync(secondGate: new SemaphoreSlim(1, 1));
        var page = app.Page;

        await SearchAsync(page, "Mara Vell");
        await Assertions.Expect(page.Locator("a.search-result-title").First).ToBeVisibleAsync();
        // Both providers' records for one book are grouped; version labels and the other matches are intact.
        await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Compare 2 editions" })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText("Graphic adaptation", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".search-result-authors a").First).ToHaveTextAsync("Mara Vell");
        await Assertions.Expect(page.Locator(".search-result-series-link").First).ToBeVisibleAsync();

        await page.GetByRole(AriaRole.Link, new() { Name = "Compare 2 editions" }).ClickAsync();
        await ExpectUrlAsync(page, "/search/demo/h-1$");
        await Assertions.Expect(page.GetByText("2 known editions")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText("Second Source", new() { Exact = true })).ToBeVisibleAsync();
        // The explicit actions live beside, not inside, the navigation links.
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Follow this series" })).ToHaveCountAsync(2);
        await Assertions.Expect(page.GetByText("Ask for this book")).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".book-author-link")).ToHaveTextAsync(["Mara Vell"]);
        AssertNoClientErrors(app);
    }

    // ================================================================ journey 8: no writes, no provider calls, actions still work

    [TestMethod]
    public async Task NavigationMakesNoCatalogRequestFollowOrNotificationChangesAndNoProviderCallsWhileExplicitFollowStillWorks()
    {
        var spy = new Spy();
        await using var app = await StartAsync(spy, secondGate: new SemaphoreSlim(1, 1));
        var suffix = $" {Guid.NewGuid():N}"[..9];
        var saved = await SeedAsync(app.Factory, suffix);
        var page = app.Page;

        await SearchAsync(page, "Mara Vell");
        await Assertions.Expect(page.Locator("a.search-result-title").First).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Search", Exact = true })).ToBeVisibleAsync();
        var before = await SnapshotAsync();
        var calls = (spy.Searches, spy.Details);

        // Only browse pages from here: author preview, series preview, saved author, saved series, reloads.
        await page.Locator(".search-result-authors a").First.ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Mara Vell", Level = 1 })).ToBeVisibleAsync();
        await Link(page, "Tidewatch Saga").First.ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Tidewatch Saga", Level = 1 })).ToBeVisibleAsync();
        await page.ReloadAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Tidewatch Saga", Level = 1 })).ToBeVisibleAsync();
        await page.GotoAsync(app.Url($"/authors/{saved.NoraA}").ToString());
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = $"Nora Tamsin{suffix}", Level = 1 })).ToBeVisibleAsync();
        await page.GotoAsync(app.Url($"/series/{saved.ChronicleOne}?page=1").ToString());
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = $"Harbor Chronicles{suffix}", Level = 1 })).ToBeVisibleAsync();

        Assert.AreEqual(calls, (spy.Searches, spy.Details), "Browse pages make zero metadata-provider calls.");
        AssertUnchanged(before, await SnapshotAsync());

        // The deliberate follow action still works, and changes only follows.
        await page.GotoAsync(app.Url($"/works/{saved.Dawn}").ToString());
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = $"Saved Dawn{suffix}" })).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Follow this author" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Following", Exact = true }).First).ToBeVisibleAsync();
        var after = await SnapshotAsync();
        Assert.AreNotEqual(before["following.follows"], after["following.follows"], "The explicit follow wrote.");
        AssertUnchanged(before, after, "following.follows");
        AssertNoClientErrors(app);
    }

    // ================================================================ journey 9: states, paging, keyboard, mobile

    [TestMethod]
    [DataRow(1440)]
    [DataRow(390)]
    public async Task LongListsPageWithoutClaimingATotalAndPagesAreKeyboardOperable(int width)
    {
        await using var app = await StartAsync(withProviders: false, width: width);
        var suffix = $" {Guid.NewGuid():N}"[..9];
        var ids = await WithDatabaseAsync(app.Factory, database =>
        {
            var author = new Author($"Prolific Author{suffix}", null, DateTimeOffset.UtcNow);
            var series = new Series($"Long Series{suffix}", SeriesStatus.Unknown, DateTimeOffset.UtcNow);
            database.Authors.Add(author);
            database.Series.Add(series);
            for (var index = 1; index <= 52; index++)
            {
                var work = NewWork(database, $"Long Book {index.ToString("D2", CultureInfo.InvariantCulture)}{suffix}", author);
                Join(work, series, index.ToString(CultureInfo.InvariantCulture), index);
            }

            return (Author: author.Id, Series: series.Id);
        });
        var page = app.Page;

        await page.GotoAsync(app.Url($"/authors/{ids.Author}").ToString());
        await Assertions.Expect(page.Locator(".browse-book-title")).ToHaveCountAsync(50);
        await Assertions.Expect(page.Locator(".browse-notes")).ToContainTextAsync("Series groups are built from the books on this page only");
        await Assertions.Expect(page.GetByText("More known books may follow on the next page.")).ToBeVisibleAsync();
        var next = Link(page, "Next page");
        await next.FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        await ExpectUrlAsync(page, $"/authors/{ids.Author}\\?page=2$");
        await Assertions.Expect(page.Locator(".browse-book-title")).ToHaveCountAsync(2);
        await Assertions.Expect(page.Locator(".browse-page-number")).ToHaveTextAsync("Page 2");
        await Assertions.Expect(Link(page, "Next page")).ToHaveCountAsync(0);
        await Link(page, "Previous page").ClickAsync();
        await ExpectUrlAsync(page, $"/authors/{ids.Author}$");
        await Assertions.Expect(page.Locator(".browse-book-title")).ToHaveCountAsync(50);

        await page.GotoAsync(app.Url($"/series/{ids.Series}?page=2").ToString());
        await Assertions.Expect(page.Locator(".browse-position")).ToHaveTextAsync(["Position 51", "Position 52"]);
        var bodyText = await page.Locator("main, .mud-main-content").First.InnerTextAsync();
        Assert.IsFalse(Regex.IsMatch(bodyText, @"of \d+|\d+ (books|entries|results)", RegexOptions.IgnoreCase), "no total is claimed");
        if (width == 390) await AssertNoHorizontalOverflowAsync(page);
        AssertNoClientErrors(app);
    }

    [TestMethod]
    public async Task LoadingErrorAndBadPageStatesAreDistinctAndRecoverable()
    {
        await using var app = await StartAsync(withProviders: false);
        var suffix = $" {Guid.NewGuid():N}"[..9];
        var saved = await SeedAsync(app.Factory, suffix);
        var page = app.Page;
        var release = new TaskCompletionSource();

        // Loading: hold the browse request open and the status text is visible.
        await page.RouteAsync("**/api/v1/catalog/browse/authors/**", async route =>
        {
            await release.Task;
            await route.ContinueAsync();
        });
        await page.GotoAsync(app.Url($"/authors/{saved.NoraA}").ToString(), new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Assertions.Expect(page.GetByText("Loading known books…")).ToBeVisibleAsync();
        release.SetResult();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = $"Nora Tamsin{suffix}", Level = 1 })).ToBeVisibleAsync();
        await page.UnrouteAsync("**/api/v1/catalog/browse/authors/**");

        // Unavailable: the host fails; the page says so, claims nothing, and Try again recovers.
        var failing = true;
        await page.RouteAsync("**/api/v1/catalog/browse/authors/**", async route =>
        {
            if (failing) await route.FulfillAsync(new() { Status = 503, Body = "boom: secret stack trace" });
            else await route.ContinueAsync();
        });
        await page.GotoAsync(app.Url($"/authors/{saved.NoraA}").ToString());
        await Assertions.Expect(page.Locator(".browse-unavailable")).ToContainTextAsync("could not be loaded right now");
        await Assertions.Expect(page.GetByText("secret stack trace")).ToHaveCountAsync(0);
        failing = false;
        await page.GetByRole(AriaRole.Button, new() { Name = "Try again" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = $"Nora Tamsin{suffix}", Level = 1 })).ToBeVisibleAsync();
        await page.UnrouteAsync("**/api/v1/catalog/browse/authors/**");

        // Bad page numbers never reach the server and offer the first page.
        await page.GotoAsync(app.Url($"/authors/{saved.NoraA}?page=0").ToString());
        await Assertions.Expect(page.Locator(".browse-bad-page")).ToContainTextAsync("not available");
        await Link(page, "Go to the first page").ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = $"Nora Tamsin{suffix}", Level = 1 })).ToBeVisibleAsync();
        AssertNoClientErrors(app);
    }

    [TestMethod]
    public async Task MalformedOrOutOfRangePageValuesOnEveryBrowseRouteShowTheBadPageStateWithNoBrowseRequestOrClientError()
    {
        var spy = new Spy();
        await using var app = await StartAsync(spy, secondGate: new SemaphoreSlim(0, 1));
        var suffix = $" {Guid.NewGuid():N}"[..9];
        var saved = await SeedAsync(app.Factory, suffix);
        var page = app.Page;
        var browseRequests = 0;
        page.Request += (_, request) =>
        {
            if (request.Url.Contains("/api/v1/catalog/browse/", StringComparison.Ordinal)) Interlocked.Increment(ref browseRequests);
        };

        // Reach one temporary author preview and one temporary series preview the way a user does.
        await SearchAsync(page, "Mara Vell");
        await Assertions.Expect(page.Locator("a.search-result-title").First).ToBeVisibleAsync();
        await page.Locator(".search-result-authors a").First.ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Mara Vell", Level = 1 })).ToBeVisibleAsync();
        var authorPreview = new Uri(page.Url).AbsolutePath;
        await Link(page, "Tidewatch Saga").First.ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Tidewatch Saga", Level = 1 })).ToBeVisibleAsync();
        var seriesPreview = new Uri(page.Url).AbsolutePath;

        var routes = new (string Path, string Heading)[]
        {
            ($"/authors/{saved.NoraA}", $"Nora Tamsin{suffix}"),
            ($"/series/{saved.ChronicleOne}", $"Harbor Chronicles{suffix}"),
            (authorPreview, "Mara Vell"),
            (seriesPreview, "Tidewatch Saga")
        };
        // Non-numeric, int overflow, below range, above the host's page-100 ceiling, signed and fractional text.
        string[] malformed = ["abc", "2147483648", "0", "101", "-1", "1.5"];

        foreach (var (path, heading) in routes)
        {
            foreach (var value in malformed)
            {
                Interlocked.Exchange(ref browseRequests, 0);
                await page.GotoAsync(app.Url($"{path}?page={value}").ToString());
                await Assertions.Expect(page.Locator(".browse-bad-page")).ToContainTextAsync("not available");
                await Assertions.Expect(page.Locator("#blazor-error-ui")).Not.ToBeVisibleAsync();
                var first = Link(page, "Go to the first page");
                await Assertions.Expect(first).ToHaveAttributeAsync("href", new Regex($"^{Regex.Escape(path.TrimStart('/'))}$"));
                Assert.AreEqual(0, Volatile.Read(ref browseRequests), $"{path}?page={value} must not call the browse API.");
            }

            // The recovery link is an in-app navigation from the bad state to a working first page.
            await Link(page, "Go to the first page").ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = heading, Level = 1 })).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".browse-bad-page")).ToHaveCountAsync(0);
            Assert.IsGreaterThan(0, Volatile.Read(ref browseRequests), "the recovered first page loads from the API");
        }

        // A deliberate rule: a supplied-but-empty page value is treated as no value, i.e. page 1.
        await page.GotoAsync(app.Url($"/authors/{saved.NoraA}?page=").ToString());
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = $"Nora Tamsin{suffix}", Level = 1 })).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".browse-bad-page")).ToHaveCountAsync(0);
        await Assertions.Expect(page.Locator(".browse-page-number")).ToHaveCountAsync(0);

        // In-app query changes (no full reload): valid -> malformed -> valid, on a live component.
        await page.GotoAsync(app.Url($"/series/{saved.ChronicleOne}").ToString());
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = $"Harbor Chronicles{suffix}", Level = 1 })).ToBeVisibleAsync();
        Interlocked.Exchange(ref browseRequests, 0);
        await ClickInAppLinkAsync(page, $"/series/{saved.ChronicleOne}?page=abc");
        await Assertions.Expect(page.Locator(".browse-bad-page")).ToContainTextAsync("not available");
        await Assertions.Expect(page.Locator("#blazor-error-ui")).Not.ToBeVisibleAsync();
        Assert.AreEqual(0, Volatile.Read(ref browseRequests), "an in-app malformed query must not call the browse API.");
        await ClickInAppLinkAsync(page, $"/series/{saved.ChronicleOne}?page=1");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = $"Harbor Chronicles{suffix}", Level = 1 })).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".browse-bad-page")).ToHaveCountAsync(0);

        AssertNoClientErrors(app);
    }

    // Blazor intercepts clicks on same-origin anchors, so this is a genuine in-app navigation, not a page load.
    private static async Task ClickInAppLinkAsync(IPage page, string href) =>
        await page.EvaluateAsync(
            """
            (href) => {
                const anchor = document.createElement('a');
                anchor.href = href;
                anchor.textContent = 'in-app test link';
                document.body.appendChild(anchor);
                anchor.click();
                anchor.remove();
            }
            """, href);

    [TestMethod]
    public async Task OpeningAPreviewBookWhileItsSourceIsDownShowsASavedReadOnlyPreviewWithNoRequestControls()
    {
        var spy = new Spy();
        await using var app = await StartAsync(spy, secondGate: new SemaphoreSlim(1, 1));
        var page = app.Page;

        // Reach a preview directly from a detail view (no search card is cached in this tab), then take the source down.
        await page.GotoAsync(app.Url("/search/demo/h-1").ToString());
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "The Lantern Keeper" })).ToBeVisibleAsync();
        await SearchAsyncViaNavigation(page);
        await Assertions.Expect(page.Locator("a.search-result-title").First).ToBeVisibleAsync();
        await page.Locator(".search-result-authors a").First.ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Mara Vell", Level = 1 })).ToBeVisibleAsync();
        var previewUrl = page.Url;

        spy.Offline = true;
        await page.GotoAsync(previewUrl); // a full reload drops the cached search card
        await Link(page, "Salt and Signal").ClickAsync();
        await Assertions.Expect(page.Locator(".book-preview-notice")).ToContainTextAsync("did not answer");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Salt and Signal" })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText("Ask for this book")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Follow this series" })).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByText("Known editions")).ToHaveCountAsync(0);
        await Assertions.Expect(page.Locator(".book-series-link")).ToHaveTextAsync(["Tidewatch Saga"]);
        await Link(page, "Back to author").ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Mara Vell", Level = 1 })).ToBeVisibleAsync();
        AssertNoClientErrors(app);
    }




    private static async Task SearchAsyncViaNavigation(IPage page)
    {
        await Link(page, "Back to search").ClickAsync();
        await SearchAsync(page, "Mara Vell");
    }
}
