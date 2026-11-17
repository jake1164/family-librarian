using System.Net;
using System.Text;
using System.Text.Json;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Catalog.Discovery;
using FamilyLibrarian.Domain.Catalog;
using FamilyLibrarian.Infrastructure.Integrations;
using FamilyLibrarian.Infrastructure.Metadata;
using FamilyLibrarian.Infrastructure.Tests.Catalog;
using Microsoft.Extensions.Options;

namespace FamilyLibrarian.Infrastructure.Tests.Metadata;

/// <summary>
/// Hardcover discovery through the real handler chain (authorization, then the budget-aware rate handler) over a scripted
/// transport, so physical attempts are counted where they happen. Every payload is hand-built from Hardcover's published schema
/// pages and the lab's saved observations; none is a captured response, and the series-id filter, the contributor_role
/// relation and series.author_id remain unexercised against the live API.
/// </summary>
[TestClass]
public sealed class HardcoverDiscoveryTests
{
    private static readonly SourceRef Book5 = new("hardcover", "5", ExternalReferenceEntityType.Work);
    private static readonly SourceRef Series100 = new("hardcover", "100", ExternalReferenceEntityType.Series);
    private static readonly SourceRef Author7 = new("hardcover", "7", ExternalReferenceEntityType.Author);

    // ---- find series for a book ----

    [TestMethod]
    public async Task SameNamedSeriesStayDistinctChoicesWithTheirNativeIds()
    {
        using var rig = Rig.With(Json("""
            {"data":{"books_by_pk":{"id":5,"book_series":[
              {"position":1.0,"details":null,"compilation":false,"featured":true,"series":{"id":300,"name":"Legacy","is_completed":true,"books_count":4,"primary_books_count":3,"canonical_id":null}},
              {"position":1.0,"details":null,"compilation":false,"featured":false,"series":{"id":301,"name":"Legacy","is_completed":false,"books_count":9,"primary_books_count":null,"canonical_id":null}}
            ]}}}
            """));

        var result = await rig.Provider.FindSeriesForBookAsync(Book5, rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Ok, result.Outcome);
        Assert.HasCount(2, result.Items);
        Assert.AreEqual("300", result.Items[0].Id!.ExternalId);
        Assert.AreEqual("301", result.Items[1].Id!.ExternalId);
        Assert.AreEqual("Legacy", result.Items[0].Label);
        Assert.AreEqual(1, rig.Requests, "Series are read in one document.");
        Assert.AreEqual(1, result.Usage.Attempts);
        StringAssert.Contains(rig.Bodies[0], "\"id\":5");
    }

    [TestMethod]
    public async Task AVerifiedCanonicalTargetIsFoundWithOneExtraRequestAndBothIdsAreKept()
    {
        using var rig = Rig.With(
            Json("""{"data":{"books_by_pk":{"id":5,"book_series":[{"position":1,"series":{"id":400,"name":"Dark Tower (dup)","canonical_id":401}}]}}}"""),
            Json("""{"data":{"series":[{"id":401,"name":"The Dark Tower","canonical_id":null}]}}"""));

        var result = await rig.Provider.FindSeriesForBookAsync(Book5, rig.Op(), CancellationToken.None);

        var link = result.Items.Single().Canonical!;
        Assert.AreEqual(CanonicalLinkState.Verified, link.State);
        Assert.AreEqual("400", link.Original.ExternalId);
        Assert.AreEqual("401", link.Target!.ExternalId);
        Assert.AreEqual("The Dark Tower", link.TargetLabel);
        Assert.AreEqual("401", link.PreferredForPreview.ExternalId);
        Assert.AreEqual("400", result.Items.Single().Id!.ExternalId, "The original stays the item's own id; nothing is merged.");
        Assert.AreEqual(2, rig.Requests);
        Assert.AreEqual(2, result.Usage.Attempts);
    }

    [TestMethod]
    public async Task ACanonicalChainOrCycleIsAWarningNotARedirect()
    {
        using var chain = Rig.With(
            Json("""{"data":{"books_by_pk":{"id":5,"book_series":[{"series":{"id":400,"name":"A","canonical_id":401}}]}}}"""),
            Json("""{"data":{"series":[{"id":401,"name":"B","canonical_id":402}]}}"""));
        using var cycle = Rig.With(
            Json("""{"data":{"books_by_pk":{"id":5,"book_series":[{"series":{"id":400,"name":"A","canonical_id":401}}]}}}"""),
            Json("""{"data":{"series":[{"id":401,"name":"B","canonical_id":400}]}}"""));

        var chained = await chain.Provider.FindSeriesForBookAsync(Book5, chain.Op(), CancellationToken.None);
        var cyclic = await cycle.Provider.FindSeriesForBookAsync(Book5, cycle.Op(), CancellationToken.None);

        Assert.AreEqual("canonical-chain", chained.Items.Single().Canonical!.ReasonCode);
        Assert.AreEqual("canonical-cycle", cyclic.Items.Single().Canonical!.ReasonCode);
        Assert.AreEqual("400", chained.Items.Single().Canonical!.PreferredForPreview.ExternalId);
        CollectionAssert.Contains(chained.Notes.ToArray(), "canonical-chain");
    }

    [TestMethod]
    public async Task AFailedCanonicalLookupKeepsTheSeriesAndLeavesTheStatementUnverified()
    {
        using var rig = Rig.With(
            Json("""{"data":{"books_by_pk":{"id":5,"book_series":[{"series":{"id":400,"name":"A","canonical_id":401}}]}}}"""),
            Plain(HttpStatusCode.InternalServerError));

        var result = await rig.Provider.FindSeriesForBookAsync(Book5, rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Ok, result.Outcome);
        Assert.AreEqual(CanonicalLinkState.StatedUnverified, result.Items.Single().Canonical!.State);
        CollectionAssert.Contains(result.Notes.ToArray(), DiscoveryCodes.CanonicalTargetNotChecked);
    }

    [TestMethod]
    public async Task ASeriesWithoutAnIdIsKeptUnboundAndSaysSo()
    {
        using var rig = Rig.With(Json("""{"data":{"books_by_pk":{"id":5,"book_series":[{"series":{"id":null,"name":"Mystery Set"}}]}}}"""));

        var result = await rig.Provider.FindSeriesForBookAsync(Book5, rig.Op(), CancellationToken.None);

        Assert.IsFalse(result.Items.Single().IsBound);
        CollectionAssert.Contains(result.Notes.ToArray(), DiscoveryCodes.NoNativeSeriesId);
    }

    [TestMethod]
    public async Task ABookWithNoSeriesIsEmptyInThatScopeAndAnUnknownBookIsEmptyToo()
    {
        using var none = Rig.With(Json("""{"data":{"books_by_pk":{"id":5,"book_series":[]}}}"""));
        using var unknown = Rig.With(Json("""{"data":{"books_by_pk":null}}"""));

        var noSeries = await none.Provider.FindSeriesForBookAsync(Book5, none.Op(), CancellationToken.None);
        var missing = await unknown.Provider.FindSeriesForBookAsync(Book5, unknown.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Empty, noSeries.Outcome);
        Assert.AreEqual(DiscoveryOutcome.Empty, missing.Outcome);
        Assert.AreEqual(Book5, noSeries.Scope.Subject);
    }

    // ---- shared series-claims operation (A5a): one read, positions kept, series-only view is a pure projection ----

    private const string TwoPositionsOfOneSeriesAndAnotherSeries = """
        {"data":{"books_by_pk":{"id":5,"book_series":[
          {"position":1.0,"details":null,"compilation":false,"featured":true,"series":{"id":300,"name":"Legacy","canonical_id":null}},
          {"position":1.5,"details":"Book 1.5","compilation":false,"featured":false,"series":{"id":300,"name":"Legacy","canonical_id":null}},
          {"position":null,"details":null,"compilation":false,"featured":false,"series":{"id":null,"name":"Mystery Set"}}
        ]}}}
        """;

    [TestMethod]
    public async Task TheSharedClaimsOperationKeepsEachPositionOfASeriesFromOnePhysicalRead()
    {
        using var rig = Rig.With(Json(TwoPositionsOfOneSeriesAndAnotherSeries));
        var shared = Assert.IsInstanceOfType<IBookMetadataDiscoveryProvider>(rig.Provider);

        var result = await shared.FindSeriesClaimsForBookAsync(Book5, rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Ok, result.Outcome);
        Assert.HasCount(3, result.Items, "The same series with two numbers keeps both claims.");
        Assert.AreEqual(1m, result.Items[0].Position!.Sort);
        Assert.AreEqual("Book 1.5", result.Items[1].Position!.Label);
        Assert.AreEqual(1.5m, result.Items[1].Position!.Sort);
        Assert.AreEqual(result.Items[0].Series.Id, result.Items[1].Series.Id);
        Assert.IsFalse(result.Items[2].Series.IsBound);
        Assert.IsNull(result.Items[2].Position!.Sort, "An absent position stays unknown.");
        Assert.AreEqual(MembershipRole.Unknown, result.Items[0].Role, "featured never implies a main novel.");
        CollectionAssert.Contains(result.Notes.ToArray(), "series-position-conflict");
        Assert.AreEqual(1, rig.Requests);
        Assert.AreEqual(1, result.Usage.Attempts);
    }

    [TestMethod]
    public async Task TheSeriesOnlyViewIsTheSameSingleReadWithNoStateLostAndEachSeriesOnce()
    {
        using var claimsRig = Rig.With(Json(TwoPositionsOfOneSeriesAndAnotherSeries));
        using var seriesRig = Rig.With(Json(TwoPositionsOfOneSeriesAndAnotherSeries));

        var claims = await ((IBookMetadataDiscoveryProvider)claimsRig.Provider).FindSeriesClaimsForBookAsync(Book5, claimsRig.Op(), CancellationToken.None);
        var series = await ((IBookMetadataDiscoveryProvider)seriesRig.Provider).FindSeriesForBookAsync(Book5, seriesRig.Op(), CancellationToken.None);

        Assert.AreEqual(claimsRig.Requests, seriesRig.Requests, "The projection never adds or removes a provider read.");
        Assert.AreEqual(claims.Usage.Attempts, series.Usage.Attempts);
        Assert.AreEqual(claims.Outcome, series.Outcome);
        Assert.AreEqual(claims.Completion, series.Completion);
        Assert.AreEqual(claims.Scope, series.Scope);
        Assert.HasCount(2, series.Items);
        Assert.AreEqual("300", series.Items[0].Id!.ExternalId);
        Assert.IsFalse(series.Items[1].IsBound);
        foreach (var note in claims.Notes)
        {
            CollectionAssert.Contains(series.Notes.ToArray(), note, "Every claims note survives the projection.");
        }

        CollectionAssert.Contains(series.Notes.ToArray(), DiscoveryCodes.DuplicateDropped);
    }

    [TestMethod]
    public async Task CanonicalEvidenceAndPartialFailureSurviveBothViews()
    {
        using var canonical = Rig.With(
            Json("""{"data":{"books_by_pk":{"id":5,"book_series":[{"position":2,"series":{"id":400,"name":"Dark Tower (dup)","canonical_id":401}}]}}}"""),
            Json("""{"data":{"series":[{"id":401,"name":"The Dark Tower","canonical_id":null}]}}"""));
        using var partial = Rig.With(Json("""
            {"data":{"books_by_pk":{"id":5,"book_series":[
              {"position":1,"series":{"id":300,"name":"Legacy"}},
              {"position":2,"series":null}
            ]}},
             "errors":[{"message":"secret","path":["books_by_pk","book_series",1,"series"]}]}
            """));

        var withCanonical = await ((IBookMetadataDiscoveryProvider)canonical.Provider).FindSeriesClaimsForBookAsync(Book5, canonical.Op(), CancellationToken.None);
        var failed = await ((IBookMetadataDiscoveryProvider)partial.Provider).FindSeriesClaimsForBookAsync(Book5, partial.Op(), CancellationToken.None);

        var link = withCanonical.Items.Single().Series.Canonical!;
        Assert.AreEqual(CanonicalLinkState.Verified, link.State);
        Assert.AreEqual("401", link.PreferredForPreview.ExternalId);
        Assert.AreEqual(2m, withCanonical.Items.Single().Position!.Sort);
        Assert.AreEqual(2, canonical.Requests);
        Assert.AreEqual(DiscoveryOutcome.Partial, failed.Outcome);
        Assert.AreEqual(DiscoveryCompletion.ProviderFailedPartial, failed.Completion);
        Assert.AreEqual("300", failed.Items.Single().Series.Id!.ExternalId);
        CollectionAssert.Contains(failed.Notes.ToArray(), DiscoveryCodes.PartialFieldError);

        using var again = Rig.With(Json("""
            {"data":{"books_by_pk":{"id":5,"book_series":[{"position":1,"series":{"id":300,"name":"Legacy"}},{"position":2,"series":null}]}},
             "errors":[{"message":"secret","path":["books_by_pk","book_series",1,"series"]}]}
            """));
        var projected = await ((IBookMetadataDiscoveryProvider)again.Provider).FindSeriesForBookAsync(Book5, again.Op(), CancellationToken.None);
        Assert.AreEqual(DiscoveryOutcome.Partial, projected.Outcome, "Partial is not dropped by the projection.");
        Assert.AreEqual("300", projected.Items.Single().Id!.ExternalId);
    }

    // ---- partial and fatal GraphQL data ----

    [TestMethod]
    public async Task DataWithErrorsKeepsTheValidItemsAndNeverReadsAFailedFieldAsAbsent()
    {
        using var rig = Rig.With(Json("""
            {"data":{"series":[{"id":100,"name":"Harry Potter","canonical_id":null,"book_series":[
               {"position":1,"details":null,"compilation":false,"book":{"id":11,"title":"Philosopher's Stone","contributions":[]}},
               {"position":2,"details":null,"compilation":false,"book":null}
             ]}]},
             "errors":[{"message":"secret internal text","path":["series",0,"book_series",1,"book"],"extensions":{"code":"internal"}}]}
            """));

        var result = await rig.Provider.GetSeriesMembersAsync(new SeriesRef("Harry Potter", Series100), null, rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Partial, result.Outcome);
        Assert.AreEqual(DiscoveryCompletion.ProviderFailedPartial, result.Completion);
        Assert.AreEqual("11", result.Items.Single().Book.Source.ExternalId);
        Assert.IsNull(result.Next, "No continuation is offered past a gap.");
        CollectionAssert.Contains(result.Notes.ToArray(), DiscoveryCodes.PartialFieldError);
        Assert.IsFalse(string.Join(' ', result.Notes).Contains("secret", StringComparison.Ordinal), "Provider error text never travels.");
    }

    [TestMethod]
    public async Task ErrorsWithoutDataAreAFailureNeverAVerifiedEmptyAnswer()
    {
        using var rig = Rig.With(Json("""{"errors":[{"message":"field 'series' not found in type: 'query_root'"}]}"""));

        var result = await rig.Provider.GetSeriesMembersAsync(new SeriesRef("Harry Potter", Series100), null, rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Failed, result.Outcome);
        Assert.AreEqual(DiscoveryCompletion.NotChecked, result.Completion);
        Assert.AreNotEqual(DiscoveryOutcome.Empty, result.Outcome);
    }

    [TestMethod]
    public async Task ANullRecordWithAnErrorIsUnknownNotAbsent()
    {
        using var rig = Rig.With(Json("""{"data":{"series":[]},"errors":[{"message":"x","path":["series"]}]}"""));

        var result = await rig.Provider.GetSeriesMembersAsync(new SeriesRef("Harry Potter", Series100), null, rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Failed, result.Outcome);
    }

    [TestMethod]
    public async Task AnItemWithoutAnIdIsDroppedWithANoteAndTheRestSurvive()
    {
        using var rig = Rig.With(Json("""
            {"data":{"series":[{"id":100,"name":"Harry Potter","book_series":[
               {"position":1,"book":{"id":null,"title":"Ghost","contributions":[]}},
               {"position":2,"book":{"id":12,"title":"Chamber of Secrets","contributions":[]}}
            ]}]}}
            """));

        var result = await rig.Provider.GetSeriesMembersAsync(new SeriesRef("Harry Potter", Series100), null, rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Ok, result.Outcome);
        Assert.AreEqual("12", result.Items.Single().Book.Source.ExternalId);
        CollectionAssert.Contains(result.Notes.ToArray(), DiscoveryCodes.ItemMissingId);
    }

    // ---- series members ----

    [TestMethod]
    public async Task MembersKeepFractionalPositionsCompilationClassificationAndOrdinalSafeCredits()
    {
        using var rig = Rig.With(Json("""
            {"data":{"series":[{"id":100,"name":"The Empyrean","is_completed":false,"books_count":5,"primary_books_count":3,"canonical_id":null,"book_series":[
              {"position":3.5,"details":null,"compilation":false,"featured":true,"book":{"id":31,"title":"Threshing Day","release_date":"2026","compilation":false,"is_partial_book":false,
                "contributions":[{"contributor_role":{"slug":"author"},"author":{"id":7,"name":"Rebecca Yarros"}}]}},
              {"position":1.0,"details":"1-3","compilation":true,"featured":false,"book":{"id":32,"title":"The Empyrean Collection","release_date":"2025-03-04","compilation":true,"is_partial_book":false,
                "contributions":[{"contributor_role":{"slug":"author"},"author":{"id":71,"name":"Pat Smith"}},{"contributor_role":{"slug":"illustrator"},"author":{"id":72,"name":"Pat Smith"}}]}}
            ]}]}}
            """));

        var result = await rig.Provider.GetSeriesMembersAsync(new SeriesRef("The Empyrean", Series100), null, rig.Op(), CancellationToken.None);

        var threshing = result.Items[0];
        Assert.AreEqual("3.5", threshing.Positions.Single().Label);
        Assert.AreEqual(3.5m, threshing.Positions.Single().Sort);
        Assert.AreEqual(Series100, threshing.Series);
        Assert.AreEqual(PublicationDateState.Dated, threshing.Book.Publication.State);
        Assert.AreEqual(DatePrecision.Year, threshing.Book.Publication.Precision);
        Assert.AreEqual(DateBasis.Unknown, threshing.Book.Publication.Basis, "Hardcover's release_date meaning is unverified.");
        Assert.AreEqual(MembershipRole.Unknown, threshing.Role, "A featured flag never proves a main novel.");

        var collection = result.Items[1];
        Assert.AreEqual("1-3", collection.Positions.Single().Label, "The source's own position text is kept.");
        Assert.AreEqual("Collection", collection.Book.Version!.Kind);
        Assert.HasCount(2, collection.Book.Credits);
        Assert.AreEqual("illustrator", collection.Book.Credits[1].Role);
        Assert.AreEqual("72", collection.Book.Credits[1].AuthorId!.ExternalId);
        Assert.IsTrue(LegacyCreditProjection.From(collection.Book.Credits).Projectable, "The illustrator never lends its id to the author.");
        Assert.AreEqual("71", LegacyCreditProjection.From(collection.Book.Credits).Credits.Single().SourceAuthorId);
    }

    [TestMethod]
    public async Task APageHoldsNinetyNineRowsWithAKnownNextPageAndTheCursorIsAnOffset()
    {
        using var rig = Rig.With(Json(SeriesPage(100, startId: 1)), Json(SeriesPage(3, startId: 100)));

        var first = await rig.Provider.GetSeriesMembersAsync(new SeriesRef("Big", Series100), null, rig.Op(), CancellationToken.None);

        Assert.HasCount(99, first.Items);
        Assert.AreEqual(DiscoveryCompletion.MoreAvailable, first.Completion);
        Assert.AreEqual("o:99", first.Next!.Value);
        StringAssert.Contains(rig.Bodies[0], "\"limit\":100");
        StringAssert.Contains(rig.Bodies[0], "\"offset\":0");

        var second = await rig.Provider.GetSeriesMembersAsync(new SeriesRef("Big", Series100), first.Next, rig.Op(), CancellationToken.None);

        Assert.HasCount(3, second.Items);
        Assert.AreEqual(DiscoveryCompletion.ExhaustedInCheckedScope, second.Completion);
        Assert.IsNull(second.Next);
        StringAssert.Contains(rig.Bodies[1], "\"offset\":99");
    }

    [TestMethod]
    public async Task ARepeatedBookWithinAPageIsDroppedOnceWithANote()
    {
        using var rig = Rig.With(Json("""
            {"data":{"series":[{"id":100,"name":"X","book_series":[
              {"position":1,"book":{"id":20,"title":"One","contributions":[]}},
              {"position":1,"book":{"id":20,"title":"One","contributions":[]}}
            ]}]}}
            """));

        var result = await rig.Provider.GetSeriesMembersAsync(new SeriesRef("X", Series100), null, rig.Op(), CancellationToken.None);

        Assert.HasCount(1, result.Items);
        CollectionAssert.Contains(result.Notes.ToArray(), DiscoveryCodes.DuplicateDropped);
    }

    [TestMethod]
    public async Task ALabelOnlyOrForeignSeriesOrBadCursorMakesNoProviderCall()
    {
        using var rig = Rig.With();
        var op = rig.Op();

        var labelOnly = await rig.Provider.GetSeriesMembersAsync(new SeriesRef("Legacy", null), null, op, CancellationToken.None);
        var foreign = await rig.Provider.GetSeriesMembersAsync(
            new SeriesRef("Legacy", new SourceRef("openlibrary", "100", ExternalReferenceEntityType.Series)), null, op, CancellationToken.None);
        var badCursor = await rig.Provider.GetSeriesMembersAsync(new SeriesRef("Legacy", Series100), new DiscoveryCursor("https://evil.example/"), op, CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Failed, labelOnly.Outcome);
        CollectionAssert.Contains(labelOnly.Notes.ToArray(), DiscoveryCodes.SubjectInvalid);
        CollectionAssert.Contains(foreign.Notes.ToArray(), DiscoveryCodes.SubjectInvalid);
        CollectionAssert.Contains(badCursor.Notes.ToArray(), DiscoveryCodes.CursorInvalid);
        Assert.AreEqual(0, rig.Requests);
        Assert.AreEqual(0, op.Usage.Attempts);
    }

    [TestMethod]
    public async Task TheSixthPageIsRefusedBeforeAnyRequest()
    {
        using var rig = Rig.With(Json(SeriesPage(1, 1)), Json(SeriesPage(1, 2)), Json(SeriesPage(1, 3)), Json(SeriesPage(1, 4)), Json(SeriesPage(1, 5)));
        var op = rig.Op();

        for (var page = 0; page < 5; page++)
        {
            var step = await rig.Provider.GetSeriesMembersAsync(new SeriesRef("X", Series100), null, op, CancellationToken.None);
            Assert.AreEqual(DiscoveryOutcome.Ok, step.Outcome);
        }

        var sixth = await rig.Provider.GetSeriesMembersAsync(new SeriesRef("X", Series100), null, op, CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Failed, sixth.Outcome);
        CollectionAssert.Contains(sixth.Notes.ToArray(), DiscoveryCodes.PageCeiling);
        Assert.AreEqual(5, rig.Requests);
    }

    // ---- physical attempts, retry, rate and failure policy ----

    [TestMethod]
    public async Task ARateLimitedRequestIsRetriedOnceAndTheTransportsOwnThreeRetriesAreNotInherited()
    {
        using var rig = Rig.With(RateLimited(), Json("""{"data":{"series":[{"id":100,"name":"X","book_series":[]}]}}"""));
        var op = rig.Op();

        var result = await rig.Provider.GetSeriesMembersAsync(new SeriesRef("X", Series100), null, op, CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Empty, result.Outcome);
        Assert.AreEqual(2, rig.Requests);
        Assert.AreEqual(2, op.Usage.Attempts);
        Assert.AreEqual(1, op.Usage.Retries);

        using var twice = Rig.With(RateLimited(), RateLimited(), Json("""{"data":{"series":[]}}"""));
        var opTwice = twice.Op();
        var throttled = await twice.Provider.GetSeriesMembersAsync(new SeriesRef("X", Series100), null, opTwice, CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Throttled, throttled.Outcome);
        Assert.AreEqual(2, twice.Requests, "Handler options allow three retries; the discovery budget allows one.");
        Assert.AreEqual(2, opTwice.Usage.Attempts);
    }

    [TestMethod]
    public async Task ARetryAfterLongerThanTheTimeLeftIsReportedNotShortenedAndRetriedEarly()
    {
        using var rig = Rig.With(RateLimited(retryAfterSeconds: 120), Json("""{"data":{"series":[]}}"""));

        var result = await rig.Provider.GetSeriesMembersAsync(new SeriesRef("X", Series100), null, rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Throttled, result.Outcome);
        Assert.AreEqual(1, rig.Requests);
        CollectionAssert.Contains(result.Notes.ToArray(), DiscoveryCodes.RetryAfterTooLong);
    }

    [TestMethod]
    public async Task ACapacityRejectionIsNotRetriedAndAPlain403IsAnAuthorizationProblem()
    {
        using var capacity = Rig.With(Plain(HttpStatusCode.Forbidden, """{"error":"request_exceeds_capacity"}"""), Json("""{"data":{"series":[]}}"""));
        using var denied = Rig.With(Plain(HttpStatusCode.Forbidden, """{"error":"Forbidden"}"""), Json("""{"data":{"series":[]}}"""));
        using var unauthorized = Rig.With(Plain(HttpStatusCode.Unauthorized), Json("""{"data":{"series":[]}}"""));

        var capacityResult = await capacity.Provider.GetSeriesMembersAsync(new SeriesRef("X", Series100), null, capacity.Op(), CancellationToken.None);
        var deniedResult = await denied.Provider.GetSeriesMembersAsync(new SeriesRef("X", Series100), null, denied.Op(), CancellationToken.None);
        var unauthorizedResult = await unauthorized.Provider.GetSeriesMembersAsync(new SeriesRef("X", Series100), null, unauthorized.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Failed, capacityResult.Outcome);
        CollectionAssert.Contains(capacityResult.Notes.ToArray(), DiscoveryCodes.QueryCapacity);
        Assert.AreEqual(1, capacity.Requests);
        Assert.AreEqual(DiscoveryOutcome.Unavailable, deniedResult.Outcome);
        Assert.AreEqual(1, denied.Requests);
        Assert.AreEqual(DiscoveryOutcome.Unavailable, unauthorizedResult.Outcome);
        Assert.AreEqual(1, unauthorized.Requests);
    }

    [TestMethod]
    public async Task AMissingTokenIsUnavailableSetupWithNoRequestSent()
    {
        using var rig = Rig.With(token: null);

        var result = await rig.Provider.FindSeriesForBookAsync(Book5, rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Unavailable, result.Outcome);
        CollectionAssert.Contains(result.Notes.ToArray(), DiscoveryCodes.SetupMissing);
        Assert.AreEqual(0, rig.Requests);
    }

    [TestMethod]
    public async Task TheAttemptCeilingStopsTheCanonicalLookupBeforeDispatchAndKeepsTheFirstAnswer()
    {
        using var rig = Rig.With(
            Json("""{"data":{"books_by_pk":{"id":5,"book_series":[{"series":{"id":400,"name":"A","canonical_id":401}}]}}}"""),
            Json("""{"data":{"series":[{"id":401,"name":"B","canonical_id":null}]}}"""));
        var op = rig.Op(DiscoveryLimits.Default with { OperationMaxAttempts = 1 });

        var result = await rig.Provider.FindSeriesForBookAsync(Book5, op, CancellationToken.None);

        Assert.AreEqual(1, rig.Requests, "The second attempt was refused before it reached the transport.");
        Assert.AreEqual(DiscoveryOutcome.Ok, result.Outcome);
        Assert.AreEqual(CanonicalLinkState.StatedUnverified, result.Items.Single().Canonical!.State);
    }

    [TestMethod]
    public async Task AnAnnouncedEmptyBucketIsHonouredWithoutSpendingAnAttempt()
    {
        using var rig = Rig.With(
            Json("""{"data":{"series":[]}}""", ("RateLimit", "\"Free\";r=0;t=3600, \"daily\";r=100;t=100")),
            Json("""{"data":{"series":[]}}"""));

        var first = await rig.Provider.GetSeriesMembersAsync(new SeriesRef("X", Series100), null, rig.Op(), CancellationToken.None);
        var secondOp = rig.Op();
        var second = await rig.Provider.GetSeriesMembersAsync(new SeriesRef("X", Series100), null, secondOp, CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Empty, first.Outcome);
        Assert.AreEqual(DiscoveryOutcome.Throttled, second.Outcome);
        Assert.AreEqual(1, rig.Requests, "Other traffic's headers pace the shared credential.");
        Assert.AreEqual(0, secondOp.Usage.Attempts);
    }

    [TestMethod]
    public async Task TheProviderWorkDeadlineEndsAnInFlightRequestAsATimeout()
    {
        using var rig = Rig.WithHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return Json("""{"data":{"series":[]}}""");
        });
        var op = rig.Op(DiscoveryLimits.Default with { OperationMaxWork = TimeSpan.FromMilliseconds(150) }, TimeProvider.System);

        var result = await rig.Provider.GetSeriesMembersAsync(new SeriesRef("X", Series100), null, op, CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.TimedOut, result.Outcome);
        CollectionAssert.Contains(result.Notes.ToArray(), DiscoveryCodes.DeadlineReached);
        Assert.AreEqual(1, op.Usage.Attempts, "A refused or timed-out attempt still counts once it was dispatched.");
    }

    [TestMethod]
    public async Task CallerCancellationPropagatesAndIsNeverAnEmptyOrSuccessfulResult()
    {
        using var rig = Rig.WithHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return Json("""{"data":{"series":[]}}""");
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            rig.Provider.GetSeriesMembersAsync(new SeriesRef("X", Series100), null, rig.Op(null, TimeProvider.System), cancellation.Token));
    }

    // ---- authors ----

    [TestMethod]
    public async Task ANameAloneReturnsBothNamesakesAsChoicesNeverTheOneWithMoreWorks()
    {
        using var rig = Rig.With(
            Json("""{"data":{"search":{"ids":[900,901]}}}"""),
            Json("""
                {"data":{"authors":[
                  {"id":900,"name":"Frank Herbert","books_count":21,"contributions":[]},
                  {"id":901,"name":"Frank Herbert","books_count":141,"contributions":[]}
                ]}}
                """));

        var result = await rig.Provider.ResolveAuthorAsync(AuthorAnchor.FromNameOnly("Frank Herbert"), rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Ambiguous, result.Outcome);
        Assert.HasCount(2, result.Items);
        Assert.AreEqual(2, rig.Requests, "A search, then one id lookup.");
        StringAssert.Contains(rig.Bodies[1], "\"ids\":[900,901]");
    }

    [TestMethod]
    public async Task AnAnchorBookThatOnlyOneNamesakeHasSelectsIt()
    {
        using var rig = Rig.With(
            Json("""{"data":{"search":{"ids":[900,901]}}}"""),
            Json("""
                {"data":{"authors":[
                  {"id":900,"name":"Frank Herbert","books_count":21,"contributions":[]},
                  {"id":901,"name":"Frank Herbert","books_count":141,"contributions":[{"book":{"id":5}}]}
                ]}}
                """));
        var anchor = AuthorAnchor.FromNameAndBooks(
            "Frank Herbert", [AnchorBook.FromSource(Book5), AnchorBook.FromIsbn13("9780441013593")]);

        var result = await rig.Provider.ResolveAuthorAsync(anchor, rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Ok, result.Outcome);
        Assert.AreEqual("901", result.Items.Single().Id.ExternalId);
        StringAssert.Contains(rig.Bodies[1], "\"bookIds\":[5]");
        StringAssert.Contains(rig.Bodies[1], "\"isbns\":[\"9780441013593\"]");
    }

    [TestMethod]
    public async Task ANativeAuthorIdNeedsOneRequestAndAForeignIdNone()
    {
        using var byId = Rig.With(Json("""{"data":{"authors":[{"id":7,"name":"Rebecca Yarros","books_count":59,"contributions":[]}]}}"""));
        using var foreign = Rig.With();

        var found = await byId.Provider.ResolveAuthorAsync(AuthorAnchor.FromSourceId(Author7), byId.Op(), CancellationToken.None);
        var refused = await foreign.Provider.ResolveAuthorAsync(
            AuthorAnchor.FromSourceId(new SourceRef("openlibrary", "OL7A", ExternalReferenceEntityType.Author)), foreign.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Ok, found.Outcome);
        Assert.AreEqual(1, byId.Requests);
        Assert.AreEqual(DiscoveryOutcome.Failed, refused.Outcome);
        Assert.AreEqual(0, foreign.Requests);
    }

    [TestMethod]
    public async Task DiscoveryAppliesTheSameConservativePrecisionAsTheOrdinaryMapping()
    {
        using var rig = Rig.With(Json("""
            {"data":{"authors":[{"id":7,"name":"Rebecca Yarros","contributions":[
              {"book":{"id":51,"title":"Year Start","release_date":"1965-01-01","contributions":[],"book_series":[]}},
              {"book":{"id":52,"title":"Month Start","release_date":"1965-06-01","contributions":[],"book_series":[]}},
              {"book":{"id":53,"title":"Exact Day","release_date":"1965-06-14","contributions":[],"book_series":[]}},
              {"book":{"id":54,"title":"Partial Month","release_date":"1965-06","contributions":[],"book_series":[]}},
              {"book":{"id":55,"title":"Bad Date","release_date":"1965-02-30","contributions":[],"book_series":[]}}
            ]}]}}
            """));

        var result = await rig.Provider.GetAuthorWorksAsync(new AuthorRef("Rebecca Yarros", Author7), null, rig.Op(), CancellationToken.None);

        Assert.AreEqual(DatePrecision.Year, PublicationDatePrecisionOf(result.Items[0]));
        Assert.AreEqual(new DateOnly(1965, 1, 1), result.Items[0].Book.Publication.Date, "The stated value is kept; only the precision is bounded.");
        Assert.AreEqual(DatePrecision.Month, PublicationDatePrecisionOf(result.Items[1]));
        Assert.AreEqual(DatePrecision.Day, PublicationDatePrecisionOf(result.Items[2]));
        Assert.AreEqual(DatePrecision.Month, PublicationDatePrecisionOf(result.Items[3]));
        Assert.AreEqual(PublicationDateState.NotStated, result.Items[4].Book.Publication.State, "An invalid date is not guessed.");
        Assert.IsTrue(result.Items.Take(4).All(item => item.Book.Publication.Basis == DateBasis.Unknown));
    }

    [TestMethod]
    public async Task AuthorWorksCarrySeriesClaimsRolesAndAKnownNextPage()
    {
        using var rig = Rig.With(Json("""
            {"data":{"authors":[{"id":7,"name":"Rebecca Yarros","contributions":[
              {"book":{"id":41,"title":"Fourth Wing","release_date":"2023-05-02","compilation":false,"is_partial_book":false,
                "contributions":[{"contributor_role":{"slug":"author"},"author":{"id":7,"name":"Rebecca Yarros"}}],
                "book_series":[{"position":1.0,"details":null,"compilation":false,"series":{"id":100,"name":"The Empyrean","canonical_id":null}}]}},
              {"book":{"id":42,"title":"Standalone","release_date":null,"compilation":false,"is_partial_book":true,"contributions":[],"book_series":[]}}
            ]}]}}
            """));

        var result = await rig.Provider.GetAuthorWorksAsync(new AuthorRef("Rebecca Yarros", Author7), null, rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Ok, result.Outcome);
        var fourthWing = result.Items[0];
        Assert.AreEqual("The Empyrean", fourthWing.Series.Single().Series.Label);
        Assert.AreEqual("100", fourthWing.Series.Single().Series.Id!.ExternalId);
        Assert.AreEqual("1", fourthWing.Series.Single().Position!.Label);
        Assert.AreEqual(DatePrecision.Day, PublicationDatePrecisionOf(fourthWing));
        var standalone = result.Items[1];
        Assert.AreEqual(PublicationDateState.NotStated, standalone.Book.Publication.State, "An absent date is not a known-undated title.");
        Assert.AreEqual("Partial", standalone.Book.Version!.Kind);
        StringAssert.Contains(rig.Bodies[0], "contributor_role");
    }

    [TestMethod]
    public async Task AuthorSeriesAreFlaggedPrimaryAuthorOnlyAndCanonicalStatementsStayUnverified()
    {
        using var rig = Rig.With(Json("""
            {"data":{"series":[
              {"id":100,"name":"The Empyrean","is_completed":false,"books_count":5,"primary_books_count":3,"canonical_id":null},
              {"id":300,"name":"Legacy","canonical_id":301}
            ]}}
            """));

        var result = await rig.Provider.GetAuthorSeriesAsync(new AuthorRef("Rebecca Yarros", Author7), null, rig.Op(), CancellationToken.None);

        CollectionAssert.Contains(result.Notes.ToArray(), DiscoveryCodes.PrimaryAuthorOnly);
        Assert.AreEqual(RelationshipBasis.Native, result.Items[0].Relationship.Basis);
        Assert.AreEqual(CanonicalLinkState.StatedUnverified, result.Items[1].Series.Canonical!.State);
        Assert.AreEqual("300", result.Items[1].Series.Canonical!.PreferredForPreview.ExternalId);
        Assert.AreEqual(1, rig.Requests);
    }

    [TestMethod]
    public async Task AnAuthorWithNoSeriesIsEmptyInThePrimaryAuthorScopeOnly()
    {
        using var rig = Rig.With(Json("""{"data":{"series":[]}}"""));

        var result = await rig.Provider.GetAuthorSeriesAsync(new AuthorRef("Rebecca Yarros", Author7), null, rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Empty, result.Outcome);
        Assert.AreEqual("series-with-this-primary-author", result.Scope.Filter);
        CollectionAssert.Contains(result.Notes.ToArray(), DiscoveryCodes.PrimaryAuthorOnly);
    }

    // ---- capability, documents and the unchanged search/detail mapping ----

    [TestMethod]
    public void EveryHardcoverOperationIsDeclaredSupportedAfterTheSampledLiveRun()
    {
        using var rig = Rig.With();
        var capabilities = ((IBookMetadataDiscoveryProvider)rig.Provider).Capabilities;

        Assert.AreEqual(CapabilityState.Supported, capabilities.Get(DiscoveryOperation.FindSeriesForBook).State);
        Assert.AreEqual(CapabilityState.Supported, capabilities.Get(DiscoveryOperation.ResolveAuthor).State);
        Assert.AreEqual(CapabilityState.Supported, capabilities.Get(DiscoveryOperation.GetAuthorWorks).State);
        Assert.AreEqual(CapabilityState.Supported, capabilities.Get(DiscoveryOperation.GetSeriesMembers).State, "Observed live once (Dune, 2026-11-17).");
        Assert.AreEqual(CapabilityState.Supported, capabilities.Get(DiscoveryOperation.GetAuthorSeries).State, "Observed live once; primary author only.");
        Assert.AreEqual("supported", capabilities.Get(DiscoveryOperation.GetAuthorSeries).ReasonCode);
        Assert.IsInstanceOfType<IBookMetadataProvider>(rig.Provider);
    }

    [TestMethod]
    public async Task EveryDiscoveryDocumentHasExactlyOneTopLevelField()
    {
        using var rig = Rig.With(
            Json("""{"data":{"books_by_pk":{"id":5,"book_series":[{"series":{"id":400,"name":"A","canonical_id":401}}]}}}"""),
            Json("""{"data":{"series":[]}}"""),
            Json("""{"data":{"series":[{"id":100,"name":"X","book_series":[]}]}}"""),
            Json("""{"data":{"search":{"ids":[7]}}}"""),
            Json("""{"data":{"authors":[{"id":7,"name":"A","books_count":1,"contributions":[]}]}}"""),
            Json("""{"data":{"authors":[{"id":7,"name":"A","contributions":[]}]}}"""),
            Json("""{"data":{"series":[]}}"""));
        var op = rig.Op(DiscoveryLimits.Default with { OperationMaxAttempts = 20, JourneyMaxAttempts = 20 });

        await rig.Provider.FindSeriesForBookAsync(Book5, op, CancellationToken.None);
        await rig.Provider.GetSeriesMembersAsync(new SeriesRef("X", Series100), null, op, CancellationToken.None);
        await rig.Provider.ResolveAuthorAsync(AuthorAnchor.FromNameOnly("A"), op, CancellationToken.None);
        await rig.Provider.GetAuthorWorksAsync(new AuthorRef("A", Author7), null, op, CancellationToken.None);
        await rig.Provider.GetAuthorSeriesAsync(new AuthorRef("A", Author7), null, op, CancellationToken.None);

        Assert.AreEqual(7, rig.Bodies.Count);
        foreach (var body in rig.Bodies)
        {
            using var document = JsonDocument.Parse(body);
            var query = document.RootElement.GetProperty("query").GetString()!;
            Assert.AreEqual(1, TopLevelFieldCount(query), $"One request per top-level field; this document must reserve exactly one:\n{query}");
        }
    }

    [TestMethod]
    public async Task SearchCandidatesNowCarrySourceIdsWhenTheyCannotBeCrossAssigned()
    {
        using var rig = Rig.With(
            Json("""{"data":{"search":{"ids":[42]}}}"""),
            Json("""
                {"data":{"books":[{"id":42,"title":"The Hunt for Red October",
                  "contributions":[{"author":{"id":7,"name":"Tom Clancy"}}],
                  "book_series":[{"position":1,"featured":true,"series":{"id":100,"name":"Jack Ryan","is_completed":false}}]}]}}
                """));

        var page = await rig.Provider.SearchAsync(new BookSearchQuery("red october"), CancellationToken.None);

        var candidate = page.Candidates.Single();
        Assert.AreEqual("7", candidate.AuthorCredits.Single().SourceAuthorId);
        Assert.AreEqual("100", candidate.Series.Single().SourceSeriesId);
    }

    // R1: the unchanged Phase 2 writer matches AuthorCredits to Authors by name, so a record whose same-named credits carry
    // different native ids must not become a resolvable candidate at all. Omitting the ids is not enough: the bare name would
    // then bind to whichever saved author has it.
    [TestMethod]
    public async Task SameNamedContributionsWithDifferentIdsAreNeverOfferedAsAResolvableCandidate()
    {
        using var search = Rig.With(
            Json("""{"data":{"search":{"ids":[42]}}}"""),
            Json("""
                {"data":{"books":[{"id":42,"title":"Dune",
                  "contributions":[{"author":{"id":900,"name":"Frank Herbert"}},{"author":{"id":901,"name":"Frank Herbert"}}]}]}}
                """));
        using var details = Rig.With(Json("""
            {"data":{"books_by_pk":{"id":42,"title":"Dune",
              "contributions":[{"author":{"id":7,"name":"Alex Twin"}},{"author":{"id":8,"name":"alex  twin"}}]}}}
            """));

        var page = await search.Provider.SearchAsync(new BookSearchQuery("dune"), CancellationToken.None);
        var detail = await details.Provider.GetDetailsAsync("42", CancellationToken.None);

        Assert.HasCount(0, page.Candidates, "A name-only candidate would be credited to a saved namesake.");
        Assert.IsNull(detail, "The detail path must keep the same decision as search.");
    }

    [TestMethod]
    public async Task OneAuthorListedTwiceUnderTheSameIdStaysAnOrdinaryCandidate()
    {
        using var rig = Rig.With(Json("""
            {"data":{"books_by_pk":{"id":42,"title":"Dune",
              "contributions":[{"author":{"id":900,"name":"Frank Herbert"}},{"author":{"id":900,"name":"Frank Herbert"}},{"author":{"id":902,"name":"Brian Herbert"}}]}}}
            """));

        var candidate = await rig.Provider.GetDetailsAsync("42", CancellationToken.None);

        Assert.IsNotNull(candidate);
        Assert.AreEqual("Frank Herbert|Brian Herbert", string.Join("|", candidate.Authors));
        Assert.AreEqual("900|902", string.Join("|", candidate.AuthorCredits.Select(credit => credit.SourceAuthorId)));
    }

    // ---- R2: a field error is never read as factual absence (author resolution) ----

    private const string AuthorsFailed =
        """{"data":{"authors":null},"errors":[{"message":"internal secret text","path":["authors"],"extensions":{"code":"internal"}}]}""";

    [TestMethod]
    public async Task ANativeIdLookupWhoseAuthorsFieldFailedIsAFailureNeverAVerifiedEmptyAnswer()
    {
        using var rig = Rig.With(Json(AuthorsFailed));

        var result = await rig.Provider.ResolveAuthorAsync(AuthorAnchor.FromSourceId(Author7), rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Failed, result.Outcome);
        Assert.AreNotEqual(DiscoveryCompletion.ExhaustedInCheckedScope, result.Completion);
        CollectionAssert.Contains(result.Notes.ToArray(), DiscoveryCodes.PartialFieldError);
        Assert.IsFalse(string.Join(' ', result.Notes).Contains("secret", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ANameLookupWhoseAuthorsFieldFailedIsAFailureNotEmpty()
    {
        using var rig = Rig.With(Json("""{"data":{"search":{"ids":[900,901]}}}"""), Json(AuthorsFailed));

        var result = await rig.Provider.ResolveAuthorAsync(AuthorAnchor.FromNameOnly("Frank Herbert"), rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Failed, result.Outcome);
        Assert.AreNotEqual(DiscoveryOutcome.Empty, result.Outcome);
    }

    [TestMethod]
    public async Task AnAnchorBookLookupThatFailedForTheOtherNamesakeNeverMakesTheFirstOneUnique()
    {
        // 900 is corroborated by the anchor book; 901's corroboration field failed, so it is unknown, not "not corroborated".
        using var rig = Rig.With(
            Json("""{"data":{"search":{"ids":[900,901]}}}"""),
            Json("""
                {"data":{"authors":[
                   {"id":900,"name":"Frank Herbert","books_count":21,"contributions":[{"book":{"id":5}}]},
                   {"id":901,"name":"Frank Herbert","books_count":141,"contributions":null}
                 ]},
                 "errors":[{"message":"x","path":["authors",1,"contributions"],"extensions":{"code":"internal"}}]}
                """));
        var anchor = AuthorAnchor.FromNameAndBooks("Frank Herbert", [AnchorBook.FromSource(Book5)]);

        var result = await rig.Provider.ResolveAuthorAsync(anchor, rig.Op(), CancellationToken.None);

        Assert.AreNotEqual(DiscoveryOutcome.Ok, result.Outcome, "Identity is never concluded while another candidate's evidence is unknown.");
        Assert.AreNotEqual(DiscoveryOutcome.Empty, result.Outcome);
        Assert.AreEqual("900|901", string.Join("|", result.Items.Select(author => author.Id.ExternalId).Order(StringComparer.Ordinal)), "Both valid candidates are kept as choices.");
        CollectionAssert.Contains(result.Notes.ToArray(), DiscoveryCodes.PartialFieldError);
    }

    [TestMethod]
    public async Task AFailedSearchFieldAmongFoundIdsKeepsTheChoicesButNeverSelectsOne()
    {
        using var rig = Rig.With(
            Json("""{"data":{"search":{"ids":[900]}},"errors":[{"message":"x","path":["search","ids",1],"extensions":{"code":"internal"}}]}"""),
            Json("""{"data":{"authors":[{"id":900,"name":"Frank Herbert","books_count":21,"contributions":[{"book":{"id":5}}]}]}}"""));
        var anchor = AuthorAnchor.FromNameAndBooks("Frank Herbert", [AnchorBook.FromSource(Book5)]);

        var result = await rig.Provider.ResolveAuthorAsync(anchor, rig.Op(), CancellationToken.None);

        Assert.AreNotEqual(DiscoveryOutcome.Ok, result.Outcome, "The ranked id list may be incomplete, so one corroborated hit is not proof of uniqueness.");
        Assert.AreEqual("900", result.Items.Single().Id.ExternalId);
    }

    [TestMethod]
    public async Task ASourceIdLookupIsUnaffectedByAnUnrelatedFailedFieldOnTheSameRecord()
    {
        using var rig = Rig.With(Json("""
            {"data":{"authors":[{"id":7,"name":"Rebecca Yarros","books_count":null,"contributions":[]}]},
             "errors":[{"message":"x","path":["authors",0,"books_count"],"extensions":{"code":"internal"}}]}
            """));

        var result = await rig.Provider.ResolveAuthorAsync(AuthorAnchor.FromSourceId(Author7), rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Ok, result.Outcome, "The native id selected the record; a failed count does not undo that.");
        Assert.AreEqual("7", result.Items.Single().Id.ExternalId);
        Assert.IsNull(result.Items.Single().WorkCount, "A failed count is unknown, not zero.");
        CollectionAssert.Contains(result.Notes.ToArray(), DiscoveryCodes.PartialFieldError);
    }

    [TestMethod]
    public async Task AFailedIdentityFieldOnTheNativeRecordIsAFailureNotEmpty()
    {
        using var rig = Rig.With(Json("""
            {"data":{"authors":[{"id":7,"name":null,"books_count":3,"contributions":[]}]},
             "errors":[{"message":"x","path":["authors",0,"name"],"extensions":{"code":"internal"}}]}
            """));

        var result = await rig.Provider.ResolveAuthorAsync(AuthorAnchor.FromSourceId(Author7), rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Failed, result.Outcome);
    }

    // ---- R2: failed role, date or series fields never become absent facts on a kept item ----

    [TestMethod]
    public async Task ASeriesMemberWhoseRoleFieldFailedIsNotKeptWithAnAuthorRoleOrAnEmptyCreditList()
    {
        using var rig = Rig.With(Json("""
            {"data":{"series":[{"id":100,"name":"X","book_series":[
               {"position":1,"book":{"id":11,"title":"Fine","contributions":[{"contributor_role":{"slug":"author"},"author":{"id":7,"name":"Alex Twin"}}]}},
               {"position":2,"book":{"id":12,"title":"Role unknown","contributions":[{"contributor_role":null,"author":{"id":8,"name":"Alex Twin"}}]}}
             ]}]},
             "errors":[{"message":"x","path":["series",0,"book_series",1,"book","contributions",0,"contributor_role"],"extensions":{"code":"internal"}}]}
            """));

        var result = await rig.Provider.GetSeriesMembersAsync(new SeriesRef("X", Series100), null, rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Partial, result.Outcome);
        Assert.AreEqual("11", result.Items.Single().Book.Source.ExternalId, "A null role caused by an error would otherwise read as an author credit.");
        CollectionAssert.Contains(result.Notes.ToArray(), DiscoveryCodes.PartialFieldError);
    }

    [TestMethod]
    public async Task AnAuthorWorkWhoseSeriesFieldFailedIsNotKeptAsHavingNoSeries()
    {
        using var rig = Rig.With(Json("""
            {"data":{"authors":[{"id":7,"name":"Rebecca Yarros","contributions":[
               {"book":{"id":41,"title":"Fourth Wing","contributions":[],"book_series":[{"position":1,"details":null,"series":{"id":100,"name":"The Empyrean"}}]}},
               {"book":{"id":42,"title":"Iron Flame","contributions":[],"book_series":null}}
             ]}]},
             "errors":[{"message":"x","path":["authors",0,"contributions",1,"book","book_series"],"extensions":{"code":"internal"}}]}
            """));

        var result = await rig.Provider.GetAuthorWorksAsync(new AuthorRef("Rebecca Yarros", Author7), null, rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Partial, result.Outcome);
        Assert.AreEqual("41", result.Items.Single().Book.Source.ExternalId);
    }

    // ---- R3: a page with nothing usable is a typed state, never an exception or a verified-empty scope ----

    private static string NullBookPage(int rows)
    {
        var builder = new StringBuilder("""{"data":{"series":[{"id":100,"name":"Big","canonical_id":null,"book_series":[""");
        for (var row = 0; row < rows; row++)
        {
            builder.Append(row > 0 ? "," : string.Empty).Append("""{"position":1,"details":null,"compilation":false,"featured":false,"book":null}""");
        }

        return builder.Append("]}]}}").ToString();
    }

    private static string UnusableWorksPage(int rows)
    {
        var builder = new StringBuilder("""{"data":{"authors":[{"id":7,"name":"Rebecca Yarros","contributions":[""");
        for (var row = 0; row < rows; row++)
        {
            builder.Append(row > 0 ? "," : string.Empty).Append("""{"book":{"id":null,"title":"Ghost","contributions":[],"book_series":[]}}""");
        }

        return builder.Append("]}]}}").ToString();
    }

    private static string NamelessSeriesPage(int rows)
    {
        var builder = new StringBuilder("""{"data":{"series":[""");
        for (var row = 0; row < rows; row++)
        {
            builder.Append(row > 0 ? "," : string.Empty).Append("""{"id":null,"name":null,"canonical_id":null}""");
        }

        return builder.Append("]}}").ToString();
    }

    private static void AssertNoUsableButNotEmpty<T>(DiscoveryResult<T> result)
    {
        Assert.AreNotEqual(DiscoveryOutcome.Empty, result.Outcome, "Unprocessed source rows are not an empty scope.");
        Assert.AreNotEqual(DiscoveryOutcome.Failed, result.Outcome, "The provider answered; this is not a failure.");
        Assert.AreEqual(DiscoveryOutcome.NoUsableItems, result.Outcome);
        Assert.AreEqual(0, result.Items.Count);
        CollectionAssert.Contains(result.Notes.ToArray(), DiscoveryCodes.ItemMissingId);
    }

    [TestMethod]
    public async Task AFullSeriesPageOfUnusableRowsDoesNotThrowAndKeepsTheNextOffset()
    {
        using var rig = Rig.With(Json(NullBookPage(100)));
        var op = rig.Op();

        var result = await rig.Provider.GetSeriesMembersAsync(new SeriesRef("Big", Series100), null, op, CancellationToken.None);

        AssertNoUsableButNotEmpty(result);
        Assert.AreEqual(DiscoveryCompletion.MoreAvailable, result.Completion);
        Assert.AreEqual("o:99", result.Next!.Value, "Offsets keep their meaning: nothing is reset or skipped.");
        Assert.AreEqual(1, rig.Requests, "No unbounded drain: the caller decides whether to continue.");
        Assert.AreEqual(1, op.Usage.Pages);
    }

    [TestMethod]
    public async Task ALastSeriesPageOfOnlyUnusableRowsIsNotCertifiedEmpty()
    {
        using var rig = Rig.With(Json(NullBookPage(3)));

        var result = await rig.Provider.GetSeriesMembersAsync(new SeriesRef("Big", Series100), null, rig.Op(), CancellationToken.None);

        AssertNoUsableButNotEmpty(result);
        Assert.IsNull(result.Next);
    }

    [TestMethod]
    public async Task AFullAuthorWorksPageAndAFullAuthorSeriesPageOfUnusableRowsAreHandledToo()
    {
        using var works = Rig.With(Json(UnusableWorksPage(100)));
        using var series = Rig.With(Json(NamelessSeriesPage(100)));

        var worksResult = await works.Provider.GetAuthorWorksAsync(new AuthorRef("Rebecca Yarros", Author7), null, works.Op(), CancellationToken.None);
        var seriesResult = await series.Provider.GetAuthorSeriesAsync(new AuthorRef("Rebecca Yarros", Author7), null, series.Op(), CancellationToken.None);

        AssertNoUsableButNotEmpty(worksResult);
        AssertNoUsableButNotEmpty(seriesResult);
        Assert.AreEqual("o:99", worksResult.Next!.Value);
        Assert.AreEqual("o:99", seriesResult.Next!.Value);
    }

    [TestMethod]
    public async Task AMixedPageKeepsTheUsableRowAndTheKnownNextPage()
    {
        var builder = new StringBuilder("""{"data":{"series":[{"id":100,"name":"Big","book_series":[""");
        builder.Append("""{"position":1,"book":{"id":11,"title":"Only usable","contributions":[]}}""");
        for (var row = 0; row < 99; row++)
        {
            builder.Append(""",{"position":2,"book":null}""");
        }

        using var rig = Rig.With(Json(builder.Append("]}]}}").ToString()));

        var result = await rig.Provider.GetSeriesMembersAsync(new SeriesRef("Big", Series100), null, rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Ok, result.Outcome);
        Assert.AreEqual("11", result.Items.Single().Book.Source.ExternalId);
        Assert.AreEqual(DiscoveryCompletion.MoreAvailable, result.Completion);
        Assert.AreEqual("o:99", result.Next!.Value);
    }

    [TestMethod]
    public async Task AnUnusablePageUnderFieldErrorsIsAFailureAndNeverThrowsOrOffersAContinuation()
    {
        using var rig = Rig.With(Json("""
            {"data":{"series":[{"id":100,"name":"Big","book_series":[{"position":1,"book":null}]}]},
             "errors":[{"message":"x","path":["series",0,"book_series",0,"book"],"extensions":{"code":"internal"}}]}
            """));

        var result = await rig.Provider.GetSeriesMembersAsync(new SeriesRef("Big", Series100), null, rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Failed, result.Outcome);
        Assert.IsNull(result.Next);
    }

    [TestMethod]
    public async Task ABudgetStopAfterAnUnusablePageStaysALimitOutcomeNotAbsence()
    {
        using var rig = Rig.With(Json(NullBookPage(100)), Json(NullBookPage(100)));
        var op = rig.Op(DiscoveryLimits.Default with { OperationMaxPages = 1 });

        var first = await rig.Provider.GetSeriesMembersAsync(new SeriesRef("Big", Series100), null, op, CancellationToken.None);
        var second = await rig.Provider.GetSeriesMembersAsync(new SeriesRef("Big", Series100), first.Next, op, CancellationToken.None);

        AssertNoUsableButNotEmpty(first);
        Assert.AreNotEqual(DiscoveryOutcome.Empty, second.Outcome);
        CollectionAssert.Contains(second.Notes.ToArray(), DiscoveryCodes.PageCeiling);
        Assert.AreEqual(1, rig.Requests, "The ceiling refused the second page before dispatch.");
    }

    // ---- R4: shared credential pacing covers ordinary traffic too ----

    [TestMethod]
    public async Task AnAnnouncedEmptyBucketStopsOrdinaryRequestsBeforeTheyReachTheTransport()
    {
        using var rig = Rig.With(Json("""{"data":{"books_by_pk":null}}"""));
        rig.State.Observe(WithRateHeader("\"Free\";r=0;t=60"));

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => rig.Provider.GetDetailsAsync("42", CancellationToken.None));

        Assert.AreEqual(HttpStatusCode.TooManyRequests, failure.StatusCode, "The caller sees an ordinary rate-limit failure, not a silent success.");
        Assert.AreEqual(0, rig.Requests, "Ordinary traffic must honour a wait Hardcover already announced.");
    }

    [TestMethod]
    public async Task AnOrdinaryRequestWaitsOutAShortAnnouncedWaitAndThenSends()
    {
        using var rig = Rig.WithRealClockState(Json("""{"data":{"books_by_pk":null}}"""));
        rig.State.Observe(WithRateHeader("\"Free\";r=0;t=1"));
        var started = System.Diagnostics.Stopwatch.StartNew();

        var candidate = await rig.Provider.GetDetailsAsync("42", CancellationToken.None);

        Assert.IsNull(candidate);
        Assert.AreEqual(1, rig.Requests);
        Assert.IsTrue(started.Elapsed >= TimeSpan.FromMilliseconds(700), "The announced wait is not shortened.");
    }

    // The failure seen once in a full run: the pause for an announced "r=0;t=1" returned a few milliseconds before the rate state's wall-clock
    // reset instant (a timer may fire early against the system clock), the single re-check still found the window in force, and the request
    // was refused as a synthetic 429 although the announced wait had been honoured. These cases make that timing exact with a manual clock.

    [TestMethod]
    public async Task APauseThatReturnsAFewMillisecondsBeforeTheResetFinishesTheSliverAndThenSends()
    {
        var pauses = new List<TimeSpan>();
        var clockAtRequest = DateTimeOffset.MinValue;
        using var rig = Rig.WithPause((rig, wait, _) =>
        {
            pauses.Add(wait);
            // First pause: the timer fires 5 ms early; second pause: the remaining 5 ms.
            rig.Clock.Advance(pauses.Count == 1 ? wait - TimeSpan.FromMilliseconds(5) : wait);
            return Task.CompletedTask;
        }, Json("""{"data":{"books_by_pk":null}}"""));
        rig.State.Observe(WithRateHeader("\"Free\";r=0;t=1"));
        var reset = rig.Clock.GetUtcNow() + TimeSpan.FromSeconds(1);

        var candidate = await rig.Provider.GetDetailsAsync("42", CancellationToken.None);
        clockAtRequest = rig.Clock.GetUtcNow();

        Assert.IsNull(candidate);
        Assert.AreEqual(1, rig.Requests, "The request is sent once, not refused as a synthetic 429.");
        Assert.HasCount(2, pauses);
        Assert.AreEqual(TimeSpan.FromSeconds(1), pauses[0]);
        Assert.AreEqual(TimeSpan.FromMilliseconds(5), pauses[1], "Only the sliver the timer left is waited.");
        Assert.IsGreaterThanOrEqualTo(reset, clockAtRequest, "The announced wait is never shortened: the request goes at or after the reset instant.");
    }

    [TestMethod]
    [DataRow(1_000, false)]
    [DataRow(5_000, false)]
    [DataRow(9_000, false)]
    [DataRow(10_000, false)]
    [DataRow(1_000, true)]
    [DataRow(5_000, true)]
    [DataRow(9_000, true)]
    [DataRow(10_000, true)]
    public async Task ASubMillisecondRemainderIsWaitedWithoutSendingEarlyOrReturningASynthetic429(int remainderTicks, bool discovery)
    {
        var pauses = new List<TimeSpan>();
        var remainder = TimeSpan.FromTicks(remainderTicks);
        using var rig = Rig.WithPause((rig, wait, _) =>
        {
            pauses.Add(wait);
            // A timer can return just before the wall-clock reset. Task.Delay truncates subsequent waits to whole milliseconds.
            rig.Clock.Advance(pauses.Count == 1 ? wait - remainder : TimeSpan.FromMilliseconds((long)wait.TotalMilliseconds));
            return Task.CompletedTask;
        }, Json("""{"data":{"books_by_pk":null}}"""));
        rig.State.Observe(WithRateHeader("\"Free\";r=0;t=1"));
        var reset = rig.Clock.GetUtcNow() + TimeSpan.FromSeconds(1);

        if (discovery)
        {
            var result = await rig.Provider.FindSeriesClaimsForBookAsync(Book5, rig.Op(), CancellationToken.None);
            Assert.AreEqual(DiscoveryOutcome.Empty, result.Outcome);
            Assert.AreEqual(1, result.Usage.Attempts);
        }
        else
        {
            Assert.IsNull(await rig.Provider.GetDetailsAsync("42", CancellationToken.None));
        }

        Assert.AreEqual(1, rig.Requests, "Only the actual admitted request reaches the transport.");
        Assert.HasCount(2, pauses);
        Assert.AreEqual(TimeSpan.FromMilliseconds(1), pauses[1], "A positive fraction must not become a zero-duration delay.");
        Assert.IsGreaterThanOrEqualTo(reset, rig.Clock.GetUtcNow(), "The reset must pass before dispatch.");
    }

    [TestMethod]
    public async Task ARoundedFinishingPauseThatDoesNotFitTheDiscoveryBudgetIsRefusedWithoutAnotherPauseOrRequest()
    {
        var pauses = 0;
        using var rig = Rig.WithPause((rig, wait, _) =>
        {
            pauses++;
            rig.Clock.Advance(wait - TimeSpan.FromTicks(5_000));
            return Task.CompletedTask;
        }, Json("""{"data":{"books_by_pk":null}}"""));
        rig.State.Observe(WithRateHeader("\"Free\";r=0;t=1"));
        var budget = rig.Op(new DiscoveryLimits { OperationMaxWork = TimeSpan.FromSeconds(1) + TimeSpan.FromTicks(2_000) });

        var result = await rig.Provider.FindSeriesClaimsForBookAsync(Book5, budget, CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Throttled, result.Outcome);
        CollectionAssert.Contains(result.Notes.ToArray(), DiscoveryCodes.RetryAfterTooLong);
        Assert.AreEqual(1, pauses, "The 0.5 ms remainder fits, but its 1 ms scheduled delay exceeds the remaining 0.7 ms budget.");
        Assert.AreEqual(0, rig.Requests);
        Assert.AreEqual(0, result.Usage.Attempts);
    }

    [TestMethod]
    public async Task ASubMillisecondFinishingPauseStillPropagatesCancellationWithoutSending()
    {
        var pauses = 0;
        using var cancellation = new CancellationTokenSource();
        using var rig = Rig.WithPause((rig, wait, token) =>
        {
            if (++pauses == 1)
            {
                rig.Clock.Advance(wait - TimeSpan.FromTicks(5_000));
                return Task.CompletedTask;
            }

            cancellation.Cancel();
            return Task.FromCanceled(token);
        }, Json("""{"data":{"books_by_pk":null}}"""));
        rig.State.Observe(WithRateHeader("\"Free\";r=0;t=1"));

        await Assert.ThrowsAsync<OperationCanceledException>(() => rig.Provider.FindSeriesClaimsForBookAsync(Book5, rig.Op(), cancellation.Token));

        Assert.AreEqual(2, pauses);
        Assert.AreEqual(0, rig.Requests);
    }

    [TestMethod]
    public async Task APauseThatEndsInANewWaitIsNotLoopedOnAndTheRequestIsRefusedWithoutBeingSent()
    {
        var pauses = 0;
        using var rig = Rig.WithPause((rig, wait, _) =>
        {
            pauses++;
            rig.Clock.Advance(wait);
            // Another caller used the freed slot and Hardcover announced a fresh five-second wait.
            rig.State.Observe(WithRateHeader("\"Free\";r=0;t=5"));
            return Task.CompletedTask;
        }, Json("""{"data":{"books_by_pk":null}}"""));
        rig.State.Observe(WithRateHeader("\"Free\";r=0;t=1"));

        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => rig.Provider.GetDetailsAsync("42", CancellationToken.None));

        Assert.AreEqual(1, pauses, "A genuinely new wait is not chased: one announced wait is paused for, once.");
        Assert.AreEqual(0, rig.Requests, "A refused request is never sent.");
    }

    [TestMethod]
    public async Task CancellationDuringTheFinishingPassStillPropagatesAndSendsNothing()
    {
        var pauses = 0;
        using var rig = Rig.WithPause((rig, wait, token) =>
        {
            pauses++;
            if (pauses == 1)
            {
                rig.Clock.Advance(wait - TimeSpan.FromMilliseconds(5));
                return Task.CompletedTask;
            }

            return Task.FromCanceled(new CancellationToken(canceled: true));
        }, Json("""{"data":{"books_by_pk":null}}"""));
        rig.State.Observe(WithRateHeader("\"Free\";r=0;t=1"));

        await Assert.ThrowsAsync<OperationCanceledException>(() => rig.Provider.GetDetailsAsync("42", CancellationToken.None));

        Assert.AreEqual(0, rig.Requests);
    }

    [TestMethod]
    public async Task AnAnnouncedWaitLongerThanTheRetryCapIsReportedWithoutAnyPauseOrRequest()
    {
        var pauses = 0;
        using var rig = Rig.WithPause((_, _, _) =>
        {
            pauses++;
            return Task.CompletedTask;
        }, Json("""{"data":{"books_by_pk":null}}"""));
        rig.State.Observe(WithRateHeader("\"Free\";r=0;t=3600"));

        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => rig.Provider.GetDetailsAsync("42", CancellationToken.None));

        Assert.AreEqual(0, pauses);
        Assert.AreEqual(0, rig.Requests);
    }

    [TestMethod]
    public async Task AnOrdinaryRequestsPauseHonoursCallerCancellation()
    {
        using var rig = Rig.WithRealClockState(Json("""{"data":{"books_by_pk":null}}"""));
        rig.State.Observe(WithRateHeader("\"Free\";r=0;t=20"));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAsync<OperationCanceledException>(() => rig.Provider.GetDetailsAsync("42", cancellation.Token));

        Assert.AreEqual(0, rig.Requests);
    }

    [TestMethod]
    public async Task TheLastAnnouncedSlotGoesToExactlyOneOfTwoConcurrentRequests()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var rig = Rig.WithHandler(async (_, _) =>
        {
            await release.Task;
            return Json("""{"data":{"books_by_pk":null}}""");
        });
        rig.State.Observe(WithRateHeader("\"Free\";r=1;t=60"));

        var first = rig.Provider.GetDetailsAsync("42", CancellationToken.None);
        while (rig.Requests < 1)
        {
            await Task.Delay(5);
        }

        HttpRequestException second;
        try
        {
            // Unfixed code would send this request too and wait for the gate; the timeout turns that hang into a failure.
            second = await Assert.ThrowsAsync<HttpRequestException>(
                () => rig.Provider.GetDetailsAsync("43", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            release.TrySetResult();
        }

        await first;

        Assert.AreEqual(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.AreEqual(1, rig.Requests, "A positive allowance is reserved before sending, not just observed afterwards.");
    }

    [TestMethod]
    public async Task OrdinaryTrafficHoldingTheLastSlotMakesDiscoveryThrottleWithoutSpendingAnAttempt()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var rig = Rig.WithHandler(async (_, _) =>
        {
            await release.Task;
            return Json("""{"data":{"books_by_pk":null}}""");
        });
        rig.State.Observe(WithRateHeader("\"Free\";r=1;t=60"));
        var ordinary = rig.Provider.GetDetailsAsync("42", CancellationToken.None);
        while (rig.Requests < 1)
        {
            await Task.Delay(5);
        }

        var op = rig.Op();
        DiscoveryResult<SeriesMember> discovery;
        try
        {
            discovery = await rig.Provider.GetSeriesMembersAsync(new SeriesRef("X", Series100), null, op, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.TrySetResult();
        }

        await ordinary;

        Assert.AreEqual(DiscoveryOutcome.Throttled, discovery.Outcome);
        Assert.AreEqual(0, op.Usage.Attempts);
        Assert.AreEqual(1, rig.Requests);
    }

    [TestMethod]
    public async Task AStaleEmptyAnnouncementDoesNotBlockAfterItsResetPassed()
    {
        using var rig = Rig.With(Json("""{"data":{"books_by_pk":null}}"""));
        rig.State.Observe(WithRateHeader("\"Free\";r=0;t=60"));
        rig.Clock.Advance(TimeSpan.FromSeconds(61));

        var candidate = await rig.Provider.GetDetailsAsync("42", CancellationToken.None);

        Assert.IsNull(candidate);
        Assert.AreEqual(1, rig.Requests);
    }

    [TestMethod]
    public async Task ADiscoveryRequestFeedsAndConsultsTheSameStateAsOrdinaryTraffic()
    {
        using var rig = Rig.With(
            Json("""{"data":{"series":[]}}""", ("RateLimit", "\"Free\";r=0;t=3600")),
            Json("""{"data":{"books_by_pk":null}}"""));

        await rig.Provider.GetSeriesMembersAsync(new SeriesRef("X", Series100), null, rig.Op(), CancellationToken.None);
        await Assert.ThrowsAsync<HttpRequestException>(() => rig.Provider.GetDetailsAsync("42", CancellationToken.None));

        Assert.AreEqual(1, rig.Requests, "What discovery learned paces the ordinary path.");
    }

    // ---- R4a: a dispatched attempt is charged; only a definitely unsent reservation is returned ----

    [TestMethod]
    public async Task ASentRequestWithNoRateLimitHeaderStillSpendsTheLastAnnouncedSlot()
    {
        using var rig = Rig.With(Json("""{"data":{"books_by_pk":null}}"""), Json("""{"data":{"books_by_pk":null}}"""));
        rig.State.Observe(WithRateHeader("\"Free\";r=1;t=600"));

        await rig.Provider.GetDetailsAsync("42", CancellationToken.None);
        var blocked = await Assert.ThrowsAsync<HttpRequestException>(() => rig.Provider.GetDetailsAsync("43", CancellationToken.None));

        Assert.AreEqual(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.AreEqual(1, rig.Requests, "Completing a sent request must not grant back the allowance it spent.");
    }

    [TestMethod]
    public async Task ASentRequestWithAMalformedRateLimitHeaderStillSpendsTheLastAnnouncedSlot()
    {
        using var rig = Rig.With(
            Json("""{"data":{"books_by_pk":null}}""", ("RateLimit", "not-a-quota")),
            Json("""{"data":{"books_by_pk":null}}"""));
        rig.State.Observe(WithRateHeader("\"Free\";r=1;t=600"));

        await rig.Provider.GetDetailsAsync("42", CancellationToken.None);
        await Assert.ThrowsAsync<HttpRequestException>(() => rig.Provider.GetDetailsAsync("43", CancellationToken.None));

        Assert.AreEqual(1, rig.Requests);
    }

    [TestMethod]
    public async Task ADiscoveryRequestWithNoRateLimitHeaderSpendsTheSlotForOrdinaryTrafficToo()
    {
        using var rig = Rig.With(Json("""{"data":{"series":[]}}"""), Json("""{"data":{"books_by_pk":null}}"""));
        rig.State.Observe(WithRateHeader("\"Free\";r=1;t=600"));

        await rig.Provider.GetSeriesMembersAsync(new SeriesRef("X", Series100), null, rig.Op(), CancellationToken.None);
        await Assert.ThrowsAsync<HttpRequestException>(() => rig.Provider.GetDetailsAsync("42", CancellationToken.None));

        Assert.AreEqual(1, rig.Requests);
    }

    [TestMethod]
    public async Task ATransportFailureAfterDispatchIsChargedBecauseTheServerMayHaveCountedIt()
    {
        using var rig = Rig.WithHandler((_, _) => throw new HttpRequestException("transport down"));
        rig.State.Observe(WithRateHeader("\"Free\";r=1;t=60"));

        await Assert.ThrowsAsync<HttpRequestException>(() => rig.Provider.GetDetailsAsync("42", CancellationToken.None));
        var blocked = await Assert.ThrowsAsync<HttpRequestException>(() => rig.Provider.GetDetailsAsync("43", CancellationToken.None));

        Assert.AreEqual(HttpStatusCode.TooManyRequests, blocked.StatusCode, "The second failure is the synthetic rate-limit refusal.");
        Assert.AreEqual(1, rig.Requests, "A lost attempt does not prove the server did no work.");
    }

    [TestMethod]
    public async Task ACancellationAfterDispatchIsChargedToo()
    {
        using var chain = new DirectChain(new HardcoverRateState(new ManualClock()), async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        chain.State.Observe(WithRateHeader("\"Free\";r=1;t=600"));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAsync<OperationCanceledException>(() => chain.SendAsync(cancellation.Token));
        // Unfixed code would send this request into the transport that never answers; the guard turns that hang into a failure.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var next = await chain.SendAsync(guard.Token);

        Assert.AreEqual(HttpStatusCode.TooManyRequests, next.StatusCode);
        Assert.AreEqual(1, chain.Requests);
    }

    [TestMethod]
    public async Task ACancellationBeforeDispatchReturnsTheSlotAndSendsNothing()
    {
        using var chain = new DirectChain(new HardcoverRateState(new ManualClock()), (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        chain.State.Observe(WithRateHeader("\"Free\";r=1;t=600"));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => chain.SendAsync(cancelled.Token));
        Assert.AreEqual(0, chain.Requests, "A request whose token was already cancelled is never sent.");

        using var next = await chain.SendAsync(CancellationToken.None);
        Assert.AreEqual(HttpStatusCode.OK, next.StatusCode, "The unsent reservation was returned, not leaked or charged.");
        Assert.AreEqual(1, chain.Requests);
    }

    [TestMethod]
    public async Task ARetryWhoseCopyCannotBeBuiltReturnsItsReservation()
    {
        using var chain = new DirectChain(
            new HardcoverRateState(new ManualClock()),
            (_, _) =>
            {
                var response = RateLimited();
                response.Headers.TryAddWithoutValidation(HardcoverRateState.HeaderName, "\"Free\";r=2;t=600");
                return Task.FromResult(response);
            },
            () => new ThrowingContent());
        chain.State.Observe(WithRateHeader("\"Free\";r=3;t=600"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => chain.SendAsync(CancellationToken.None));

        Assert.AreEqual(1, chain.Requests);
        Assert.IsNotNull(chain.State.TryReserve().Reservation);
        Assert.IsNotNull(chain.State.TryReserve().Reservation, "The reservation taken for the retry that was never sent must not leak.");
    }

    // ---- R4b: delayed responses cannot reopen an exhausted window ----

    [TestMethod]
    public async Task AnOlderDelayedAnnouncementCannotReopenAnExhaustedWindowOnTheSharedPath()
    {
        var gates = Enumerable.Range(0, 3)
            .Select(_ => new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        var next = -1;
        using var rig = Rig.WithHandler((_, _) => gates[Interlocked.Increment(ref next)].Task);
        rig.State.Observe(WithRateHeader("\"Free\";r=2;t=600"));

        var ordinary = rig.Provider.GetDetailsAsync("42", CancellationToken.None);
        while (rig.Requests < 1)
        {
            await Task.Delay(5);
        }

        var discovery = rig.Provider.GetSeriesMembersAsync(new SeriesRef("X", Series100), null, rig.Op(), CancellationToken.None);
        while (rig.Requests < 2)
        {
            await Task.Delay(5);
        }

        // The discovery answer carries the newer count and arrives first; the ordinary answer was counted earlier and arrives late.
        gates[1].SetResult(Json("""{"data":{"series":[]}}""", ("RateLimit", "\"Free\";r=0;t=600")));
        await discovery;
        gates[0].SetResult(Json("""{"data":{"books_by_pk":null}}""", ("RateLimit", "\"Free\";r=1;t=600")));
        await ordinary;
        gates[2].SetResult(Json("""{"data":{"books_by_pk":null}}"""));

        await Assert.ThrowsAsync<HttpRequestException>(() => rig.Provider.GetDetailsAsync("43", CancellationToken.None));
        Assert.AreEqual(2, rig.Requests, "The older r=1 must not reopen the window the newer r=0 closed.");

        rig.Clock.Advance(TimeSpan.FromSeconds(601));
        Assert.IsNull(await rig.Provider.GetDetailsAsync("44", CancellationToken.None));
        Assert.AreEqual(3, rig.Requests, "A genuine reset still lets traffic through.");
    }

    // ---- rate state ----

    [TestMethod]
    public void RateStateReadsTheDocumentedHeaderAndForgetsBucketsItNoLongerReports()
    {
        var clock = new ManualClock();
        var state = new HardcoverRateState(clock);

        state.Observe(WithRateHeader("\"Free\";q=60;w=60;burst=10 \"ignored\", \"Free\";r=0;t=30, \"daily\";r=4231;t=51234"));
        Assert.AreEqual(TimeSpan.FromSeconds(30), state.RequiredWait());
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.AreEqual(TimeSpan.FromSeconds(20), state.RequiredWait());

        state.Observe(WithRateHeader("\"Free\";r=8;t=0, \"daily\";r=4230;t=51200"));
        Assert.AreEqual(TimeSpan.Zero, state.RequiredWait());

        state.Observe(WithRateHeader("\"daily\";r=0;t=7200"));
        Assert.AreEqual(TimeSpan.FromHours(2), state.RequiredWait(), "Once the daily bucket is empty only it is reported.");

        state.Observe(WithRateHeader("garbage;;r=;t="));
        Assert.AreEqual(TimeSpan.FromHours(2), state.RequiredWait(), "A malformed header changes nothing.");
    }

    [TestMethod]
    public void AReservationCountsAgainstEveryBucketUntilItsResponseIsKnown()
    {
        var state = new HardcoverRateState(new ManualClock());
        state.Observe(WithRateHeader("\"Free\";r=2;t=60, \"daily\";r=100;t=3600"));

        var first = state.TryReserve();
        var second = state.TryReserve();
        var third = state.TryReserve();

        Assert.IsNotNull(first.Reservation);
        Assert.IsNotNull(second.Reservation);
        Assert.IsNull(third.Reservation, "Two announced slots are promised to two callers; the third must wait.");
        Assert.AreEqual(TimeSpan.FromSeconds(60), third.Wait);

        using var answered = WithRateHeader("\"Free\";r=1;t=59, \"daily\";r=99;t=3599");
        first.Reservation!.Complete(answered);
        Assert.IsNull(state.TryReserve().Reservation, "The server's r=1 is already held by the other in-flight request.");

        second.Reservation!.Dispose();
        Assert.IsNotNull(state.TryReserve().Reservation, "A request that never got an answer returns its slot.");
    }

    [TestMethod]
    public void ACompletedOrDisposedReservationIsReturnedExactlyOnce()
    {
        var state = new HardcoverRateState(new ManualClock());
        state.Observe(WithRateHeader("\"Free\";r=1;t=60"));
        var held = state.TryReserve().Reservation!;

        held.Dispose();
        held.Dispose();
        held.Complete(null);

        var next = state.TryReserve();
        Assert.IsNotNull(next.Reservation);
        Assert.IsNull(state.TryReserve().Reservation, "A double release must not hand out more than the announced slot.");
    }

    [TestMethod]
    public void NoHeaderNoBucketAndAPassedResetAllConstrainNothing()
    {
        var clock = new ManualClock();
        var state = new HardcoverRateState(clock);

        Assert.IsNotNull(state.TryReserve().Reservation, "Without any announcement there is nothing to pace against.");

        state.Observe(WithRateHeader("\"Free\";r=0;t=30"));
        Assert.IsNull(state.TryReserve().Reservation);
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.IsNotNull(state.TryReserve().Reservation, "The announced window has ended; its numbers describe nothing now.");

        state.Observe(new HttpResponseMessage(HttpStatusCode.OK));
        state.Observe(WithRateHeader("nonsense"));
        Assert.AreEqual(TimeSpan.Zero, state.RequiredWait(), "An absent or malformed header changes nothing.");
    }

    [TestMethod]
    public void ADispatchedReservationWithoutAUsableHeaderIsChargedWhateverTheReason()
    {
        foreach (var completion in new Action<HardcoverRateState.Reservation>[]
        {
            reservation => reservation.Complete(new HttpResponseMessage(HttpStatusCode.OK)),
            reservation => reservation.Complete(WithRateHeader("not-a-quota")),
            reservation => reservation.Complete(WithRateHeader("\"Other\";r=9;t=600")),
            reservation => reservation.Complete(null),
            reservation => reservation.Dispose()
        })
        {
            var state = new HardcoverRateState(new ManualClock());
            state.Observe(WithRateHeader("\"Free\";r=1;t=600"));
            var reservation = state.TryReserve().Reservation!;
            reservation.MarkDispatched();

            completion(reservation);

            Assert.IsNull(state.TryReserve().Reservation, "A sent request whose quota outcome is unknown keeps its slot spent.");
        }
    }

    [TestMethod]
    public void AnUnsentReservationIsReturnedAndChargesNothing()
    {
        var state = new HardcoverRateState(new ManualClock());
        state.Observe(WithRateHeader("\"Free\";r=1;t=600"));

        state.TryReserve().Reservation!.Dispose();
        state.TryReserve().Reservation!.Complete(null);

        Assert.IsNotNull(state.TryReserve().Reservation);
    }

    [TestMethod]
    public void AChargedRequestIsNotRefundedByAnotherRequestsOverlappingHeader()
    {
        var state = new HardcoverRateState(new ManualClock());
        state.Observe(WithRateHeader("\"Free\";r=2;t=600"));
        var silent = state.TryReserve().Reservation!;
        var counted = state.TryReserve().Reservation!;
        silent.MarkDispatched();
        counted.MarkDispatched();

        silent.Complete(new HttpResponseMessage(HttpStatusCode.OK));
        counted.Complete(WithRateHeader("\"Free\";r=1;t=600"));

        Assert.IsNull(state.TryReserve().Reservation, "r=1 may or may not already include the silent request, so it stays charged.");
    }

    [TestMethod]
    public void ChargingNeverInventsAQuotaWhereNoneWasAnnouncedOrItsWindowHasPassed()
    {
        var clock = new ManualClock();
        var state = new HardcoverRateState(clock);
        for (var i = 0; i < 50; i++)
        {
            var reservation = state.TryReserve().Reservation;
            Assert.IsNotNull(reservation, "No announcement means no quota to spend.");
            reservation.MarkDispatched();
            reservation.Complete(new HttpResponseMessage(HttpStatusCode.OK));
        }

        state.Observe(WithRateHeader("\"Free\";r=1;t=30"));
        clock.Advance(TimeSpan.FromSeconds(31));
        var late = state.TryReserve().Reservation!;
        late.MarkDispatched();
        late.Complete(null);

        Assert.IsNotNull(state.TryReserve().Reservation);
        Assert.IsNotNull(state.TryReserve().Reservation, "A passed window is not charged.");
        Assert.AreEqual(TimeSpan.Zero, state.RequiredWait());
    }

    [TestMethod]
    public void AnOlderDelayedHeaderCannotReopenAnExhaustedWindowButAGenuineResetCan()
    {
        var clock = new ManualClock();
        var state = new HardcoverRateState(clock);
        state.Observe(WithRateHeader("\"Free\";r=2;t=600"));
        var older = state.TryReserve().Reservation!;
        var newer = state.TryReserve().Reservation!;
        older.MarkDispatched();
        newer.MarkDispatched();

        newer.Complete(WithRateHeader("\"Free\";r=0;t=600"));
        older.Complete(WithRateHeader("\"Free\";r=1;t=600"));

        Assert.IsNull(state.TryReserve().Reservation, "The exhaustion already observed stands.");
        Assert.AreEqual(TimeSpan.FromSeconds(600), state.RequiredWait());

        clock.Advance(TimeSpan.FromSeconds(601));
        state.Observe(WithRateHeader("\"Free\";r=2;t=600"));
        Assert.IsNotNull(state.TryReserve().Reservation);
        Assert.IsNotNull(state.TryReserve().Reservation);
        Assert.IsNull(state.TryReserve().Reservation, "The new window's own announcement governs it.");
    }

    [TestMethod]
    public void ADelayedHeaderThatLeavesOutABucketDoesNotReopenIt()
    {
        var state = new HardcoverRateState(new ManualClock());
        state.Observe(WithRateHeader("\"Free\";r=0;t=600, \"daily\";r=50;t=3600"));

        state.Observe(WithRateHeader("\"daily\";r=51;t=3601"));

        Assert.IsNull(state.TryReserve().Reservation);
        Assert.AreEqual(TimeSpan.FromSeconds(600), state.RequiredWait());
    }

    [TestMethod]
    public void AWindowsReportedCountOnlyFallsWhileItsResetIsInTheFuture()
    {
        var clock = new ManualClock();
        var state = new HardcoverRateState(clock);
        state.Observe(WithRateHeader("\"Free\";r=3;t=60"));
        state.Observe(WithRateHeader("\"Free\";r=2;t=59"));
        state.Observe(WithRateHeader("\"Free\";r=3;t=60"));

        var held = new[] { state.TryReserve().Reservation, state.TryReserve().Reservation, state.TryReserve().Reservation };

        Assert.IsNotNull(held[0]);
        Assert.IsNotNull(held[1]);
        Assert.IsNull(held[2], "A later r=3 inside the same window is stale evidence; r=2 stands.");
    }

    // ---- helpers ----

    private sealed class ThrowingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new InvalidOperationException("The body cannot be copied.");

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    // The real rate handler over a scripted transport, called directly so a test controls the token and the request body.
    private sealed class DirectChain : IDisposable
    {
        private readonly HttpMessageInvoker invoker;
        private readonly Func<HttpContent> content;
        private int requests;

        public DirectChain(
            HardcoverRateState state,
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
            Func<HttpContent>? content = null)
        {
            State = state;
            this.content = content ?? (() => new StringContent("{}", Encoding.UTF8, "application/json"));
            var handler = new HardcoverRateLimitHandler(
                Options.Create(new HardcoverMetadataOptions { MaxRetryAttempts = 3, MaxRetryDelaySeconds = 30 }),
                state)
            {
                InnerHandler = new Scripted((request, token) =>
                {
                    Interlocked.Increment(ref requests);
                    return send(request, token);
                })
            };
            invoker = new HttpMessageInvoker(handler);
        }

        public HardcoverRateState State { get; }

        public int Requests => Volatile.Read(ref requests);

        public void Dispose() => invoker.Dispose();

        public Task<HttpResponseMessage> SendAsync(CancellationToken cancellationToken) =>
            invoker.SendAsync(
                new HttpRequestMessage(HttpMethod.Post, "https://unit.invalid/graphql") { Content = content() },
                cancellationToken);

        private sealed class Scripted(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                send(request, cancellationToken);
        }
    }

    private static DatePrecision PublicationDatePrecisionOf(AuthorWork work) => work.Book.Publication.Precision!.Value;

    private static string SeriesPage(int rows, int startId)
    {
        var builder = new StringBuilder("""{"data":{"series":[{"id":100,"name":"Big","canonical_id":null,"book_series":[""");
        for (var row = 0; row < rows; row++)
        {
            if (row > 0)
            {
                builder.Append(',');
            }

            var id = startId + row;
            var text = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            builder.Append("{\"position\":" + text + ",\"details\":null,\"compilation\":false,\"featured\":false,")
                .Append("\"book\":{\"id\":" + text + ",\"title\":\"Book " + text + "\",\"contributions\":[]}}");
        }

        return builder.Append("]}]}}").ToString();
    }

    private static HttpResponseMessage WithRateHeader(string value)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation(HardcoverRateState.HeaderName, value);
        return response;
    }

    // Counts selection sets opened directly under the operation, ignoring braces inside argument lists: each top-level
    // field Hardcover bills as one request opens exactly one.
    private static int TopLevelFieldCount(string query)
    {
        var depth = 0;
        var parenthesis = 0;
        var fields = 0;
        foreach (var character in query)
        {
            switch (character)
            {
                case '(':
                    parenthesis++;
                    break;
                case ')':
                    parenthesis--;
                    break;
                case '{' when parenthesis == 0:
                    depth++;
                    if (depth == 2)
                    {
                        fields++;
                    }

                    break;
                case '}' when parenthesis == 0:
                    depth--;
                    break;
            }
        }

        return fields;
    }

    private static HttpResponseMessage Json(string json, params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        foreach (var (name, value) in headers)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }

        return response;
    }

    private static HttpResponseMessage Plain(HttpStatusCode status, string body = "") => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage RateLimited(int retryAfterSeconds = 0)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
            retryAfterSeconds == 0 ? TimeSpan.FromMilliseconds(1) : TimeSpan.FromSeconds(retryAfterSeconds));
        return response;
    }

    private sealed class Rig : IDisposable
    {
        private readonly HttpClient client;
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler;
        private int requests;

        private Rig(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler,
            string? token,
            HardcoverRateState? state = null,
            Func<TimeSpan, CancellationToken, Task>? pause = null)
        {
            this.handler = handler;
            State = state ?? new HardcoverRateState(Clock);
            var transport = new Transport(Handle);
            var rateLimit = new HardcoverRateLimitHandler(
                Options.Create(new HardcoverMetadataOptions { MaxRetryAttempts = 3, MaxRetryDelaySeconds = 30 }),
                State,
                pause)
            {
                InnerHandler = transport
            };
            var authorization = new HardcoverAuthorizationHandler(new Credential(token)) { InnerHandler = rateLimit };
            client = new HttpClient(authorization) { BaseAddress = new Uri("https://api.hardcover.app/v1/graphql") };
            Provider = new HardcoverBookMetadataProvider(
                client, new HardcoverLanguageLookupCache(new Factory(client)), Clock);
        }

        public HardcoverBookMetadataProvider Provider { get; }

        public ManualClock Clock { get; } = new();

        /// <summary>The shared credential state the handler consults, on this rig's manual clock.</summary>
        public HardcoverRateState State { get; }

        public List<string> Bodies { get; } = [];

        public int Requests => Volatile.Read(ref requests);

        public static Rig With(params HttpResponseMessage[] responses) => WithToken("test-token", responses);

        public static Rig With(string? token) => WithToken(token, []);

        public static Rig WithHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) => new(handler, "test-token");

        /// <summary>A rig whose handler pauses through <paramref name="pause"/> (so a test controls what each pause does to the manual clock).</summary>
        public static Rig WithPause(Func<Rig, TimeSpan, CancellationToken, Task> pause, params HttpResponseMessage[] responses)
        {
            Rig? created = null;
            var queue = new Queue<HttpResponseMessage>(responses);
            created = new Rig((_, _) =>
            {
                lock (queue)
                {
                    return Task.FromResult(queue.Count > 0 ? queue.Dequeue() : new HttpResponseMessage(HttpStatusCode.InternalServerError));
                }
            }, "test-token", null, (wait, token) => pause(created!, wait, token));
            return created;
        }

        public static Rig WithRealClockState(params HttpResponseMessage[] responses) => WithToken("test-token", responses, new HardcoverRateState());

        private static Rig WithToken(string? token, HttpResponseMessage[] responses, HardcoverRateState? state = null)
        {
            var queue = new Queue<HttpResponseMessage>(responses);
            return new Rig((_, _) =>
            {
                lock (queue)
                {
                    return Task.FromResult(queue.Count > 0
                        ? queue.Dequeue()
                        : new HttpResponseMessage(HttpStatusCode.InternalServerError));
                }
            }, token, state);
        }

        public DiscoveryOperationBudget Op(DiscoveryLimits? limits = null, TimeProvider? clock = null)
        {
            var journey = new DiscoveryJourney(Guid.NewGuid(), limits ?? DiscoveryLimits.Default, clock ?? Clock);
            return journey.BeginOperation(DiscoveryOperation.GetSeriesMembers, "hardcover", Series100)!;
        }

        public void Dispose() => client.Dispose();

        private async Task<HttpResponseMessage> Handle(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requests);
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            lock (Bodies)
            {
                Bodies.Add(body);
            }

            return await handler(request, cancellationToken);
        }

        private sealed class Transport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                send(request, cancellationToken);
        }

        private sealed class Credential(string? token) : IMetadataCredentialAccessor
        {
            public Task<string?> GetCredentialAsync(string providerId, CancellationToken cancellationToken) => Task.FromResult(token);
        }

        private sealed class Factory(HttpClient shared) : IHttpClientFactory
        {
            public HttpClient CreateClient(string name) => shared;
        }
    }
}
