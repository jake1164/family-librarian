// Playwright locator text lists and expected-value arrays are one-shot literals; CA1861 targets repeated calls.
#pragma warning disable CA1861
using System.Text.RegularExpressions;
using IClock = FamilyLibrarian.Application.Abstractions.IClock;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Catalog.Discovery;
using FamilyLibrarian.Domain.Catalog;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Playwright;
using Npgsql;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// Real-browser acceptance for explicit source lookups: the whole stack (migrated PostgreSQL, the real host and Blazor client
/// served from Kestrel on a loopback port, Chromium, desktop and narrow widths) with a deterministic scripted provider that
/// counts every discovery call. Nothing here talks to a real metadata service or uses household data. This is the Debug build
/// served by Kestrel, not the published image. Opt in with <c>FAMILY_LIBRARIAN_SOURCE_LOOKUP_BROWSER_TESTS=1</c>; the class is
/// also in the Release category.
/// </summary>
[TestClass]
[TestCategory("Release")]
public sealed class SourceLookupBrowserTests
{
    private const string OptInVariable = "FAMILY_LIBRARIAN_SOURCE_LOOKUP_BROWSER_TESTS";
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
            Assert.Inconclusive($"Set {OptInVariable}=1 for the isolated source-lookup browser acceptance tests.");
    }

    private sealed class PinnedClock(DateTimeOffset start) : IClock
    {
        private long ticks = start.UtcTicks;

        public DateTimeOffset UtcNow => new(Interlocked.Read(ref ticks), TimeSpan.Zero);

        public void Advance(TimeSpan by) => Interlocked.Add(ref ticks, by.Ticks);
    }

    private static BookCandidate Candidate() =>
        new("demo", "Demo Source", "b1", "The Spice Chronicle", ["Frank Herbert", "Unanchored Person"], "A story.", null, null, [],
            [new BookSeriesCandidate("Saga", "1", true) { SourceSeriesId = "S1" }])
        {
            AuthorCredits = [new BookAuthorCredit("Frank Herbert", null, "A1")],
            SourceKind = ExternalReferenceEntityType.Work
        };

    private sealed class Harness : IAsyncDisposable
    {
        public required WebApplicationFactory<Program> Factory { get; init; }

        public required FamilyLibrarianAppFactory Original { get; init; }

        public required Uri BaseUri { get; init; }

        public required IPlaywright Playwright { get; init; }

        public required IBrowser Browser { get; init; }

        public required IPage Page { get; init; }

        public required ScriptedDiscoveryProvider Provider { get; init; }

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

    private static async Task<Harness> StartAsync(int width, IClock? clock = null, Action<ScriptedDiscoveryProvider>? configure = null)
    {
        RequireOptIn();
        var provider = new ScriptedDiscoveryProvider(books: [Candidate()]);
        configure?.Invoke(provider);
        var original = new FamilyLibrarianAppFactory(WebTestFixture.Require(fixture).ConnectionString, services =>
        {
            services.RemoveAll<IBookMetadataProvider>();
            services.AddSingleton<IBookMetadataProvider>(provider);
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
        var harness = new Harness { Factory = factory, Original = original, BaseUri = baseUri, Playwright = playwright, Browser = browser, Page = page, Provider = provider };
        page.PageError += (_, message) => harness.ClientErrors.Add($"pageerror: {message}");
        page.Console += (_, message) =>
        {
            if (message.Type == "error" && !message.Text.Contains("Failed to load resource", StringComparison.Ordinal) &&
                !message.Text.Contains("report-only policy", StringComparison.Ordinal))
                harness.ClientErrors.Add($"console: {message.Text}");
        };
        await page.GotoAsync(new Uri(baseUri, "/search").ToString());
        await page.GetByLabel("Email", new() { Exact = true }).FillAsync(WebTestFixture.UserEmail);
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync(WebTestFixture.UserPassword);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByLabel("Title, author, or ISBN")).ToBeVisibleAsync();
        return harness;
    }

    /// <summary>Adds the page address and visible text to a failed step, so a locator timeout says what the reader was looking at.</summary>
    private static async Task StepAsync(IPage page, Func<Task> step)
    {
        try
        {
            await step();
        }
        catch (Exception exception) when (exception is PlaywrightException or TimeoutException)
        {
            var text = await page.Locator("body").InnerTextAsync();
            throw new InvalidOperationException($"{exception.Message}{Environment.NewLine}URL: {page.Url}{Environment.NewLine}PAGE TEXT: {text}", exception);
        }
    }

    private static ILocator Button(IPage page, string name) => page.GetByRole(AriaRole.Button, new() { Name = name, Exact = true });

    private static Task ExpectUrlAsync(IPage page, string pattern) => Assertions.Expect(page).ToHaveURLAsync(new Regex(pattern));

    private static async Task AssertNoHorizontalOverflowAsync(IPage page) =>
        Assert.IsFalse(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth > window.innerWidth"), "The page scrolls sideways.");

    private static void AssertNoClientErrors(Harness harness) =>
        Assert.IsEmpty(harness.ClientErrors, string.Join(Environment.NewLine, harness.ClientErrors));

    private static readonly string[] SnapshotTables =
    [
        "catalog.authors", "catalog.works", "catalog.work_authors", "catalog.editions", "catalog.series",
        "catalog.series_entries", "catalog.external_references", "requests.book_requests", "requests.request_participants",
        "requests.request_formats", "requests.request_status_history", "following.follows", "notifications.notification_events",
        "notifications.notification_receipts", "communications.outbound_communications",
        "communications.outbound_communication_deliveries", "audit.audit_events"
    ];

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

    private static void AssertUnchanged(Dictionary<string, string> before, Dictionary<string, string> after)
    {
        foreach (var table in SnapshotTables) Assert.AreEqual(before[table], after[table], $"{table} changed during a source lookup.");
    }

    // ================================================================ book -> series -> members, two pages

    [TestMethod]
    [DataRow(1440)]
    [DataRow(390)]
    public async Task ABookAsksItsSourceForSeriesThenThatSeriesForMembersAndPagesOnWithNothingSavedAndNoCallUntilPressed(int width)
    {
        await using var app = await StartAsync(width);
        var page = app.Page;
        var before = await SnapshotAsync();

        await page.GotoAsync(app.Url("/search/demo/b1").ToString());
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "The Spice Chronicle" })).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".book-source-lookup")).ToContainTextAsync("only when you press a button");
        Assert.AreEqual(0, app.Provider.DiscoveryCalls, "Opening the book makes no discovery call.");

        await Button(page, "Look up more about The Spice Chronicle from its source").ClickAsync();
        Assert.AreEqual(0, app.Provider.DiscoveryCalls, "Reading what is offered makes no discovery call either.");
        var ask = Button(page, "Find this book's series in Demo Source");
        await Assertions.Expect(ask).ToBeVisibleAsync();
        await ask.ClickAsync();

        await ExpectUrlAsync(page, $"/browse/lookup/{Guid36}$");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Series Demo Source reports for this book", Level = 1 })).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".lookup-outcome[data-outcome='Ok']")).ToContainTextAsync("not a promise that nothing more exists");
        await Assertions.Expect(page.Locator(".lookup-membership")).ToHaveCountAsync(2);
        await Assertions.Expect(page.Locator(".lookup-membership").Nth(0)).ToContainTextAsync("Saga");
        await Assertions.Expect(page.Locator(".lookup-membership").Nth(0)).ToContainTextAsync("position 1");
        await Assertions.Expect(page.Locator(".lookup-membership").Nth(1).Locator(".lookup-name-missing")).ToHaveTextAsync("Series name not provided by Demo Source");
        await Assertions.Expect(page.Locator(".lookup-membership").Nth(1)).ToContainTextAsync("position 7");
        await Assertions.Expect(page.Locator(".lookup-notes")).ToContainTextAsync("gave only an id for some series, not a name");
        await Assertions.Expect(page.Locator(".lookup-book .lookup-date")).ToHaveTextAsync("this source's work record");
        if (width == 390) await AssertNoHorizontalOverflowAsync(page);
        Assert.AreEqual(1, app.Provider.Calls(DiscoveryOperation.FindSeriesForBook));

        // An unnamed series is still a real source id, so it can be asked about; the page never invents a name for it.
        await Button(page, "Look up more about a series from its source").ClickAsync();
        await Button(page, "List the books Demo Source has in this series").ClickAsync();
        await ExpectUrlAsync(page, $"/browse/lookup/{Guid36}$");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Books Demo Source lists in this series", Level = 1 })).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".browse-source-context")).ToContainTextAsync("a series with no name from Demo Source");
        await Assertions.Expect(page.Locator(".lookup-book h2, .lookup-book p").First).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".lookup-outcome[data-outcome='Ok'][data-completion='MoreAvailable']")).ToContainTextAsync("This page is not the whole list");
        await Assertions.Expect(page.Locator(".lookup-book")).ToHaveCountAsync(2);
        await Assertions.Expect(page.Locator(".lookup-book .lookup-date").First).ToContainTextAsync("1965 (year only; original publication)");
        await Assertions.Expect(page.Locator(".lookup-book").Nth(0)).ToContainTextAsync("Member One");
        await Assertions.Expect(page.Locator(".lookup-book").Nth(1)).ToContainTextAsync("Member Two");
        if (width == 390) await AssertNoHorizontalOverflowAsync(page);

        await Button(page, "Load the next page from Demo Source").ClickAsync();
        await ExpectUrlAsync(page, $"/browse/lookup/{Guid36}$");
        await Assertions.Expect(page.Locator(".lookup-book")).ToHaveCountAsync(1);
        await Assertions.Expect(page.Locator(".lookup-book")).ToContainTextAsync("Member Three");
        await Assertions.Expect(page.Locator(".lookup-outcome[data-completion='ExhaustedInCheckedScope']")).ToContainTextAsync("everything it returned for this question");
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Load the next page from Demo Source" })).ToHaveCountAsync(0);
        if (width == 390) await AssertNoHorizontalOverflowAsync(page);

        // Reloading a result is a provider-free read of the same temporary window.
        var calls = app.Provider.DiscoveryCalls;
        await page.ReloadAsync();
        await Assertions.Expect(page.Locator(".lookup-book")).ToContainTextAsync("Member Three");
        Assert.AreEqual(calls, app.Provider.DiscoveryCalls);
        Assert.AreEqual(3, app.Provider.DiscoveryCalls, "One series lookup and two member pages, and nothing else.");

        AssertUnchanged(before, await SnapshotAsync());
        AssertNoClientErrors(app);
    }

    // ================================================================ author: a name needs choices before works

    [TestMethod]
    [DataRow(1440)]
    [DataRow(390)]
    public async Task ANameWithNoSourceIdGetsChoicesThatAreNeverChosenForThePersonAndAKnownIdListsWorksAndDerivedSeries(int width)
    {
        await using var app = await StartAsync(width);
        var page = app.Page;
        var before = await SnapshotAsync();

        await StepAsync(page, async () =>
        {
            await page.GotoAsync(app.Url("/search/demo/b1").ToString());
            await Assertions.Expect(page.Locator(".book-author-link")).ToHaveTextAsync(["Frank Herbert", "Unanchored Person"]);

            // A credit the source gave an id for lists works directly, and series worked out from those works are labelled as derived.
            await page.Locator(".book-author-link").First.ClickAsync();
            await ExpectUrlAsync(page, $"/browse/authors/{Guid36}/{Guid36}$");
            await Button(page, "List this author's works in Demo Source").ClickAsync();
            await ExpectUrlAsync(page, $"/browse/lookup/{Guid36}$");
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Works Demo Source lists for this author", Level = 1 })).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".lookup-book")).ToContainTextAsync("Work One");
            await Assertions.Expect(page.Locator(".lookup-book .lookup-date")).ToContainTextAsync("original publication");
            if (width == 390) await AssertNoHorizontalOverflowAsync(page);

            // Back returns to the author page with its explicit lookups intact; going back asks the source nothing.
            var callsBeforeBack = app.Provider.DiscoveryCalls;
            await page.GoBackAsync();
            await ExpectUrlAsync(page, $"/browse/authors/{Guid36}/{Guid36}$");
            await Assertions.Expect(Button(page, "Find series in this author's works at Demo Source")).ToBeVisibleAsync();
            Assert.AreEqual(callsBeforeBack, app.Provider.DiscoveryCalls);
            await Button(page, "Find series in this author's works at Demo Source").ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Series found in Demo Source's works for this author", Level = 1 })).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".lookup-derived")).ToContainTextAsync("Worked out from the works that were read, not stated by the source.");

            // A name with no source id may only be put to the source with the book that credited it, and returns choices.
            await page.GotoAsync(app.Url("/search/demo/b1").ToString());
            await page.Locator(".book-author-link").Nth(1).ClickAsync();
            await Button(page, "Ask Demo Source which person this is").ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "People Demo Source lists under this name", Level = 1 })).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".lookup-outcome[data-outcome='Ambiguous']")).ToContainTextAsync("Nothing was chosen for you");
            await Assertions.Expect(page.Locator(".lookup-ref")).ToHaveCountAsync(2);
            await Assertions.Expect(page.Locator(".lookup-ref-details").First).ToContainTextAsync("A larger count is not evidence this is the person you mean.");
            if (width == 390) await AssertNoHorizontalOverflowAsync(page);
            Assert.AreEqual(1, app.Provider.Calls(DiscoveryOperation.ResolveAuthor));

        });

        AssertUnchanged(before, await SnapshotAsync());
        AssertNoClientErrors(app);
    }

    // ================================================================ stated contributor roles

    [TestMethod]
    [DataRow(1440)]
    [DataRow(390)]
    public async Task ASolePainterCreditIsShownWithItsRoleAndNeverAsByAnAuthor(int width)
    {
        await using var app = await StartAsync(width, configure: provider =>
            provider.WorksScript = (author, budget) => DiscoveryResult.Found<AuthorWork>(
                [
                    new AuthorWork(ScriptedDiscoveryProvider.Book("w1", "Painted Only", new CreditRef(0, "Pat Painter", "illustrator", null)), []),
                    new AuthorWork(ScriptedDiscoveryProvider.Book(
                        "w2", "Mixed Credits", new CreditRef(0, "Frank Herbert", null, author.Id), new CreditRef(1, "Tess Translator", "translator", null)), []),
                    // A role too long to keep is dropped; the credit must then be neutral, never an unqualified author.
                    new AuthorWork(ScriptedDiscoveryProvider.Book(
                        "w3", "Long Role", new CreditRef(0, "Lou Longrole", "illustrator-" + new string('x', 64), null)), [])
                ],
                new DiscoveryScope(DiscoveryOperation.GetAuthorWorks, author.Id), DiscoveryCompletion.ExhaustedInCheckedScope,
                DateTimeOffset.UnixEpoch, budget.Usage));
        var page = app.Page;

        await StepAsync(page, async () =>
        {
            await page.GotoAsync(app.Url("/search/demo/b1").ToString());
            await page.Locator(".book-author-link").First.ClickAsync();
            await Button(page, "List this author's works in Demo Source").ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Works Demo Source lists for this author", Level = 1 })).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".lookup-book")).ToHaveCountAsync(3);
            await Assertions.Expect(page.Locator(".lookup-credits").Nth(2)).ToHaveTextAsync("Credited: Lou Longrole (role not retained)");
            Assert.AreEqual(0, await page.GetByText("By Lou Longrole").CountAsync(), "A dropped non-author role is never an unqualified By credit.");
            await Assertions.Expect(page.Locator(".lookup-credits").Nth(0)).ToHaveTextAsync("Credited: Pat Painter (illustrator)");
            await Assertions.Expect(page.Locator(".lookup-credits").Nth(1)).ToHaveTextAsync("By Frank Herbert · Also credited: Tess Translator (translator)");
            Assert.AreEqual(0, await page.GetByText("By Pat Painter").CountAsync(), "A stated illustrator is never an unqualified By credit.");
            if (width == 390) await AssertNoHorizontalOverflowAsync(page);
        });
        AssertNoClientErrors(app);
    }

    // ================================================================ honest states and recovery

    [TestMethod]
    [DataRow(1440)]
    [DataRow(390)]
    public async Task UnusableAndUnsupportedAndUnverifiedStatesAreStatedAndNeverDressedAsAnswers(int width)
    {
        await using var app = await StartAsync(width, configure: provider =>
        {
            provider.Capabilities = new DiscoveryCapabilities(
            [
                new(DiscoveryOperation.FindSeriesForBook, DiscoveryCapability.Unverified("live-probe-not-run-coverage-uneven")),
                new(DiscoveryOperation.GetSeriesMembers, DiscoveryCapability.Supported),
                new(DiscoveryOperation.GetAuthorWorks, DiscoveryCapability.Unsupported("no-author-id-query"))
            ]);
            provider.MembersScript = (_, budget) => DiscoveryResult.NoUsableItems<SeriesMember>(
                new DiscoveryScope(DiscoveryOperation.GetSeriesMembers, ScriptedDiscoveryProvider.SagaSeries),
                DiscoveryCompletion.ExhaustedInCheckedScope, null, DateTimeOffset.UnixEpoch, budget.Usage, [DiscoveryCodes.ItemMissingId]);
        });
        var page = app.Page;

        await page.GotoAsync(app.Url("/search/demo/b1").ToString());
        await Button(page, "Look up more about The Spice Chronicle from its source").ClickAsync();
        await Assertions.Expect(page.Locator(".lookup-caveat")).ToContainTextAsync("not yet verified against Demo Source");
        await Button(page, "Find this book's series in Demo Source").ClickAsync();
        await Assertions.Expect(page.Locator(".lookup-membership")).ToHaveCountAsync(2);
        await Button(page, "Look up more about Saga from its source").ClickAsync();
        await Button(page, "List the books Demo Source has in this series").ClickAsync();

        await Assertions.Expect(page.Locator(".lookup-outcome[data-outcome='NoUsableItems']")).ToContainTextAsync("answered, but nothing could be used");
        await Assertions.Expect(page.Locator(".lookup-outcome")).ToContainTextAsync("different from sending none");
        await Assertions.Expect(page.Locator(".lookup-book")).ToHaveCountAsync(0);
        if (width == 390) await AssertNoHorizontalOverflowAsync(page);

        // The author lookup the source cannot do has no button, only the reason.
        await page.GotoAsync(app.Url("/search/demo/b1").ToString());
        await page.Locator(".book-author-link").First.ClickAsync();
        await Assertions.Expect(page.Locator(".lookup-unavailable")).ToContainTextAsync("cannot list an author's works by an author id");
        await Assertions.Expect(Button(page, "List this author's works in Demo Source")).ToHaveCountAsync(0);
        AssertNoClientErrors(app);
    }

    [TestMethod]
    [DataRow(1440)]
    [DataRow(390)]
    public async Task AnExpiredPreviewExplainsItselfWhetherAPersonPressesALookupOrReopensAResult(int width)
    {
        // Pinned at the real current time (as the other browser tests do): a clock far from it disturbs the sign-in session itself.
        var clock = new PinnedClock(DateTimeOffset.UtcNow);
        await using var app = await StartAsync(width, clock);
        var page = app.Page;

        await StepAsync(page, async () =>
        {
            await page.GotoAsync(app.Url("/search/demo/b1").ToString());
            await Button(page, "Look up more about The Spice Chronicle from its source").ClickAsync();
            await Button(page, "Find this book's series in Demo Source").ClickAsync();
            await ExpectUrlAsync(page, $"/browse/lookup/{Guid36}$");
            var window = page.Url;
            await Assertions.Expect(page.Locator(".lookup-membership").First).ToBeVisibleAsync();
            var calls = app.Provider.DiscoveryCalls;

            clock.Advance(TimeSpan.FromMinutes(11));
            await Button(page, "Look up more about Saga from its source").ClickAsync();
            await Assertions.Expect(page.Locator(".lookup-unavailable, .lookup-none, .lookup-offers-problem, .lookup-offers").First).ToBeVisibleAsync();
            await page.GotoAsync(window);
            await Assertions.Expect(page.Locator(".browse-expired")).ToContainTextAsync("This lookup result has expired or is no longer available");
            await Assertions.Expect(page.Locator(".browse-expired")).ToContainTextAsync("Nothing was saved, requested or imported");
            Assert.AreEqual(calls, app.Provider.DiscoveryCalls, "Neither the expired lookup nor the reopened result reached the source.");
            if (width == 390) await AssertNoHorizontalOverflowAsync(page);
        });

        AssertNoClientErrors(app);
    }
}
