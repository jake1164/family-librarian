// Playwright locator text lists and expected-value arrays are one-shot literals; CA1861 targets repeated calls.
#pragma warning disable CA1861
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Catalog.Discovery;
using FamilyLibrarian.Application.Conversation;
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
/// Real-browser acceptance for the chat's shared answers: the whole stack (migrated PostgreSQL, the real host and Blazor client on
/// a loopback Kestrel port, Chromium at desktop and narrow widths) with a deterministic scripted source that counts every read.
/// It exercises a numbered position, a follow-up with no read, and the explicit edition-title expansion, and checks that nothing
/// durable changed. This is the Debug build served by Kestrel, not the published image. Opt in with
/// <c>FAMILY_LIBRARIAN_CHAT_ANSWER_BROWSER_TESTS=1</c>; the class is also in the Release category.
/// </summary>
[TestClass]
[TestCategory("Release")]
public sealed class ChatAnswerBrowserTests
{
    private const string OptInVariable = "FAMILY_LIBRARIAN_CHAT_ANSWER_BROWSER_TESTS";
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
            Assert.Inconclusive($"Set {OptInVariable}=1 for the isolated chat-answer browser acceptance tests.");
    }

    private static BookCandidate Seed() =>
        new("demo", "Demo Source", "seed1", "The Lantern Road", ["Mara Quill"], "A story.", null, null, [],
            [new BookSeriesCandidate("The Lantern Road", "1", true, 1m) { SourceSeriesId = "S1" }])
        {
            AuthorCredits = [new BookAuthorCredit("Mara Quill", null, "A1")],
            SourceKind = ExternalReferenceEntityType.Work
        };

    private static SeriesMember Member(string id, string title, decimal position, int year) =>
        new(
            new DiscoveredBook(
                new SourceRef("demo", id, ExternalReferenceEntityType.Work),
                new TitleForm(title),
                [new CreditRef(0, "Mara Quill", null, ScriptedDiscoveryProvider.Herbert)],
                PublicationEvidence.Dated(new DateOnly(year, 5, 1), DatePrecision.Day, DateBasis.Original, PublicationStatus.Published),
                new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero)),
            ScriptedDiscoveryProvider.SagaSeries,
            [new PositionClaim(position.ToString(System.Globalization.CultureInfo.InvariantCulture), position, null, ScriptedDiscoveryProvider.SagaSeries)]);

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

    private static async Task<Harness> StartAsync(int width)
    {
        RequireOptIn();
        var provider = new ScriptedDiscoveryProvider(books: [Seed()])
        {
            MembersScript = (cursor, budget) => DiscoveryResult.Found<SeriesMember>(
                [Member("m1", "Lanterns at Dawn", 1, 1999), Member("m2", "Lanterns at Dusk", 2, 2005)],
                new DiscoveryScope(DiscoveryOperation.GetSeriesMembers, ScriptedDiscoveryProvider.SagaSeries),
                DiscoveryCompletion.ExhaustedInCheckedScope,
                new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero),
                budget.Usage),
            Editions = _ =>
            [
                new WorkEditionTitle("OL1M", "Lanterns at Dawn", "en", "title"),
                new WorkEditionTitle("OL2M", "Lanterns at Dawn (Large Print)", null, "title")
            ]
        };
        var original = new FamilyLibrarianAppFactory(WebTestFixture.Require(fixture).ConnectionString, services =>
        {
            services.RemoveAll<IBookMetadataProvider>();
            services.AddSingleton<IBookMetadataProvider>(provider);
            services.RemoveAll<ConversationOptions>();
            services.AddSingleton(new ConversationOptions { Enabled = true, MatrixEnabled = true, WebEnabled = true });
        });
        var factory = original.WithWebHostBuilder(builder => builder.UseStaticWebAssets());
        // Test classes run in parallel in one assembly (and in the Release job), so this listens on a dynamic loopback port.
        factory.UseKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
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
        await page.GotoAsync(new Uri(baseUri, "/chat").ToString());
        await page.GetByLabel("Email", new() { Exact = true }).FillAsync(WebTestFixture.UserEmail);
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync(WebTestFixture.UserPassword);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByLabel("Message to the librarian")).ToBeVisibleAsync();
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

    private static async Task SayAsync(IPage page, string text)
    {
        await page.GetByLabel("Message to the librarian").FillAsync(text);
        await page.GetByRole(AriaRole.Button, new() { Name = "Send", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByLabel("Message to the librarian")).ToHaveValueAsync(string.Empty);
        await Assertions.Expect(page.Locator("text=Working…")).ToHaveCountAsync(0);
    }

    private static async Task AssertNoHorizontalOverflowAsync(IPage page) =>
        Assert.IsFalse(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth > window.innerWidth"), "The page scrolls sideways.");

    private static readonly string[] DurableTables =
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
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var table in DurableTables)
        {
            await using var command = new NpgsqlCommand(
                $"SELECT count(*)::text || ':' || coalesce(md5(string_agg(t::text, '|' ORDER BY t::text)), '') FROM {table} t", connection);
            result[table] = (string)(await command.ExecuteScalarAsync())!;
        }

        return result;
    }

    [TestMethod]
    [DataRow(1440)]
    [DataRow(390)]
    public async Task ANumberedBookAFollowUpAndAnEditionExpansionWorkInTheBrowserWithNothingSaved(int width)
    {
        await using var app = await StartAsync(width);
        var page = app.Page;
        var before = await SnapshotAsync();

        await StepAsync(page, async () =>
        {
            await SayAsync(page, "book 1 of The Lantern Road");

            // A name is never an identity: the saved-or-source series is offered and the reader picks it.
            await Assertions.Expect(page.Locator(".family-librarian-chat-answer, .family-librarian-chat-choices").First).ToBeVisibleAsync();
            var pick = page.GetByRole(AriaRole.Button, new() { Name = "Pick The Lantern Road" });
            await Assertions.Expect(pick).ToBeVisibleAsync();
            await pick.ClickAsync();

            // The answer is a card: the position, the facts it rests on and the qualification, not a bare sentence.
            await Assertions.Expect(page.Locator(".family-librarian-chat-answer").Last).ToContainTextAsync("Book 1 of The Lantern Road is");
            await Assertions.Expect(page.Locator(".family-librarian-chat-answer").Last).ToContainTextAsync("Lanterns at Dawn");
            await Assertions.Expect(page.Locator(".family-librarian-chat-answer").Last).ToContainTextAsync("book 1");
            if (width == 390) await AssertNoHorizontalOverflowAsync(page);

            var readsAfterAnswer = app.Provider.DiscoveryCalls;
            await SayAsync(page, "what comes next?");

            // The follow-up is answered from what the conversation retained: one more answer, no further provider read.
            await Assertions.Expect(page.Locator("body")).ToContainTextAsync("Lanterns at Dusk");
            Assert.AreEqual(readsAfterAnswer, app.Provider.DiscoveryCalls, "A retained follow-up makes no provider call.");
            if (width == 390) await AssertNoHorizontalOverflowAsync(page);

            await SayAsync(page, "editions 1");

            await Assertions.Expect(page.Locator(".family-librarian-chat-answer").Last).ToContainTextAsync("Lanterns at Dawn (Large Print)");
            await Assertions.Expect(page.Locator(".family-librarian-chat-answer").Last).ToContainTextAsync("language not stated");
            await Assertions.Expect(page.Locator(".family-librarian-chat-answer").Last).ToContainTextAsync("not alternative titles, translations or identity proof");
            if (width == 390) await AssertNoHorizontalOverflowAsync(page);
        });

        Assert.AreEqual(1, app.Provider.Calls(DiscoveryOperation.SearchBooks), "One seed search for the named series.");
        Assert.AreEqual(1, app.Provider.Calls(DiscoveryOperation.GetSeriesMembers), "One series page for the answer.");
        Assert.AreEqual(1, app.Provider.EditionCalls, "One edition read, never one per edition.");
        Assert.AreEqual(3, app.Provider.DiscoveryCalls, "No speculative fan-out, no retry and no per-series expansion.");
        AssertUnchanged(before, await SnapshotAsync());
        Assert.IsEmpty(app.ClientErrors, string.Join(Environment.NewLine, app.ClientErrors));
    }

    [TestMethod]
    [DataRow(1440)]
    [DataRow(390)]
    public async Task AButtonOnAnEarlierListDoesNothingOnceANewerListIsShownAndTheNewestListStillWorks(int width)
    {
        await using var app = await StartAsync(width);
        var page = app.Page;
        var before = await SnapshotAsync();

        await StepAsync(page, async () =>
        {
            await SayAsync(page, "book 1 of The Lantern Road");
            var picks = page.GetByRole(AriaRole.Button, new() { Name = "Pick The Lantern Road" });
            await Assertions.Expect(picks).ToHaveCountAsync(1);

            // A second list replaces the first. Its buttons are still on the page, above the newer ones.
            await SayAsync(page, "book 1 of The Lantern Road");
            await Assertions.Expect(picks).ToHaveCountAsync(2);
            var memberReadsBefore = app.Provider.Calls(DiscoveryOperation.GetSeriesMembers);
            var cardsBefore = await page.Locator(".family-librarian-chat-answer").CountAsync();

            await picks.First.ClickAsync();

            // The old button addresses nothing: the reader is told, and no answer was read or given.
            await Assertions.Expect(page.Locator("body")).ToContainTextAsync("belongs to an earlier list");
            await Assertions.Expect(page.Locator(".family-librarian-chat-answer")).ToHaveCountAsync(cardsBefore);
            Assert.AreEqual(memberReadsBefore, app.Provider.Calls(DiscoveryOperation.GetSeriesMembers), "A stale button makes no provider read.");
            if (width == 390) await AssertNoHorizontalOverflowAsync(page);

            // The button of the newest list is unaffected by the stale press.
            await picks.Last.ClickAsync();
            await Assertions.Expect(page.Locator(".family-librarian-chat-answer").Last).ToContainTextAsync("Book 1 of The Lantern Road is");
            if (width == 390) await AssertNoHorizontalOverflowAsync(page);
        });

        Assert.AreEqual(1, app.Provider.Calls(DiscoveryOperation.GetSeriesMembers), "Only the newest list's button read the series.");
        AssertUnchanged(before, await SnapshotAsync());
        Assert.IsEmpty(app.ClientErrors, string.Join(Environment.NewLine, app.ClientErrors));
    }

    private static void AssertUnchanged(Dictionary<string, string> before, Dictionary<string, string> after)
    {
        foreach (var table in DurableTables) Assert.AreEqual(before[table], after[table], $"{table} changed during a read-only answer.");
    }
}
