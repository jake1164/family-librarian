using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Web.Client.Authentication;
using FamilyLibrarian.Web.Client.Catalog;
using FamilyLibrarian.Web.Client.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using MudBlazor.Services;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// Server-rendered HTML of the explicit source-lookup UI over a stub HTTP transport: what a reader actually sees for each
/// outcome, that an unnamed series is shown as unnamed rather than named, that capability is stated apart from results, that
/// nothing contacts a source on render, and that provider text is encoded. The data is hand-built, not a provider response.
/// </summary>
[TestClass]
public sealed class CatalogDiscoveryRenderTests
{
    private static readonly Guid Window = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid Parent = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Series1 = Guid.Parse("dddddddd-0000-0000-0000-000000000001");
    private static readonly Guid Series2 = Guid.Parse("dddddddd-0000-0000-0000-000000000002");
    private static readonly Guid Book1 = Guid.Parse("eeeeeeee-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Seen = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private sealed class StaticNavigationManager : NavigationManager
    {
        public StaticNavigationManager() => Initialize("http://localhost/", "http://localhost/");
    }

    private sealed class NoScriptRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => default;

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => default;
    }

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private int requests;

        public int Requests => Volatile.Read(ref requests);

        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requests);
            lock (Paths) Paths.Add(request.Method + " " + request.RequestUri!.PathAndQuery);
            return Task.FromResult(respond(request));
        }
    }

    private static async Task<(string Html, Stub Transport)> RenderAsync<T>(
        Func<HttpRequestMessage, HttpResponseMessage> respond, Dictionary<string, object?> parameters) where T : IComponent
    {
        var transport = new Stub(respond);
        var http = new HttpClient(transport) { BaseAddress = new Uri("http://localhost/") };
        await using var services = new ServiceCollection()
            .AddLogging().AddMudServices()
            .AddSingleton<IJSRuntime, NoScriptRuntime>()
            .AddSingleton<NavigationManager, StaticNavigationManager>()
            .AddSingleton(new CatalogDiscoveryApiClient(http, new AntiforgeryTokenProvider(http)))
            .BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters))).ToHtmlString());
        return (html, transport);
    }

    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value, options: WebJson) };

    private static CatalogDiscoveryResponse Result(
        string outcome = "Ok",
        string completion = "ExhaustedInCheckedScope",
        string operation = "FindSeriesForBook",
        string[]? notes = null,
        CatalogDiscoveryBookResponse[]? books = null,
        CatalogDiscoveryRefResponse[]? refs = null,
        bool canContinue = false,
        int offered = 0,
        int retained = 0,
        string capability = "Supported",
        string subject = "Book One") =>
        new(Window, Parent, Guid.NewGuid(), operation, "demo", "Demo Source", "Book", subject, outcome, completion,
            notes ?? [], capability, null, Seen, Seen.AddMinutes(5), 1, 0, 1, offered, retained, canContinue,
            books ?? [], refs ?? [], ["source-lookup"]);

    private static CatalogDiscoveryBookResponse BookWithSeries(params CatalogDiscoveryMembershipResponse[] memberships) =>
        new(Book1, "Book One", "Work", new DateOnly(1965, 1, 1), "Year", "Original",
            [new CatalogDiscoveryCreditResponse("Frank Herbert", Guid.NewGuid(), true)], memberships, null, null, false);

    private static Task<(string Html, Stub Transport)> RenderPage(CatalogDiscoveryResponse result) =>
        RenderAsync<SourceLookup>(_ => Json(result), new() { ["ContextId"] = Window });

    // ================================================================ the page

    [TestMethod]
    public async Task AnUnnamedSeriesIsShownAsUnnamedWithItsPositionAndNoNameOrIdIsInvented()
    {
        var (html, transport) = await RenderPage(Result(
            notes: ["series-name-not-provided", "series-position-conflict", "not-a-reviewed-code"],
            books: [BookWithSeries(
                new CatalogDiscoveryMembershipResponse(Series1, "Saga", false, "1", 1m, "native", null, true),
                new CatalogDiscoveryMembershipResponse(Series2, string.Empty, true, "7", 7m, "native", null, true))]));

        StringAssert.Contains(html, "Saga");
        StringAssert.Contains(html, "Series name not provided by Demo Source");
        StringAssert.Contains(html, "position 1");
        StringAssert.Contains(html, "position 7");
        StringAssert.Contains(html, "gave only an id for some series, not a name");
        StringAssert.Contains(html, "more than one position in the same series");
        Assert.DoesNotContain("not-a-reviewed-code", html, "An unknown code is skipped, never echoed.");
        Assert.DoesNotContain("a-reviewed", html);
        Assert.AreEqual(1, transport.Requests, "Rendering reads the window once and contacts no source.");
        Assert.AreEqual("GET /api/v1/catalog/discovery/windows/" + Window, transport.Paths.Single());
        Assert.AreEqual(2, CountOf(html, "Look up from the source…"), "Each series with a source id offers an explicit, not automatic, lookup.");
    }

    [TestMethod]
    [DataRow("Empty", "ExhaustedInCheckedScope", "had nothing for this question", "does not mean there is nothing elsewhere")]
    [DataRow("Ambiguous", "ExhaustedInCheckedScope", "More than one person fits", "Nothing was chosen for you")]
    [DataRow("Partial", "ProviderFailedPartial", "Only part of the answer could be read", "unknown, not absent")]
    [DataRow("NoUsableItems", "ExhaustedInCheckedScope", "answered, but nothing could be used", "different from sending none")]
    [DataRow("Throttled", "NotChecked", "asked for a wait", "Nothing was retried early")]
    [DataRow("TimedOut", "NotChecked", "did not answer in time", "the rest is unknown")]
    [DataRow("Unavailable", "NotChecked", "could not be asked", "says nothing about whether it holds")]
    [DataRow("Failed", "NotChecked", "could not be completed", "Nothing was saved")]
    public async Task EachOutcomeIsSaidInItsOwnWordsAndNeverAsACompleteAnswer(string outcome, string completion, string headline, string detail)
    {
        var (html, _) = await RenderPage(Result(outcome, completion));

        StringAssert.Contains(html, headline);
        StringAssert.Contains(html, detail);
        Assert.AreEqual(1, CountOf(html, "data-outcome=\"" + outcome + "\""), "The outcome code is exposed for assistive and test use.");
        Assert.DoesNotContain("complete list", html, StringComparison.OrdinalIgnoreCase);
        StringAssert.Contains(html, "Nothing is saved");
    }

    [TestMethod]
    public async Task AnExhaustedAnswerClaimsOnlyWhatTheSourceReturnedAndAContinuationOffersTheNextPage()
    {
        var (exhausted, _) = await RenderPage(Result());
        var (more, _) = await RenderPage(Result(completion: "MoreAvailable", canContinue: true, books: [BookWithSeries()]));

        StringAssert.Contains(exhausted, "not a promise that nothing more exists in the source");
        StringAssert.Contains(more, "More exists. This page is not the whole list");
        StringAssert.Contains(more, "Load the next page from Demo Source");
        Assert.DoesNotContain("Load the next page", exhausted);
    }

    [TestMethod]
    public async Task ACutResultSaysItWasCutAndOffersNoNextPage()
    {
        var (html, _) = await RenderPage(Result(
            completion: "ContextCapacity", notes: ["context-capacity"], offered: 50, retained: 12, canContinue: false,
            books: [BookWithSeries()]));

        StringAssert.Contains(html, "Some results could not be kept");
        StringAssert.Contains(html, "returned 50 items");
        StringAssert.Contains(html, "only hold 12");
        StringAssert.Contains(html, "a start of the list, not all of it");
        Assert.DoesNotContain("Load the next page", html);
    }

    [TestMethod]
    public async Task AuthorChoicesKeepEveryNamesakeWithDetailsAndNeverImplyThatMoreWorksMeansTheRightPerson()
    {
        var (html, _) = await RenderPage(Result(
            outcome: "Ambiguous", operation: "ResolveAuthor", notes: ["anchor-insufficient", "list-truncated"],
            refs:
            [
                new CatalogDiscoveryRefResponse(Series1, "Author", "Frank Herbert", false, true, "native", "8 October 1920 – 11 February 1986", 141, null, null, null),
                new CatalogDiscoveryRefResponse(Series2, "Author", "Frank Herbert", false, true, "native", "born 1872", 1, null, null, null)
            ]));

        Assert.AreEqual(2, CountOf(html, "class=\"lookup-ref\""), "Two people with one name stay two choices.");
        StringAssert.Contains(html, "8 October 1920 &#x2013; 11 February 1986");
        StringAssert.Contains(html, "141 works listed. A larger count is not evidence this is the person you mean.");
        StringAssert.Contains(html, "1 work listed.");
        StringAssert.Contains(html, "A name alone does not identify a person.");
        StringAssert.Contains(html, "More people share this name than could be read.");
    }

    [TestMethod]
    public async Task DerivedAuthorSeriesAndCanonicalStatementsAreLabelledAsWhatTheyAre()
    {
        var (html, _) = await RenderPage(Result(
            operation: "GetAuthorSeries",
            refs:
            [
                new CatalogDiscoveryRefResponse(Series1, "Series", "Saga", false, true, "derived:author-works-checked-pages", null, null, true, 3, "Verified")
            ]));

        StringAssert.Contains(html, "Worked out from the works that were read, not stated by the source.");
        StringAssert.Contains(html, "says this series duplicates another one. Both are kept; nothing was merged.");
        StringAssert.Contains(html, "marked complete");
        StringAssert.Contains(html, "not a promise of what is published");
    }

    [TestMethod]
    public async Task PublicationDatesKeepTheirPrecisionAndWhatTheyDescribe()
    {
        var yearOnly = CatalogDiscoveryCopy.DateText(new DateOnly(1965, 1, 1), "Year", "Original");
        var edition = CatalogDiscoveryCopy.DateText(new DateOnly(2023, 5, 2), "Day", "EditionDerived");
        var unverified = CatalogDiscoveryCopy.DateText(new DateOnly(1998, 3, 1), "Month", "Unknown");
        var none = CatalogDiscoveryCopy.DateText(null, null, null);

        Assert.AreEqual("1965 (year only; original publication)", yearOnly);
        Assert.AreEqual("2 May 2023 (date of one edition, not necessarily the original)", edition);
        Assert.AreEqual("March 1998 (month only; what this date describes is not stated)", unverified);
        Assert.AreEqual("Date not stated", none);
        var (html, _) = await RenderPage(Result(operation: "GetSeriesMembers", books: [BookWithSeries()]));
        StringAssert.Contains(html, "1965 (year only; original publication)");
        StringAssert.Contains(WebUtility.HtmlDecode(html), "this source's work record");

        // A series lookup describes the book already on screen: it carries no date of its own and none is claimed.
        var (seriesHtml, _) = await RenderPage(Result(books: [BookWithSeries()]));
        Assert.DoesNotContain("Date not stated", seriesHtml);
        Assert.DoesNotContain("1965", seriesHtml);
    }

    [TestMethod]
    public async Task ProviderSuppliedTextIsEncodedNotInjected()
    {
        var hostile = new CatalogDiscoveryBookResponse(
            Book1, "<script>alert(1)</script>", "Edition", null, null, null,
            [new CatalogDiscoveryCreditResponse("<img src=x onerror=alert(2)>", Guid.NewGuid(), false)],
            [new CatalogDiscoveryMembershipResponse(Series1, "<b>Bold</b>", false, "<i>1</i>", null, "native", null, false)], null, null, false);

        var (html, _) = await RenderPage(Result(books: [hostile], subject: "<svg onload=alert(3)>"));

        Assert.DoesNotContain("<script>alert", html);
        Assert.DoesNotContain("<img src=x", html);
        Assert.DoesNotContain("<b>Bold</b>", html);
        Assert.DoesNotContain("<svg onload", html);
        StringAssert.Contains(html, "&lt;script&gt;");
    }

    [TestMethod]
    public async Task ARecordWithoutASourceIdOffersNoLookup()
    {
        var (html, _) = await RenderPage(Result(books: [BookWithSeries(
            new CatalogDiscoveryMembershipResponse(Series1, "Loose Label", false, "2", 2m, "native", null, false))]));

        StringAssert.Contains(html, "Loose Label");
        Assert.AreEqual(0, CountOf(html, "Look up from the source…"), "A label alone is never looked up.");
    }

    [TestMethod]
    public async Task AnExpiredOrMissingResultIsExplainedAndNothingElseIsShown()
    {
        var (html, _) = await RenderAsync<SourceLookup>(_ => new HttpResponseMessage(HttpStatusCode.NotFound), new() { ["ContextId"] = Window });
        var (down, _) = await RenderAsync<SourceLookup>(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError), new() { ["ContextId"] = Window });

        StringAssert.Contains(html, "This lookup result has expired or is no longer available");
        StringAssert.Contains(html, "Nothing was saved");
        StringAssert.Contains(down, "This could not be loaded right now");
        Assert.DoesNotContain("data-outcome", html);
    }

    // ================================================================ stated contributor roles

    private static CatalogDiscoveryBookResponse BookWithCredits(params CatalogDiscoveryCreditResponse[] credits) =>
        new(Book1, "Book One", "Work", null, null, null, credits, [], null, null, false);

    [TestMethod]
    public async Task ASoleIllustratorIsShownQualifiedAndNeverAsByAnAuthor()
    {
        var (html, _) = await RenderPage(Result(
            operation: "GetAuthorWorks",
            books: [BookWithCredits(new CatalogDiscoveryCreditResponse("Pat Painter", Guid.NewGuid(), false, "illustrator"))]));

        var text = WebUtility.HtmlDecode(html);
        StringAssert.Contains(text, "Credited: Pat Painter (illustrator)");
        Assert.DoesNotContain("By Pat Painter", text);
        Assert.DoesNotContain("Author not reported", text, "A stated credit exists, so nothing says there is none.");
    }

    [TestMethod]
    public async Task ADroppedRoleIsShownNeutrallyOnThePageNeverAsByAndMixedCreditsKeepTheirDistinctions()
    {
        var (sole, _) = await RenderPage(Result(
            operation: "GetAuthorWorks",
            books: [BookWithCredits(new CatalogDiscoveryCreditResponse("Pat Painter", Guid.NewGuid(), false, null, true))]));
        var (mixed, _) = await RenderPage(Result(
            operation: "GetAuthorWorks",
            books:
            [
                BookWithCredits(
                    new CatalogDiscoveryCreditResponse("Ann Author", Guid.NewGuid(), true, null),
                    new CatalogDiscoveryCreditResponse("Pat Painter", Guid.NewGuid(), false, null, true),
                    new CatalogDiscoveryCreditResponse("<b>Tess</b>", Guid.NewGuid(), false, "translator"))
            ]));

        StringAssert.Contains(WebUtility.HtmlDecode(sole), "Credited: Pat Painter (role not retained)");
        Assert.DoesNotContain("By Pat Painter", WebUtility.HtmlDecode(sole));
        var text = WebUtility.HtmlDecode(mixed);
        StringAssert.Contains(text, "By Ann Author");
        StringAssert.Contains(text, "Also credited: Pat Painter (role not retained), <b>Tess</b> (translator)");
        Assert.DoesNotContain("By Ann Author, Pat Painter", text);
        Assert.DoesNotContain("<b>Tess</b>", mixed, "Provider text stays encoded in the markup.");
    }

    [TestMethod]
    public void TheCreditLineKeepsAuthorsApartFromOtherStatedRolesAndNeverInventsOne()
    {
        static CatalogDiscoveryCreditResponse Credit(string name, string? role) => new(name, Guid.NewGuid(), false, role);

        Assert.AreEqual("By Ann Author", CatalogDiscoveryCopy.CreditLine([Credit("Ann Author", null)]));
        Assert.AreEqual("By Ann Author, Bo Writer", CatalogDiscoveryCopy.CreditLine([Credit("Ann Author", "Author"), Credit("Bo Writer", " author ")]));
        Assert.AreEqual("By Ann Author · Also credited: Pat Painter (illustrator), Tess Translator (translator)",
            CatalogDiscoveryCopy.CreditLine([Credit("Ann Author", null), Credit("Pat Painter", "illustrator"), Credit("Tess Translator", " translator ")]));
        Assert.AreEqual("Credited: Narrator Nia (narrator)", CatalogDiscoveryCopy.CreditLine([Credit("Narrator Nia", "narrator")]));
        Assert.AreEqual("Author not reported", CatalogDiscoveryCopy.CreditLine([]));
    }

    [TestMethod]
    public async Task AProviderSuppliedRoleIsEncodedNotInjected()
    {
        var (html, _) = await RenderPage(Result(
            operation: "GetAuthorWorks",
            books: [BookWithCredits(new CatalogDiscoveryCreditResponse("Pat Painter", Guid.NewGuid(), false, "<img src=x onerror=alert(1)>"))]));

        Assert.DoesNotContain("<img src=x", html);
        StringAssert.Contains(html, "&lt;img src=x");
    }

    // ================================================================ the actions

    [TestMethod]
    public async Task TheActionsReadNothingAndContactNothingUntilAPersonAsks()
    {
        var (html, transport) = await RenderAsync<SourceLookupActions>(
            _ => Json(new CatalogExpansionOffersResponse([])),
            new() { ["ContextId"] = Parent, ["SubjectId"] = Series1, ["Describe"] = "Saga" });

        StringAssert.Contains(html, "Look up from the source…");
        StringAssert.Contains(html, "aria-label=\"Look up more about Saga from its source\"");
        Assert.AreEqual(0, transport.Requests, "Rendering the button makes no request of any kind.");
    }

    [TestMethod]
    public async Task EagerActionsReadOfferedLookupsWithAGetAndStateCapabilityApartFromResults()
    {
        var offers = new CatalogExpansionOffersResponse(
        [
            new CatalogExpansionOfferResponse("BookSeries", "googlebooks", "Google Books", "Unverified", "live-probe-not-run-coverage-uneven", true),
            new CatalogExpansionOfferResponse("SeriesMembers", "googlebooks", "Google Books", "Unsupported", "series-endpoints-not-key-accessible", false)
        ]);
        var (html, transport) = await RenderAsync<SourceLookupActions>(
            _ => Json(offers),
            new() { ["ContextId"] = Parent, ["SubjectId"] = Series1, ["Eager"] = true });

        Assert.AreEqual("GET /api/v1/catalog/discovery/contexts/" + Parent + "/subjects/" + Series1 + "/offers", transport.Paths.Single());
        StringAssert.Contains(html, "Find this book&#x27;s series in Google Books");
        StringAssert.Contains(html, "not yet verified against Google Books");
        StringAssert.Contains(html, "List the books Google Books has in this series is not available.");
        StringAssert.Contains(html, "does not offer a series listing at this app&#x27;s level of access");
        Assert.AreEqual(1, CountOf(html, "data-kind=\"BookSeries\""), "Only the lookup that can start has a button.");
        Assert.DoesNotContain("<button type=\"button\" class=\"mud-button-root mud-button mud-button-filled mud-button-filled-primary mud-button-filled-size-small mud-ripple\" data-kind=\"SeriesMembers\"", html);
    }

    [TestMethod]
    public async Task EagerActionsWithNothingToOfferRenderNothingAtAll()
    {
        var (html, _) = await RenderAsync<SourceLookupActions>(
            _ => Json(new CatalogExpansionOffersResponse([])),
            new() { ["ContextId"] = Parent, ["SubjectId"] = Series1, ["Eager"] = true });

        Assert.DoesNotContain("Look up from the source", html);
        Assert.DoesNotContain("lookup-none", html);
    }

    // ================================================================ copy

    [TestMethod]
    public void ThrottleAndRefusalWordingIsSpecificAndNeverPromisesAnEarlyRetry()
    {
        Assert.AreEqual("Too many lookups in the last minute. You can ask again in about 40 seconds.",
            CatalogDiscoveryCopy.ThrottleText(LookupOutcome.RateLimited, TimeSpan.FromSeconds(39.2)));
        StringAssert.Contains(CatalogDiscoveryCopy.ThrottleText(LookupOutcome.Busy, null), "already running");
        StringAssert.Contains(CatalogDiscoveryCopy.RefusalText("source-disabled", "Demo Source"), "switched off");
        StringAssert.Contains(CatalogDiscoveryCopy.RefusalText("journey-ended", "Demo Source"), "Search again");
        Assert.IsNull(CatalogDiscoveryCopy.NoteText("a-code-with-no-reviewed-text", "Demo Source"));
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
