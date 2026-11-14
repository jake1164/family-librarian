using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Web.Client.Catalog;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using MudBlazor.Services;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// Server-rendered HTML checks for the shared browse components: what a reader actually sees for namesakes, ordering
/// labels, ties, unknowns, truncation and unroutable records, and that data is encoded rather than injected.
/// </summary>
[TestClass]
public sealed class CatalogBrowseRenderTests
{
    private static readonly Guid Context = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Subject = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid WorkA = Guid.Parse("11111111-0000-0000-0000-00000000000a");
    private static readonly Guid WorkB = Guid.Parse("11111111-0000-0000-0000-00000000000b");
    private static readonly Guid SeriesOne = Guid.Parse("22222222-0000-0000-0000-000000000001");
    private static readonly Guid SeriesTwo = Guid.Parse("22222222-0000-0000-0000-000000000002");
    private static readonly DateTimeOffset Seen = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    private static async Task<string> RenderAsync<T>(Dictionary<string, object?> parameters) where T : IComponent
    {
        await using var services = new ServiceCollection().AddLogging().AddMudServices().AddSingleton<IJSRuntime, NoScriptRuntime>().AddSingleton<NavigationManager, StaticNavigationManager>().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);
        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters))).ToHtmlString());
    }

    // MudChip registers a key interceptor that needs a JS runtime even for static rendering; nothing here runs script.
    private sealed class StaticNavigationManager : NavigationManager
    {
        public StaticNavigationManager() => Initialize("http://localhost/", "http://localhost/");
    }

    private sealed class NoScriptRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => default;
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => default;
    }

    private static CatalogBrowseBookResponse Saved(
        Guid id, string title, IReadOnlyList<string>? formats = null, string? kind = null, string? label = null,
        DateOnly? date = null, bool truncated = false, CatalogBrowseCreditResponse[]? credits = null,
        CatalogBrowseMembershipResponse[]? memberships = null, CatalogBrowseSourceResponse[]? sources = null) =>
        new(id, null, null, title, date,
            credits ?? [new(Guid.NewGuid(), null, "Ann Author", null)],
            memberships ?? [],
            sources ?? [new("demo", "Demo Catalog", Seen)],
            formats, kind, label, truncated);

    private static CatalogBrowseEntryResponse Entry(CatalogBrowseBookResponse book, string? label = null, decimal? position = null) =>
        new(book, label, position);

    // ---- Book list ---------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task SeriesRowsShowRecordedPositionsVerbatimMarkTiesAndKeepUnknownsUnknown()
    {
        var html = await RenderAsync<BrowseBookList>(new()
        {
            ["Entries"] = new[]
            {
                Entry(Saved(WorkA, "First"), "1", 1m),
                Entry(Saved(WorkB, "Half"), "2.5", 2.5m),
                Entry(Saved(Guid.NewGuid(), "Tied one"), "3", 3m),
                Entry(Saved(Guid.NewGuid(), "Tied two"), "3", 3m),
                Entry(Saved(Guid.NewGuid(), "Qualified"), "Book 4 (omnibus)", null),
                Entry(Saved(Guid.NewGuid(), "Nowhere"), null, null)
            }
        });

        StringAssert.Contains(html, "Position 1");
        StringAssert.Contains(html, "Position 2.5");
        StringAssert.Contains(html, "Position Book 4 (omnibus)");
        StringAssert.Contains(html, "Position unknown");
        Assert.AreEqual(2, CountOf(html, "Shares this position with another book on this page."), "only the two tied rows");
        Assert.IsLessThan(html.IndexOf("Tied one", StringComparison.Ordinal), html.IndexOf("Half", StringComparison.Ordinal), "order is exactly as supplied");
    }

    [TestMethod]
    public async Task UnknownFactsAreSaidToBeUnknownAndNeverInferredFromTheTitle()
    {
        var html = await RenderAsync<BrowseBookList>(new()
        {
            ["Entries"] = new[] { Entry(Saved(WorkA, "Complete Collection Box Set (Audiobook)", credits: [], sources: [])) }
        });

        StringAssert.Contains(html, "Author not reported");
        StringAssert.Contains(html, "Format not reported");
        StringAssert.Contains(html, "Version unknown");
        StringAssert.Contains(html, "Date unknown");
        StringAssert.Contains(html, "Source not recorded");
        Assert.DoesNotContain("Headphones", html, "no audiobook icon from a title");
        Assert.DoesNotContain("standalone", html, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task KnownFormatsVersionDateRoleAndSourceAreRenderedAndExtraContributorsAreLabelled()
    {
        var book = Saved(WorkA, "Known", ["Ebook", "Audiobook"], "Collection", "Collection or combined volume", new DateOnly(1984, 5, 1),
            credits: [new(Guid.NewGuid(), null, "Ann Author", null), new(Guid.NewGuid(), null, "Bo Builder", "Illustrator")]);
        var html = await RenderAsync<BrowseBookList>(new() { ["Entries"] = new[] { Entry(book) } });

        StringAssert.Contains(html, "Ebook");
        StringAssert.Contains(html, "Audiobook");
        StringAssert.Contains(html, "Collection or combined volume");
        StringAssert.Contains(html, "Published 1984");
        StringAssert.Contains(html, "Contributors:");
        StringAssert.Contains(html, "(Illustrator)");
        StringAssert.Contains(html, "Source: Demo Catalog (seen 14 Nov 2026)");
        Assert.DoesNotContain("Version unknown", html);
    }

    [TestMethod]
    public async Task UnspecifiedVersionIsNotPresentedAsANormalNovel()
    {
        var html = await RenderAsync<BrowseBookList>(new()
        {
            ["Entries"] = new[] { Entry(Saved(WorkA, "Plain", kind: "Unspecified", label: "Version unknown")) }
        });
        StringAssert.Contains(html, "Version unknown");
        Assert.DoesNotContain("Novel", html);
    }

    [TestMethod]
    public async Task SavedRowsLinkToTheExistingWorkRouteWithTypedReturnContextAndSavedCreditsLinkToAuthors()
    {
        var authorId = Guid.NewGuid();
        var book = Saved(WorkA, "Linked", credits: [new(authorId, null, "Ann Author", null)],
            memberships: [new(SeriesOne, null, "Saga", "2", 2m, true)]);
        var html = await RenderAsync<BrowseBookList>(new()
        {
            ["Entries"] = new[] { Entry(book, "2", 2m) },
            ["Return"] = new BrowseReturn(BrowseReturnKind.Author, authorId, null, 2)
        });

        StringAssert.Contains(html, $"href=\"works/{WorkA}?back=author&amp;bid={authorId}&amp;bp=2\"");
        StringAssert.Contains(html, $"href=\"authors/{authorId}\"");
        StringAssert.Contains(html, $"href=\"series/{SeriesOne}\"");
        StringAssert.Contains(html, "position 2");
    }

    [TestMethod]
    public async Task ObservedRowsLinkToTheProviderBookRouteAndTheirOwnTemporarySubjects()
    {
        var book = new CatalogBrowseBookResponse(null, "demo", "ext/1?x", "Observed Book", null,
            [new(null, Subject, "Ann Author", null)],
            [new(null, Guid.NewGuid(), "Saga (Chronological)", "7", 7m, true)],
            [new("demo", "Demo Catalog", Seen)]);
        var html = await RenderAsync<BrowseBookList>(new()
        {
            ["Entries"] = new[] { Entry(book, "7", 7m) },
            ["Return"] = new BrowseReturn(BrowseReturnKind.ObservedSeries, Context, Subject),
            ["ContextId"] = (Guid?)Context
        });

        StringAssert.Contains(html, "href=\"search/demo/ext%2F1%3Fx?back=oseries&amp;bid=" + Context + "&amp;bsub=" + Subject + "\"");
        StringAssert.Contains(html, $"href=\"browse/authors/{Context}/{Subject}\"");
        StringAssert.Contains(html, "browse/series/" + Context + "/");
        StringAssert.Contains(html, "Saga (Chronological)", "ordering qualifier survives");
    }

    [TestMethod]
    public async Task ARecordWhoseReferenceCannotBeRoutedShowsRecoveryInsteadOfAGuessedLink()
    {
        var book = new CatalogBrowseBookResponse(null, "demo", "..", "Odd id", null, [], [], []);
        var html = await RenderAsync<BrowseBookList>(new() { ["Entries"] = new[] { Entry(book) } });

        StringAssert.Contains(html, "This record cannot be opened from here.");
        Assert.DoesNotContain("href=\"search/demo", html);
        StringAssert.Contains(html, "Odd id");
    }

    [TestMethod]
    public async Task TruncatedAssociationsAreDisclosedPerRowAndMarkupIsEncoded()
    {
        var html = await RenderAsync<BrowseBookList>(new()
        {
            ["Entries"] = new[] { Entry(Saved(WorkA, "<script>alert(1)</script> & \"quotes\"", truncated: true)) }
        });

        StringAssert.Contains(html, "may be incomplete");
        StringAssert.Contains(html, "&lt;script&gt;alert(1)&lt;/script&gt;");
        Assert.DoesNotContain("<script>", html);
    }

    // ---- Groups ------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task SameNamedSeriesAndDistinctOrderingLabelsStaySeparateGroupsWithOwnLinks()
    {
        var groups = new CatalogBrowseGroupResponse[]
        {
            new(SeriesOne, null, "Ryanverse", [Entry(Saved(WorkA, "One"), "1", 1m)]),
            new(SeriesTwo, null, "Ryanverse", [Entry(Saved(WorkB, "Other One"), "1", 1m)]),
            new(null, Subject, "Ryanverse (Chronological)", [Entry(Saved(Guid.NewGuid(), "Prequel"), "1", 1m)])
        };
        var html = await RenderAsync<BrowseGroups>(new() { ["Groups"] = groups, ["NoKnownSeries"] = Array.Empty<CatalogBrowseEntryResponse>(), ["ContextId"] = (Guid?)Context });

        Assert.AreEqual(3, CountOf(html, "<section"), "three groups, nothing merged");
        StringAssert.Contains(html, $"href=\"series/{SeriesOne}\"");
        StringAssert.Contains(html, $"href=\"series/{SeriesTwo}\"");
        StringAssert.Contains(html, $"href=\"browse/series/{Context}/{Subject}\"");
        StringAssert.Contains(html, "Ryanverse (Chronological)");
        Assert.DoesNotContain("No known series", html);
    }

    [TestMethod]
    public async Task BooksWithoutAKnownSeriesAreSaidToHaveNoKnownSeriesNotToBeStandalone()
    {
        var html = await RenderAsync<BrowseGroups>(new()
        {
            ["Groups"] = Array.Empty<CatalogBrowseGroupResponse>(),
            ["NoKnownSeries"] = new[] { Entry(Saved(WorkA, "Lone")) }
        });

        StringAssert.Contains(html, "No known series");
        StringAssert.Contains(html, "does not mean they stand alone");
        Assert.DoesNotContain("Position", html, "no position column outside a series");
    }

    [TestMethod]
    public async Task AnEmptyPageShowsTheQualifiedEmptyText()
    {
        var html = await RenderAsync<BrowseGroups>(new()
        {
            ["Groups"] = Array.Empty<CatalogBrowseGroupResponse>(),
            ["NoKnownSeries"] = Array.Empty<CatalogBrowseEntryResponse>(),
            ["EmptyText"] = "No books are known for this author yet."
        });
        StringAssert.Contains(html, "No books are known for this author yet.");
    }

    // ---- Name choices ------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task NamesakeChoicesAreSeparateLinksWithDistinctRecordsAndAWarning()
    {
        var one = Guid.Parse("33333333-0000-0000-0000-0000000abc01");
        var two = Guid.Parse("33333333-0000-0000-0000-0000000abc02");
        var choices = new CatalogBrowseNameChoicesResponse(
            [new(one, "Tom Clancy", 3, ["Red Storm Rising", "Patriot Games"]), new(two, "Tom Clancy", 1, [])], true);
        var html = await RenderAsync<BrowseNameChoices>(new() { ["Choices"] = choices, ["Label"] = "Tom Clancy", ["IsAuthor"] = true });

        StringAssert.Contains(html, "Matching names in your catalog");
        StringAssert.Contains(html, "A matching name is not proof of a match");
        StringAssert.Contains(html, $"href=\"authors/{one}\"");
        StringAssert.Contains(html, $"href=\"authors/{two}\"");
        StringAssert.Contains(html, "3 known books");
        StringAssert.Contains(html, "1 known book");
        StringAssert.Contains(html, "saved record …0abc01");
        StringAssert.Contains(html, "saved record …0abc02");
        StringAssert.Contains(html, "Red Storm Rising; Patriot Games");
        StringAssert.Contains(html, "More saved authors share this name than are shown");
        Assert.DoesNotContain("<meta http-equiv=\"refresh\"", html);
    }

    [TestMethod]
    public async Task SingleNamesakeStillCarriesTheIdentityWarningAndNoChoiceMeansSaidSo()
    {
        var single = await RenderAsync<BrowseNameChoices>(new()
        {
            ["Choices"] = new CatalogBrowseNameChoicesResponse([new(SeriesOne, "Saga", 2, [])], false),
            ["Label"] = "Saga",
            ["IsAuthor"] = false
        });
        StringAssert.Contains(single, "not proof of a match");
        StringAssert.Contains(single, $"href=\"series/{SeriesOne}\"");

        var none = await RenderAsync<BrowseNameChoices>(new()
        {
            ["Choices"] = new CatalogBrowseNameChoicesResponse([], false), ["Label"] = "Saga", ["IsAuthor"] = false
        });
        StringAssert.Contains(none, "No saved series has this exact name.");
    }

    // ---- Notes, pager, problems --------------------------------------------------------------------------------

    [TestMethod]
    public async Task NotesAppearInTheBodyAndAreAbsentWhenThereAreNoLimitations()
    {
        var shown = await RenderAsync<BrowseNotes>(new() { ["Limitations"] = (IReadOnlyList<string>)["source-sample", "identity-unverified", "truncated"], ["Observed"] = true });
        StringAssert.Contains(shown, "What this list is");
        StringAssert.Contains(shown, "may belong to a different person or series");
        StringAssert.Contains(shown, "incomplete");

        var none = await RenderAsync<BrowseNotes>(new() { ["Limitations"] = Array.Empty<string>() });
        Assert.DoesNotContain("What this list is", none);
    }

    [TestMethod]
    public async Task PagerOffersOnlyApplicableDirectionsAndNeverClaimsATotal()
    {
        static string Href(int page) => $"authors/x?page={page}";

        var first = await RenderAsync<BrowsePager>(new() { ["Page"] = 1, ["HasMore"] = true, ["HrefFor"] = (Func<int, string>)Href });
        StringAssert.Contains(first, "href=\"authors/x?page=2\"");
        Assert.DoesNotContain("Previous page", first);
        StringAssert.Contains(first, "More known books may follow");

        var middle = await RenderAsync<BrowsePager>(new() { ["Page"] = 3, ["HasMore"] = true, ["HrefFor"] = (Func<int, string>)Href });
        StringAssert.Contains(middle, "href=\"authors/x?page=2\"");
        StringAssert.Contains(middle, "href=\"authors/x?page=4\"");
        StringAssert.Contains(middle, "Page 3");

        var last = await RenderAsync<BrowsePager>(new() { ["Page"] = 2, ["HasMore"] = false, ["HrefFor"] = (Func<int, string>)Href });
        Assert.DoesNotContain("Next page", last);
        StringAssert.Contains(last, "Previous page");

        var only = await RenderAsync<BrowsePager>(new() { ["Page"] = 1, ["HasMore"] = false, ["HrefFor"] = (Func<int, string>)Href });
        Assert.DoesNotContain("browse-pager", only);
        Assert.DoesNotMatchRegex(@"Page \d+ of|of \d+ pages|\d+ pages", first + middle + last);
    }

    [TestMethod]
    public async Task PagerNeverOffersAPageBeyondTheBrowsingLimitAndSaysSoWithoutClaimingTheEnd()
    {
        static string Href(int page) => $"authors/x?page={page}";

        // The host can truthfully report more rows on its last browsable page; the UI must not link to the rejected page 101.
        var limit = await RenderAsync<BrowsePager>(new() { ["Page"] = CatalogBrowseLinks.MaximumPage, ["HasMore"] = true, ["HrefFor"] = (Func<int, string>)Href });
        Assert.DoesNotContain("page=101", limit);
        Assert.DoesNotContain("Next page", limit);
        Assert.DoesNotContain("More known books may follow on the next page", limit);
        StringAssert.Contains(limit, "href=\"authors/x?page=99\"");
        StringAssert.Contains(limit, "Previous page");
        StringAssert.Contains(limit, "Page 100");
        StringAssert.Contains(limit, "browse-limit");
        StringAssert.Contains(limit, "more known books than can be browsed here");
        Assert.DoesNotMatchRegex(@"no more|end of|all books|complete|\d+ (books|pages|results)", limit);

        // One page earlier the last permitted next page is still offered, with the ordinary wording.
        var before = await RenderAsync<BrowsePager>(new() { ["Page"] = CatalogBrowseLinks.MaximumPage - 1, ["HasMore"] = true, ["HrefFor"] = (Func<int, string>)Href });
        StringAssert.Contains(before, "href=\"authors/x?page=100\"");
        Assert.DoesNotContain("browse-limit", before);

        // The limit wording appears only when the host reported more rows.
        var exactEnd = await RenderAsync<BrowsePager>(new() { ["Page"] = CatalogBrowseLinks.MaximumPage, ["HasMore"] = false, ["HrefFor"] = (Func<int, string>)Href });
        Assert.DoesNotContain("browse-limit", exactEnd);
        Assert.DoesNotContain("Next page", exactEnd);
    }

    [TestMethod]
    public async Task KnownFormatsUseTheSharedMediaChipWithItsTooltipAndAnUnknownFormatStaysPlainText()
    {
        var known = await RenderAsync<BrowseBookList>(new()
        {
            ["Entries"] = new[] { Entry(Saved(WorkA, "Known", ["Ebook", "Audiobook"], "Collection", "Collection or combined volume")) }
        });
        // MediaTypeChip wraps each format in a MudTooltip; the version chip is deliberately not a media type.
        Assert.AreEqual(2, System.Text.RegularExpressions.Regex.Count(known, "mud-tooltip-root"), known);
        Assert.DoesNotContain("Format not reported", known);

        var unknown = await RenderAsync<BrowseBookList>(new() { ["Entries"] = new[] { Entry(Saved(WorkA, "Unknown")) } });
        Assert.DoesNotContain("mud-tooltip-root", unknown);
        StringAssert.Contains(unknown, "Format not reported");
    }

    [TestMethod]
    public async Task EachProblemStateHasItsOwnRecoveryAndNeverShowsAnExceptionBody()
    {
        var expired = await RenderAsync<BrowseProblem>(new() { ["Outcome"] = CatalogBrowseOutcome.NotFound, ["Observed"] = true });
        StringAssert.Contains(expired, "expired or is no longer available");
        StringAssert.Contains(expired, "href=\"search\"");
        StringAssert.Contains(expired, "Nothing was saved, requested or imported.");

        var missing = await RenderAsync<BrowseProblem>(new() { ["Outcome"] = CatalogBrowseOutcome.NotFound, ["Noun"] = "author" });
        StringAssert.Contains(missing, "This author could not be found in the catalog");
        Assert.DoesNotContain("expired", missing);

        var signedOut = await RenderAsync<BrowseProblem>(new() { ["Outcome"] = CatalogBrowseOutcome.Unauthorized });
        StringAssert.Contains(signedOut, "Your session has ended");
        StringAssert.Contains(signedOut, "href=\"login\"");

        var badPage = await RenderAsync<BrowseProblem>(new() { ["Outcome"] = CatalogBrowseOutcome.InvalidRequest, ["FirstPageHref"] = "series/abc" });
        StringAssert.Contains(badPage, "not available");
        StringAssert.Contains(badPage, "href=\"series/abc\"");

        var down = await RenderAsync<BrowseProblem>(new() { ["Outcome"] = CatalogBrowseOutcome.Unavailable });
        StringAssert.Contains(down, "could not be loaded right now");
        StringAssert.Contains(down, "Try again");
        Assert.DoesNotContain("Exception", down);
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
