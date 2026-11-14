using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Web.Client.Catalog;
using Microsoft.AspNetCore.Components.Authorization;

namespace FamilyLibrarian.Web.Tests;

[TestClass]
public sealed class CatalogBrowseNavigationTests
{
    private static readonly Guid Context = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Subject = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Other = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    private static CatalogBookCandidateResponse Candidate(
        string[]? authors = null, string[]? series = null, CatalogBookNavigationResponse? navigation = null) =>
        new("demo", "Demo", "book-1", "Title", authors ?? ["Ann Author", "Bo Builder"], null, null, null, [],
            (series ?? ["Saga"]).Select((name, index) => new CatalogSeriesResponse(name, (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), index == 0)).ToArray(),
            null, null, [], null, Navigation: navigation);

    private static CatalogBookNavigationResponse Navigation(
        Guid? context = null, DateTimeOffset? expires = null, params (int Ordinal, string Label, bool Series)[] refs) =>
        new(context ?? Context, expires ?? Now.AddMinutes(5),
            refs.Where(r => !r.Series).Select(r => new CatalogNavigationReferenceResponse(r.Ordinal, r.Label, Guid.NewGuid())).ToArray(),
            refs.Where(r => r.Series).Select(r => new CatalogNavigationReferenceResponse(r.Ordinal, r.Label, Guid.NewGuid())).ToArray(),
            []);

    // ---- Routes and encoding ------------------------------------------------------------------------------------

    [TestMethod]
    public void BrowseRoutesAreLocalRelativeAndOnlyCarryAPageAfterTheFirst()
    {
        Assert.AreEqual($"authors/{Subject}", CatalogBrowseLinks.AuthorHref(Subject));
        Assert.AreEqual($"authors/{Subject}?page=3", CatalogBrowseLinks.AuthorHref(Subject, 3));
        Assert.AreEqual($"series/{Subject}", CatalogBrowseLinks.SeriesHref(Subject, 1));
        Assert.AreEqual($"browse/authors/{Context}/{Subject}", CatalogBrowseLinks.ObservedAuthorHref(Context, Subject));
        Assert.AreEqual($"browse/series/{Context}/{Subject}?page=2", CatalogBrowseLinks.ObservedSeriesHref(Context, Subject, 2));
    }

    [TestMethod]
    [DataRow(null, 1)]
    [DataRow("", 1)]
    [DataRow("1", 1)]
    [DataRow("2", 2)]
    [DataRow("99", 99)]
    [DataRow("100", 100)]
    [DataRow("007", 7)]
    public void ARawPageValueThatIsMissingEmptyOrInRangeParsesAndAMissingOrEmptyOneMeansTheFirstPage(string? raw, int expected)
    {
        Assert.IsTrue(CatalogBrowseLinks.TryParsePage(raw, out var page));
        Assert.AreEqual(expected, page);
    }

    [TestMethod]
    [DataRow("abc")]
    [DataRow("2147483648")]
    [DataRow("99999999999999999999")]
    [DataRow("0")]
    [DataRow("101")]
    [DataRow("-1")]
    [DataRow("+2")]
    [DataRow("1.5")]
    [DataRow("1e2")]
    [DataRow(" ")]
    [DataRow(" 2")]
    [DataRow("2 ")]
    [DataRow("２")]
    [DataRow("1,0")]
    public void AnyOtherRawPageValueIsRejectedWithoutThrowing(string raw)
    {
        Assert.IsFalse(CatalogBrowseLinks.TryParsePage(raw, out _));
    }

    [TestMethod]
    public void ProviderBookRouteKeepsTheExistingShapeAndEncodesReservedAndUnicodeCharacters()
    {
        Assert.AreEqual("search/demo/novel", CatalogBrowseLinks.TryBookHref("demo", "novel"));
        Assert.AreEqual("search/demo/a%2Fb%3Fc%23d%20%C3%A9%E6%9B%B8%25", CatalogBrowseLinks.TryBookHref("demo", "a/b?c#d é書%"));
        Assert.AreEqual("search/my%20source/x", CatalogBrowseLinks.TryBookHref("my source", "x"));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow(".")]
    [DataRow("..")]
    [DataRow("a\u0000b")]
    [DataRow("line\nbreak")]
    public void UnroutableBookReferencesYieldNoRouteSoThePageShowsRecovery(string? externalId)
    {
        Assert.IsNull(CatalogBrowseLinks.TryBookHref("demo", externalId));
        Assert.IsNull(CatalogBrowseLinks.TryBookHref(externalId, "book"));
    }

    [TestMethod]
    public void BookReferenceLengthLimitsMatchTheHost()
    {
        Assert.IsNotNull(CatalogBrowseLinks.TryBookHref(new string('p', 128), new string('e', 512)));
        Assert.IsNull(CatalogBrowseLinks.TryBookHref(new string('p', 129), "e"));
        Assert.IsNull(CatalogBrowseLinks.TryBookHref("p", new string('e', 513)));
    }

    // ---- Typed return context -----------------------------------------------------------------------------------

    [TestMethod]
    public void ReturnContextRoundTripsThroughTheQueryForEveryKind()
    {
        foreach (var back in new[]
        {
            new BrowseReturn(BrowseReturnKind.Author, Subject),
            new BrowseReturn(BrowseReturnKind.Series, Subject, Page: 4),
            new BrowseReturn(BrowseReturnKind.ObservedAuthor, Context, Subject),
            new BrowseReturn(BrowseReturnKind.ObservedSeries, Context, Subject, 2)
        })
        {
            var query = System.Web.HttpUtility.ParseQueryString(CatalogBrowseLinks.ReturnQuery(back));
            var parsed = CatalogBrowseLinks.TryParseReturn(
                query["back"],
                Guid.TryParse(query["bid"], out var id) ? id : null,
                Guid.TryParse(query["bsub"], out var sub) ? sub : null,
                int.TryParse(query["bp"], out var page) ? page : null);
            Assert.AreEqual(back, parsed);
        }
    }

    [TestMethod]
    public void ReturnHrefIsRebuiltFromValidatedPartsAndNeverFromRawInput()
    {
        Assert.AreEqual($"authors/{Subject}?page=2", CatalogBrowseLinks.ReturnHref(new(BrowseReturnKind.Author, Subject, Page: 2)));
        Assert.AreEqual($"series/{Subject}", CatalogBrowseLinks.ReturnHref(new(BrowseReturnKind.Series, Subject)));
        Assert.AreEqual($"browse/authors/{Context}/{Subject}", CatalogBrowseLinks.ReturnHref(new(BrowseReturnKind.ObservedAuthor, Context, Subject)));
        Assert.AreEqual($"browse/series/{Context}/{Subject}", CatalogBrowseLinks.ReturnHref(new(BrowseReturnKind.ObservedSeries, Context, Subject)));
        Assert.AreEqual("Back to author", CatalogBrowseLinks.ReturnLabel(new(BrowseReturnKind.Author, Subject)));
        Assert.AreEqual("Back to series", CatalogBrowseLinks.ReturnLabel(new(BrowseReturnKind.ObservedSeries, Context, Subject)));
    }

    [TestMethod]
    [DataRow("https://evil.example/", true, false, null)]
    [DataRow("//evil.example", true, false, null)]
    [DataRow("javascript:alert(1)", true, false, null)]
    [DataRow(null, true, false, null)]
    [DataRow("", true, false, null)]
    [DataRow("work", true, false, null)]
    [DataRow("Author", true, false, null)]    // tokens are exact
    [DataRow("author", false, false, null)]   // no id
    [DataRow("author", true, true, null)]     // subject on a saved page
    [DataRow("oauthor", true, false, null)]   // observed page missing its subject
    [DataRow("series", true, false, 0)]
    [DataRow("series", true, false, 101)]
    [DataRow("series", true, false, -1)]
    public void InvalidReturnContextIsRejectedSoItCannotBecomeARedirect(string? kind, bool hasId, bool hasSubject, int? page)
    {
        Assert.IsNull(CatalogBrowseLinks.TryParseReturn(kind, hasId ? Subject : null, hasSubject ? Other : null, page));
    }

    [TestMethod]
    [DataRow("author", "not-a-guid", null, null)]
    [DataRow("author", "{33333333-3333-3333-3333-333333333333}", null, null)]
    [DataRow("author", "33333333333333333333333333333333", null, null)]
    [DataRow("author", "33333333-3333-3333-3333-333333333333", "bad", null)]
    [DataRow("author", "33333333-3333-3333-3333-333333333333", null, "x")]
    [DataRow("author", "33333333-3333-3333-3333-333333333333", null, "-2")]
    [DataRow("author", "33333333-3333-3333-3333-333333333333", null, "999999999999999")]
    [DataRow(null, "33333333-3333-3333-3333-333333333333", null, null)]
    public void MalformedRawReturnQueryValuesMeanNoReturnContextAndNeverThrow(string? kind, string? id, string? subject, string? page)
    {
        Assert.IsNull(CatalogBrowseLinks.TryParseReturn(kind, id, subject, page));
    }

    [TestMethod]
    public void WellFormedRawReturnQueryValuesParse()
    {
        Assert.AreEqual(
            new BrowseReturn(BrowseReturnKind.ObservedSeries, Context, Subject, 7),
            CatalogBrowseLinks.TryParseReturn("oseries", Context.ToString(), Subject.ToString(), "7"));
        Assert.AreEqual(new BrowseReturn(BrowseReturnKind.Author, Other), CatalogBrowseLinks.TryParseReturn("author", Other.ToString(), null, null));
    }

    [TestMethod]
    public void EmptyGuidReturnContextIsRejected()
    {
        Assert.IsNull(CatalogBrowseLinks.TryParseReturn("author", Guid.Empty, null, null));
        Assert.IsNull(CatalogBrowseLinks.TryParseReturn("oauthor", Context, Guid.Empty, null));
    }

    [TestMethod]
    public void ReturnContextOnABookLinkIsAppendedAsTypedQueryValues()
    {
        var back = new BrowseReturn(BrowseReturnKind.ObservedSeries, Context, Subject, 2);
        Assert.AreEqual($"search/demo/b1?back=oseries&bid={Context}&bsub={Subject}&bp=2", CatalogBrowseLinks.TryBookHref("demo", "b1", back));
        Assert.AreEqual($"works/{Other}?back=oseries&bid={Context}&bsub={Subject}&bp=2", CatalogBrowseLinks.WorkHref(Other, back));
        Assert.AreEqual($"works/{Other}", CatalogBrowseLinks.WorkHref(Other));
    }

    // ---- Candidate navigation references ------------------------------------------------------------------------

    [TestMethod]
    public void ReferencesMatchOnlyTheExactDisplayedOrdinalAndLabel()
    {
        var navigation = Navigation(refs: [(0, "Ann Author", false), (1, "Bo Builder", false), (0, "Saga", true)]);
        var candidate = Candidate(navigation: navigation);

        Assert.AreEqual(navigation.Authors[0].SubjectId, CatalogBrowseLinks.AuthorReference(candidate, 0)!.SubjectId);
        Assert.AreEqual(BrowseReturnKind.ObservedAuthor, CatalogBrowseLinks.AuthorReference(candidate, 1)!.Kind);
        Assert.AreEqual(Context, CatalogBrowseLinks.AuthorReference(candidate, 1)!.Id);
        Assert.AreEqual(navigation.Series[0].SubjectId, CatalogBrowseLinks.SeriesReference(candidate, 0)!.SubjectId);
        Assert.IsNull(CatalogBrowseLinks.AuthorReference(candidate, 2), "no such displayed author");
        Assert.IsNull(CatalogBrowseLinks.AuthorReference(candidate, -1));
    }

    [TestMethod]
    public void EqualLabelsAtDifferentOrdinalsKeepTheirOwnSubjectIds()
    {
        var navigation = Navigation(refs: [(0, "Shared", true), (1, "Shared", true)]);
        var candidate = Candidate(series: ["Shared", "Shared"], navigation: navigation);

        Assert.AreEqual(navigation.Series[0].SubjectId, CatalogBrowseLinks.SeriesReference(candidate, 0)!.SubjectId);
        Assert.AreEqual(navigation.Series[1].SubjectId, CatalogBrowseLinks.SeriesReference(candidate, 1)!.SubjectId);
        Assert.AreNotEqual(navigation.Series[0].SubjectId, navigation.Series[1].SubjectId);
    }

    [TestMethod]
    public void ALabelChangedFromTheIssuedReferenceOrALookalikeGetsNoLink()
    {
        var navigation = Navigation(refs: [(0, "Saga (Chronological)", true)]);
        Assert.IsNull(CatalogBrowseLinks.SeriesReference(Candidate(series: ["Saga"], navigation: navigation), 0), "qualifier stripped");
        Assert.IsNull(CatalogBrowseLinks.SeriesReference(Candidate(series: ["saga (chronological)"], navigation: navigation), 0), "case differs");
        Assert.IsNotNull(CatalogBrowseLinks.SeriesReference(Candidate(series: ["Saga (Chronological)"], navigation: navigation), 0));
    }

    [TestMethod]
    public void NavigationWithoutAContextOrAtAllOffersNoLinks()
    {
        Assert.IsNull(CatalogBrowseLinks.AuthorReference(Candidate(), 0));
        var unavailable = new CatalogBookNavigationResponse(null, null, [], [], ["navigation-unavailable"]);
        Assert.IsNull(CatalogBrowseLinks.AuthorReference(Candidate(navigation: unavailable), 0));
    }

    // ---- Fresh detail versus cached search navigation -----------------------------------------------------------

    [TestMethod]
    public void RicherCachedNavigationIsKeptWhenIdentityAndLabelsAgree()
    {
        var cachedNav = Navigation(refs: [(0, "Ann Author", false), (1, "Bo Builder", false), (0, "Saga", true)]);
        var freshNav = Navigation(Other, refs: [(0, "Ann Author", false)]);
        var fresh = Candidate(navigation: freshNav);
        var cached = Candidate(navigation: cachedNav);

        var result = CatalogBrowseLinks.WithBestNavigation(fresh, cached, Now);

        Assert.AreSame(cachedNav, result.Navigation);
        Assert.AreEqual("Title", result.Title, "fresh metadata still wins for everything but navigation");
    }

    [TestMethod]
    public void CachedNavigationIsDroppedWhenAnyOrdinalOrLabelDisagrees()
    {
        var freshNav = Navigation(Other, refs: [(0, "Ann Author", false)]);
        var fresh = Candidate(authors: ["Ann Author", "Cy Changed"], navigation: freshNav);
        var cached = Candidate(navigation: Navigation(refs: [(0, "Ann Author", false), (1, "Bo Builder", false)]));

        Assert.AreSame(freshNav, CatalogBrowseLinks.WithBestNavigation(fresh, cached, Now).Navigation);

        var reordered = Candidate(authors: ["Bo Builder", "Ann Author"], navigation: freshNav with { Authors = [] });
        var result = CatalogBrowseLinks.WithBestNavigation(reordered, cached, Now);
        Assert.IsNull(CatalogBrowseLinks.AuthorReference(result, 0), "an old subject id must never attach to a reordered record");
        Assert.IsNull(CatalogBrowseLinks.AuthorReference(result, 1), "an old subject id must never attach to a reordered record");
    }

    [TestMethod]
    public void CachedNavigationForADifferentRecordIsNeverUsed()
    {
        var cached = Candidate(navigation: Navigation(refs: [(0, "Ann Author", false)])) with { ExternalId = "book-2" };
        var result = CatalogBrowseLinks.WithBestNavigation(Candidate(), cached, Now);
        Assert.IsNull(result.Navigation);

        var otherProvider = Candidate(navigation: Navigation(refs: [(0, "Ann Author", false)])) with { ProviderId = "other" };
        Assert.IsNull(CatalogBrowseLinks.WithBestNavigation(Candidate(), otherProvider, Now).Navigation);
    }

    [TestMethod]
    public void ExpiredCachedNavigationYieldsToFreshAndAnExpiredFreshOneIsNotOffered()
    {
        var expired = Navigation(expires: Now.AddSeconds(-1), refs: [(0, "Ann Author", false), (1, "Bo Builder", false)]);
        var freshNav = Navigation(Other, refs: [(0, "Ann Author", false)]);

        Assert.AreSame(freshNav, CatalogBrowseLinks.WithBestNavigation(Candidate(navigation: freshNav), Candidate(navigation: expired), Now).Navigation);
        Assert.IsNull(CatalogBrowseLinks.WithBestNavigation(Candidate(navigation: expired), null, Now).Navigation);
    }

    [TestMethod]
    public void CachedNavigationThatLinksFewerLabelsThanFreshDoesNotReplaceIt()
    {
        var freshNav = Navigation(Other, refs: [(0, "Ann Author", false), (1, "Bo Builder", false)]);
        var cached = Candidate(navigation: Navigation(refs: [(0, "Ann Author", false)]));
        Assert.AreSame(freshNav, CatalogBrowseLinks.WithBestNavigation(Candidate(navigation: freshNav), cached, Now).Navigation);
    }

    [TestMethod]
    public void NoNavigationAnywhereStaysHonestlyUnavailable()
    {
        Assert.IsNull(CatalogBrowseLinks.WithBestNavigation(Candidate(), null, Now).Navigation);
        Assert.IsNull(CatalogBrowseLinks.WithBestNavigation(Candidate(), Candidate(), Now).Navigation);
    }

    // ---- Cached-book fallback -----------------------------------------------------------------------------------

    private static CatalogObservedBookResponse Observed(bool truncated = false, string[]? limitations = null) =>
        new(Context,
            new CatalogBrowseBookResponse(
                null, "demo", "book-1", "Cached Title", new DateOnly(1984, 5, 1),
                [new(null, Subject, "Ann Author", null), new(null, Other, "Bo Builder", "Illustrator")],
                [new(null, Guid.NewGuid(), "Saga (Chronological)", "2.5", 2.5m, true)],
                [new("demo", "Demo Catalog", Now)],
                ["Ebook"], "Collection", "Collection or combined volume", truncated),
            Now, Now.AddMinutes(8), limitations ?? ["source-sample"]);

    [TestMethod]
    public void PreviewCandidateKeepsKnownFactsAndInventsNothing()
    {
        var preview = CatalogBrowseLinks.ToPreviewCandidate(Observed(), "demo", "book-1");

        Assert.AreEqual("Demo Catalog", preview.ProviderName);
        Assert.AreEqual("Cached Title", preview.Title);
        Assert.HasCount(2, preview.Authors);
        Assert.AreEqual("Ann Author", preview.Authors[0]);
        Assert.AreEqual("Bo Builder", preview.Authors[1]);
        Assert.AreEqual("Saga (Chronological)", preview.Series[0].Name);
        Assert.AreEqual("2.5", preview.Series[0].PositionLabel);
        Assert.AreEqual("Collection", preview.VersionKind);
        Assert.AreEqual("Collection or combined volume", preview.VersionLabel);
        Assert.IsNull(preview.Description);
        Assert.IsEmpty(preview.Editions, "no edition may be fabricated from a format name");
        Assert.IsNull(preview.CoverUrl);
    }

    [TestMethod]
    public void PreviewCandidateIssuesNavigationOnlyForACompleteRecord()
    {
        var complete = CatalogBrowseLinks.ToPreviewCandidate(Observed(), "demo", "book-1");
        Assert.AreEqual(Context, complete.Navigation!.ContextId);
        Assert.AreEqual(Subject, CatalogBrowseLinks.AuthorReference(complete, 0)!.SubjectId);
        Assert.AreEqual(Other, CatalogBrowseLinks.AuthorReference(complete, 1)!.SubjectId);
        Assert.IsNotNull(CatalogBrowseLinks.SeriesReference(complete, 0));

        // An omitted credit would shift positions, so index could no longer be trusted as the original ordinal.
        Assert.IsNull(CatalogBrowseLinks.ToPreviewCandidate(Observed(truncated: true), "demo", "book-1").Navigation);
        Assert.IsNull(CatalogBrowseLinks.ToPreviewCandidate(Observed(limitations: ["source-sample", "truncated"]), "demo", "book-1").Navigation);
    }

    [TestMethod]
    public void PreviewCandidateWithUnknownVersionKeepsTheStandardUnknownWording()
    {
        var book = Observed().Book with { VersionKind = null, VersionLabel = null, Credits = [], Memberships = [] };
        var preview = CatalogBrowseLinks.ToPreviewCandidate(Observed() with { Book = book }, "demo", "book-1");
        Assert.AreEqual("Unspecified", preview.VersionKind);
        Assert.AreEqual("Version unknown", preview.VersionLabel);
        Assert.IsEmpty(preview.Authors);
    }

    // ---- Limitation copy ----------------------------------------------------------------------------------------

    [TestMethod]
    public void LimitationCopyNeverClaimsCompletenessAndIgnoresUnknownCodes()
    {
        var lines = CatalogBrowseCopy.Describe(
            ["known-local-only", "source-sample", "identity-unverified", "grouping-page-local", "truncated", "source-failed", "<script>alert(1)</script>", "truncated"],
            observed: true, noun: "books");

        Assert.HasCount(6, lines);
        var joined = string.Join(' ', lines);
        Assert.DoesNotContain("script", joined);
        foreach (var banned in new[] { "all books", "latest", "complete series", "complete bibliography is" })
            Assert.DoesNotContain(banned, joined, StringComparison.OrdinalIgnoreCase);
        StringAssert.Contains(joined, "not a complete bibliography");
    }

    // ---- API client status mapping ------------------------------------------------------------------------------

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public List<HttpMethod> Methods { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            Methods.Add(request.Method);
            return Task.FromResult(respond(request));
        }
    }

    private static (CatalogBrowseApiClient Client, StubHandler Handler) ClientFor(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        return (new CatalogBrowseApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }), handler);
    }

    [TestMethod]
    public async Task ClientCallsOnlyGetRoutesWithEncodedReferences()
    {
        var (client, handler) = ClientFor(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        await client.GetAuthorAsync(Subject);
        await client.GetSeriesAsync(Subject, 3);
        await client.GetObservedAuthorAsync(Context, Subject, 2);
        await client.GetObservedSeriesAsync(Context, Subject);
        await client.GetObservedBookAsync(Context, "my source", "a/b?c#d é");

        CollectionAssert.AreEqual(new[]
        {
            $"/api/v1/catalog/browse/authors/{Subject}",
            $"/api/v1/catalog/browse/series/{Subject}?page=3",
            $"/api/v1/catalog/browse/contexts/{Context}/authors/{Subject}?page=2",
            $"/api/v1/catalog/browse/contexts/{Context}/series/{Subject}",
            $"/api/v1/catalog/browse/contexts/{Context}/books/my%20source?externalId=a%2Fb%3Fc%23d%20%C3%A9"
        }, handler.Requests);
        Assert.IsTrue(handler.Methods.All(method => method == HttpMethod.Get), "browsing never writes");
    }

    [TestMethod]
    [DataRow(HttpStatusCode.NotFound, CatalogBrowseOutcome.NotFound)]
    [DataRow(HttpStatusCode.BadRequest, CatalogBrowseOutcome.InvalidRequest)]
    [DataRow(HttpStatusCode.Unauthorized, CatalogBrowseOutcome.Unauthorized)]
    [DataRow(HttpStatusCode.Forbidden, CatalogBrowseOutcome.Unauthorized)]
    [DataRow(HttpStatusCode.InternalServerError, CatalogBrowseOutcome.Unavailable)]
    [DataRow(HttpStatusCode.BadGateway, CatalogBrowseOutcome.Unavailable)]
    [DataRow(HttpStatusCode.ServiceUnavailable, CatalogBrowseOutcome.Unavailable)]
    public async Task ErrorStatusesMapToOutcomesWithoutExposingBodies(HttpStatusCode status, CatalogBrowseOutcome expected)
    {
        var (client, _) = ClientFor(_ => new HttpResponseMessage(status) { Content = new StringContent("secret exception text") });
        var result = await client.GetObservedAuthorAsync(Context, Subject);
        Assert.AreEqual(expected, result.Outcome);
        Assert.IsNull(result.Value);
        Assert.IsFalse(result.IsOk);
    }

    [TestMethod]
    public async Task SuccessDeserializesAndABrokenBodyOrTransportFaultIsUnavailable()
    {
        var page = new CatalogSeriesBrowseResponse(Subject, "Saga", 1, false, [], ["known-local-only"]);
        var (ok, _) = ClientFor(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(page) });
        var good = await ok.GetSeriesAsync(Subject);
        Assert.IsTrue(good.IsOk);
        Assert.AreEqual("Saga", good.Value!.Name);

        var (broken, _) = ClientFor(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{not json", System.Text.Encoding.UTF8, "application/json") });
        Assert.AreEqual(CatalogBrowseOutcome.Unavailable, (await broken.GetSeriesAsync(Subject)).Outcome);

        var (down, _) = ClientFor(_ => throw new HttpRequestException("connection refused"));
        Assert.AreEqual(CatalogBrowseOutcome.Unavailable, (await down.GetSeriesAsync(Subject)).Outcome);
    }

    [TestMethod]
    public async Task CallerCancellationStillPropagatesRatherThanLookingLikeAnOutage()
    {
        var (client, _) = ClientFor(_ => throw new OperationCanceledException());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => client.GetSeriesAsync(Subject, 1, cancelled.Token));
    }

    // ---- Search state across authentication changes -------------------------------------------------------------

    private sealed class TestAuthenticationStateProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));

        public void Change() => NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }

    [TestMethod]
    public void AuthenticationChangeDropsRememberedResultsAndTheirNavigationReferences()
    {
        var authentication = new TestAuthenticationStateProvider();
        using var state = new CatalogSearchState(authentication);
        state.SearchText = "red october";
        state.HasSearched = true;
        state.CurrentPage = 3;
        state.HasMore = true;
        state.CompletedRunId = Guid.NewGuid();
        state.Results = [Candidate(navigation: Navigation(refs: [(0, "Ann Author", false)]))];
        state.AvailabilityByResultKey["demo:book-1"] = new CandidateAvailabilityRunResponse(true, []);

        authentication.Change();

        Assert.AreEqual(string.Empty, state.SearchText);
        Assert.IsEmpty(state.Results);
        Assert.IsFalse(state.HasSearched);
        Assert.AreEqual(1, state.CurrentPage);
        Assert.IsFalse(state.HasMore);
        Assert.IsNull(state.CompletedRunId);
        Assert.AreEqual(0, state.AvailabilityByResultKey.Count);
    }

    [TestMethod]
    public void DisposedSearchStateNoLongerReactsToAuthenticationChanges()
    {
        var authentication = new TestAuthenticationStateProvider();
        var state = new CatalogSearchState(authentication) { SearchText = "kept" };
        state.Dispose();
        authentication.Change();
        Assert.AreEqual("kept", state.SearchText);
    }
}
