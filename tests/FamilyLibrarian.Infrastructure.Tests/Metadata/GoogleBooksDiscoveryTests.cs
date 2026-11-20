using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Catalog.Discovery;
using FamilyLibrarian.Domain.Catalog;
using FamilyLibrarian.Infrastructure.Integrations;
using FamilyLibrarian.Infrastructure.Metadata;
using FamilyLibrarian.Infrastructure.Tests.Catalog;
using Microsoft.Extensions.Options;

namespace FamilyLibrarian.Infrastructure.Tests.Metadata;

/// <summary>
/// Google Books series discovery through the real key handler and the real (budget-aware) retry handler over a scripted
/// transport, so physical attempts are counted where they happen.
/// <para>
/// Fixture provenance, stated plainly. <b>Captured</b> = a real Google response saved by the comparison lab on 2026-11-16 (its
/// own API key, <c>volumes.get?includeNonComicsSeries=true</c>), trimmed to the fields the adapter reads:
/// <see cref="CapturedFourthWing"/> (carries <c>seriesInfo</c>) and <see cref="CapturedHarryPotter"/> (carries none). No request
/// was made to Google for this implementation. <b>Hand-built</b> = constructed from Google's published Discovery schema
/// (revision 20261110) to exercise a case no captured response held (several series, conflicting numbers, missing ids, unreadable
/// numbers, error statuses); none of those is a provider response. The Discovery schema, not any sample, is the source for field
/// names and meanings.
/// </para>
/// </summary>
[TestClass]
public sealed class GoogleBooksDiscoveryTests
{
    private const string Key = "test-api-key";
    private static readonly SourceRef FourthWingVolume = new("googlebooks", "E-OLEAAAQBAJ", ExternalReferenceEntityType.Edition);
    private static readonly SourceRef EmpyreanSeries = new("googlebooks", "da0vGwAAABBbtM", ExternalReferenceEntityType.Series);

    // ---- captured (lab, 2026-11-16), trimmed ----

    private const string CapturedFourthWing =
        """{"kind":"books#volume","id":"E-OLEAAAQBAJ","volumeInfo":{"title":"Fourth Wing","authors":["Rebecca Yarros"],"publisher":"Entangled Publishing, LLC","publishedDate":"2023-05-02","language":"en","seriesInfo":{"kind":"books#volume_series_info","shortSeriesBookTitle":"Fourth Wing","bookDisplayNumber":"1","volumeSeries":[{"seriesId":"da0vGwAAABBbtM","seriesBookType":"ISSUE","orderNumber":1}]}}}""";

    private const string CapturedHarryPotter =
        """{"kind":"books#volume","id":"fo4rzdaHDAwC","volumeInfo":{"title":"Harry Potter and the Sorcerer's Stone","authors":["J. K. Rowling"],"publisher":"Scholastic","publishedDate":"1998","language":"en"}}""";

    private static string Volume(string id, string? seriesInfo) =>
        "{\"id\":\"" + id + "\",\"volumeInfo\":{\"title\":\"T\"" +
        (seriesInfo is null ? string.Empty : ",\"seriesInfo\":" + seriesInfo) + "}}";

    private static SourceRef Edition(string id) => new("googlebooks", id, ExternalReferenceEntityType.Edition);

    // ================================================================ captured shapes

    [TestMethod]
    public async Task ACapturedVolumeGivesOneNativeSeriesIdWithItsPositionFromOneRequest()
    {
        using var rig = Rig.With(Json(CapturedFourthWing));

        var result = await rig.Provider.FindSeriesClaimsForBookAsync(FourthWingVolume, rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Ok, result.Outcome);
        Assert.AreEqual(DiscoveryCompletion.ExhaustedInCheckedScope, result.Completion);
        var claim = result.Items.Single();
        Assert.AreEqual(EmpyreanSeries, claim.Series.Id);
        Assert.IsTrue(claim.Series.IsBound);
        Assert.AreEqual(string.Empty, claim.Series.Label, "Google sends no series name, and none is invented from a book title.");
        Assert.AreEqual("1", claim.Position!.Label);
        Assert.AreEqual(1m, claim.Position.Sort);
        Assert.IsNull(claim.Position.Ordering, "The number does not say publication or reading order.");
        Assert.AreEqual(EmpyreanSeries, claim.Position.Series);
        Assert.AreEqual(MembershipRole.Unknown, claim.Role, "seriesBookType ISSUE on a novel proves nothing about role.");
        CollectionAssert.Contains(result.Notes.ToArray(), "series-name-not-provided");
        Assert.AreEqual(1, rig.Requests, "A volume lookup is one physical request.");
        Assert.AreEqual(1, result.Usage.Attempts);
        Assert.AreEqual(0, result.Usage.Retries);
        Assert.AreEqual(1, result.Usage.Items);
        Assert.AreEqual("volume-series-info-include-non-comics", result.Scope.Filter);
        Assert.IsTrue(rig.AllGet, "Discovery only reads.");
        StringAssert.Contains(rig.Uris[0], "volumes/E-OLEAAAQBAJ?includeNonComicsSeries=true");
        StringAssert.Contains(rig.Uris[0], "key=" + Key, "The existing handler adds the key; the adapter never builds it.");
    }

    [TestMethod]
    public async Task TheSharedBookToSeriesOperationReturnsTheSameNativeSeriesRef()
    {
        using var rig = Rig.With(Json(CapturedFourthWing));
        var provider = Assert.IsInstanceOfType<IBookMetadataDiscoveryProvider>(rig.Provider);

        var result = await provider.FindSeriesForBookAsync(FourthWingVolume, rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Ok, result.Outcome);
        Assert.AreEqual(EmpyreanSeries, result.Items.Single().Id);
        Assert.AreEqual(DiscoveryOperation.FindSeriesForBook, result.Scope.Operation);
        Assert.AreEqual(1, rig.Requests);
    }

    [TestMethod]
    public async Task TheSharedClaimsOperationCarriesTheEditionSubjectAndPositionThroughTheInterface()
    {
        using var rig = Rig.With(Json(CapturedFourthWing), Json(CapturedFourthWing));
        var shared = Assert.IsInstanceOfType<IBookMetadataDiscoveryProvider>(rig.Provider);

        var claims = await shared.FindSeriesClaimsForBookAsync(FourthWingVolume, rig.Op(), CancellationToken.None);
        var series = await shared.FindSeriesForBookAsync(FourthWingVolume, rig.Op(), CancellationToken.None);

        Assert.AreEqual(FourthWingVolume, claims.Scope.Subject, "The subject keeps its Edition kind.");
        Assert.AreEqual(1m, claims.Items.Single().Position!.Sort);
        Assert.AreEqual(claims.Items.Single().Series, series.Items.Single());
        Assert.AreEqual(2, rig.Requests, "Each view is exactly one read; a caller uses one of them per lookup.");
    }

    [TestMethod]
    public async Task EveryNonAnswerStateSurvivesTheSeriesOnlyView()
    {
        using var throttled = Rig.With(Plain(HttpStatusCode.TooManyRequests));
        using var denied = Rig.With(Plain(HttpStatusCode.Forbidden));
        using var unusable = Rig.With(Json(Volume("v1", """{"volumeSeries":[{"orderNumber":1}]}""")));

        var first = await throttled.Provider.FindSeriesForBookAsync(FourthWingVolume, throttled.Op(), CancellationToken.None);
        var second = await denied.Provider.FindSeriesForBookAsync(FourthWingVolume, denied.Op(), CancellationToken.None);
        var third = await unusable.Provider.FindSeriesForBookAsync(Edition("v1"), unusable.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Throttled, first.Outcome);
        Assert.AreEqual(DiscoveryOutcome.Unavailable, second.Outcome);
        Assert.AreEqual(DiscoveryOutcome.NoUsableItems, third.Outcome);
        Assert.AreEqual(1, first.Usage.Attempts);
    }

    [TestMethod]
    public async Task ACapturedVolumeWithoutSeriesInfoIsEmptyInThatResponseOnly()
    {
        using var rig = Rig.With(Json(CapturedHarryPotter));

        var result = await rig.Provider.FindSeriesClaimsForBookAsync(Edition("fo4rzdaHDAwC"), rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Empty, result.Outcome);
        CollectionAssert.Contains(result.Notes.ToArray(), "no-series-info-in-volume");
        Assert.AreEqual("volume-series-info-include-non-comics", result.Scope.Filter, "Empty names what was actually checked.");
        Assert.AreEqual(1, rig.Requests);
    }

    // ================================================================ hand-built shapes

    [TestMethod]
    public async Task SeriesInfoWithNoSeriesListIsEmptyWithItsOwnReason()
    {
        using var none = Rig.With(Json(Volume("v1", """{"kind":"books#volume_series_info","bookDisplayNumber":"2"}""")));
        using var empty = Rig.With(Json(Volume("v1", """{"volumeSeries":[]}""")));

        var absent = await none.Provider.FindSeriesClaimsForBookAsync(Edition("v1"), none.Op(), CancellationToken.None);
        var listed = await empty.Provider.FindSeriesClaimsForBookAsync(Edition("v1"), empty.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Empty, absent.Outcome);
        Assert.AreEqual(DiscoveryOutcome.Empty, listed.Outcome);
        CollectionAssert.Contains(absent.Notes.ToArray(), "series-info-without-series");
    }

    [TestMethod]
    public async Task SeveralSeriesKeepTheirOwnNumbersAndTheVolumeLevelDisplayNumberIsNotSharedAcrossThem()
    {
        using var rig = Rig.With(Json(Volume("v1",
            """{"bookDisplayNumber":"3","volumeSeries":[{"seriesId":"sA","orderNumber":2},{"seriesId":"sB","orderNumber":5}]}""")));

        var result = await rig.Provider.FindSeriesClaimsForBookAsync(Edition("v1"), rig.Op(), CancellationToken.None);

        Assert.HasCount(2, result.Items);
        Assert.AreEqual("2", result.Items[0].Position!.Label);
        Assert.AreEqual(2m, result.Items[0].Position!.Sort);
        Assert.AreEqual("5", result.Items[1].Position!.Label);
        Assert.AreEqual("sA", result.Items[0].Series.Id!.ExternalId);
        Assert.AreEqual("sB", result.Items[1].Series.Id!.ExternalId);
    }

    [TestMethod]
    public async Task ADisplayNumberThatDiffersFromTheOrderNumberKeepsBothAsSeparateFacts()
    {
        // The schema says the display string is for display only and the order is "inferred from orderNumber".
        using var rig = Rig.With(Json(Volume("v1",
            """{"bookDisplayNumber":"3.5","volumeSeries":[{"seriesId":"sA","orderNumber":4}]}""")));

        var result = await rig.Provider.FindSeriesClaimsForBookAsync(Edition("v1"), rig.Op(), CancellationToken.None);

        Assert.AreEqual("3.5", result.Items.Single().Position!.Label);
        Assert.AreEqual(4m, result.Items.Single().Position!.Sort);
    }

    [TestMethod]
    public async Task TheSameSeriesWithTwoNumbersKeepsBothClaimsAndSharedRefsListItOnce()
    {
        using var rig = Rig.With(
            Json(Volume("v1", """{"volumeSeries":[{"seriesId":"sA","orderNumber":1},{"seriesId":"sA","orderNumber":9}]}""")),
            Json(Volume("v1", """{"volumeSeries":[{"seriesId":"sA","orderNumber":1},{"seriesId":"sA","orderNumber":9}]}""")));

        var claims = await rig.Provider.FindSeriesClaimsForBookAsync(Edition("v1"), rig.Op(), CancellationToken.None);
        var series = await rig.Provider.FindSeriesForBookAsync(Edition("v1"), rig.Op(), CancellationToken.None);

        Assert.HasCount(2, claims.Items, "A conflict is preserved, not resolved.");
        Assert.AreEqual(1m, claims.Items[0].Position!.Sort);
        Assert.AreEqual(9m, claims.Items[1].Position!.Sort);
        CollectionAssert.Contains(claims.Notes.ToArray(), "series-position-conflict");
        Assert.HasCount(1, series.Items);
    }

    [TestMethod]
    public async Task AnExactRepeatedEntryIsDroppedAsADuplicate()
    {
        using var rig = Rig.With(Json(Volume("v1",
            """{"volumeSeries":[{"seriesId":"sA","orderNumber":1},{"seriesId":"sA","orderNumber":1}]}""")));

        var result = await rig.Provider.FindSeriesClaimsForBookAsync(Edition("v1"), rig.Op(), CancellationToken.None);

        Assert.HasCount(1, result.Items);
        CollectionAssert.Contains(result.Notes.ToArray(), DiscoveryCodes.DuplicateDropped);
    }

    [TestMethod]
    public async Task EntriesWithoutAUsableSeriesIdAreDroppedAndTheUsableOnesKept()
    {
        using var rig = Rig.With(Json(Volume("v1",
            """{"volumeSeries":[null,{"orderNumber":1},{"seriesId":"has space","orderNumber":2},{"seriesId":"sOk","orderNumber":3}]}""")));

        var result = await rig.Provider.FindSeriesClaimsForBookAsync(Edition("v1"), rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Ok, result.Outcome);
        Assert.AreEqual("sOk", result.Items.Single().Series.Id!.ExternalId);
        CollectionAssert.Contains(result.Notes.ToArray(), DiscoveryCodes.ItemMissingId);
    }

    [TestMethod]
    public async Task WhenNoEntryHasAUsableIdTheAnswerIsNoUsableItemsNeverEmpty()
    {
        using var rig = Rig.With(Json(Volume("v1", """{"volumeSeries":[{"orderNumber":1},null]}""")));

        var result = await rig.Provider.FindSeriesClaimsForBookAsync(Edition("v1"), rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.NoUsableItems, result.Outcome);
        Assert.IsEmpty(result.Items);
        CollectionAssert.Contains(result.Notes.ToArray(), DiscoveryCodes.ItemMissingId);
    }

    [TestMethod]
    [DataRow("\"three\"")]
    [DataRow("1.5")]
    [DataRow("-1")]
    [DataRow("99999999999")]
    [DataRow("true")]
    public async Task AnUnreadableOrderNumberKeepsTheClaimWithoutASortValueAndSaysSo(string order)
    {
        using var rig = Rig.With(Json(Volume("v1", "{\"volumeSeries\":[{\"seriesId\":\"sA\",\"orderNumber\":" + order + "}]}")));

        var result = await rig.Provider.FindSeriesClaimsForBookAsync(Edition("v1"), rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Ok, result.Outcome);
        Assert.IsNull(result.Items.Single().Position, "No number and no display number means no position claim.");
        CollectionAssert.Contains(result.Notes.ToArray(), "series-order-unreadable");
    }

    [TestMethod]
    public async Task ADisplayNumberAloneStillGivesALabelOnlyPositionForASingleSeries()
    {
        using var rig = Rig.With(Json(Volume("v1", """{"bookDisplayNumber":"Book 3","volumeSeries":[{"seriesId":"sA"}]}""")));

        var result = await rig.Provider.FindSeriesClaimsForBookAsync(Edition("v1"), rig.Op(), CancellationToken.None);

        Assert.AreEqual("Book 3", result.Items.Single().Position!.Label);
        Assert.IsNull(result.Items.Single().Position!.Sort);
    }

    [TestMethod]
    public async Task OmnibusIssueListsAreReportedAsNotMappedNotInvented()
    {
        using var rig = Rig.With(Json(Volume("v1",
            """{"volumeSeries":[{"seriesId":"sA","orderNumber":1,"seriesBookType":"COLLECTED_EDITION","issue":[{"issueDisplayNumber":"1","issueOrderNumber":1}]}]}""")));

        var result = await rig.Provider.FindSeriesClaimsForBookAsync(Edition("v1"), rig.Op(), CancellationToken.None);

        Assert.AreEqual(MembershipRole.Unknown, result.Items.Single().Role);
        CollectionAssert.Contains(result.Notes.ToArray(), "series-issues-not-mapped");
    }

    [TestMethod]
    public async Task AResponseForADifferentVolumeIsNeverReadAsThisVolumesClaims()
    {
        using var rig = Rig.With(Json(Volume("someOtherVolume", """{"volumeSeries":[{"seriesId":"sA","orderNumber":1}]}""")));

        var result = await rig.Provider.FindSeriesClaimsForBookAsync(Edition("v1"), rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.NoUsableItems, result.Outcome);
        CollectionAssert.Contains(result.Notes.ToArray(), "book-anchor-mismatch");
    }

    // ================================================================ identity of the subject

    [TestMethod]
    [DataRow("hardcover", "v1", ExternalReferenceEntityType.Edition)]
    [DataRow("googlebooks", "v1", ExternalReferenceEntityType.Work)]
    [DataRow("googlebooks", "v1", ExternalReferenceEntityType.Series)]
    [DataRow("googlebooks", "../v1", ExternalReferenceEntityType.Edition)]
    [DataRow("googlebooks", "v1?key=x", ExternalReferenceEntityType.Edition)]
    [DataRow("googlebooks", "has space", ExternalReferenceEntityType.Edition)]
    public async Task ASubjectThatIsNotAGoogleVolumeEditionMakesNoRequest(string provider, string id, ExternalReferenceEntityType kind)
    {
        using var rig = Rig.With();

        var result = await rig.Provider.FindSeriesClaimsForBookAsync(new SourceRef(provider, id, kind), rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Failed, result.Outcome);
        CollectionAssert.Contains(result.Notes.ToArray(), DiscoveryCodes.SubjectInvalid);
        Assert.AreEqual(0, rig.Requests);
    }

    // ================================================================ operations Google does not offer

    [TestMethod]
    public async Task EveryOperationGoogleDoesNotOfferMakesNoRequestAndIsNeverEmpty()
    {
        using var rig = Rig.With();
        var series = new SeriesRef("S", EmpyreanSeries);
        var author = new AuthorRef("A", new SourceRef("googlebooks", "a1", ExternalReferenceEntityType.Author));

        var members = await rig.Provider.GetSeriesMembersAsync(series, null, rig.Op(), CancellationToken.None);
        var resolved = await rig.Provider.ResolveAuthorAsync(AuthorAnchor.FromNameOnly("Rebecca Yarros"), rig.Op(), CancellationToken.None);
        var works = await rig.Provider.GetAuthorWorksAsync(author, null, rig.Op(), CancellationToken.None);
        var authorSeries = await rig.Provider.GetAuthorSeriesAsync(author, null, rig.Op(), CancellationToken.None);

        AssertUnsupported(members, "series-endpoints-not-key-accessible");
        AssertUnsupported(resolved, "no-native-author-entity");
        AssertUnsupported(works, "no-author-id-query");
        AssertUnsupported(authorSeries, "no-author-to-series-query");
        Assert.AreEqual(0, rig.Requests);
    }

    [TestMethod]
    public void TheCapabilityTableSaysWhatIsUnsupportedAndNeverClaimsSupportForTheUnprobedOperation()
    {
        using var rig = Rig.With();
        var provider = Assert.IsInstanceOfType<IBookMetadataDiscoveryProvider>(rig.Provider);

        Assert.AreEqual("googlebooks", provider.ProviderId);
        Assert.AreEqual(CapabilityState.Unverified, provider.Capabilities.Get(DiscoveryOperation.FindSeriesForBook).State);
        foreach (var operation in new[]
                 {
                     DiscoveryOperation.GetSeriesMembers, DiscoveryOperation.ResolveAuthor,
                     DiscoveryOperation.GetAuthorWorks, DiscoveryOperation.GetAuthorSeries
                 })
        {
            Assert.AreEqual(CapabilityState.Unsupported, provider.Capabilities.Get(operation).State, operation.ToString());
        }
    }

    // ================================================================ real transport: attempts, retry, Retry-After

    [TestMethod]
    public async Task OneTransientFailureIsRetriedOnceThroughTheBudgetAndBothAttemptsAreCounted()
    {
        using var rig = Rig.With(Plain(HttpStatusCode.ServiceUnavailable), Json(CapturedFourthWing));

        var result = await rig.Provider.FindSeriesClaimsForBookAsync(FourthWingVolume, rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Ok, result.Outcome);
        Assert.AreEqual(2, rig.Requests);
        Assert.AreEqual(2, result.Usage.Attempts);
        Assert.AreEqual(1, result.Usage.Retries);
        Assert.AreEqual(rig.Uris[0], rig.Uris[1], "The retry is the same request, key included.");
    }

    [TestMethod]
    public async Task TheApprovedProbeShapeIsExactlyOnePhysicalRequestEvenWhenGoogleAnswers503()
    {
        // Section 12.1 D5: one attempt, no retry. The retry handler's own policy would send three; the budget decides.
        using var rig = Rig.With(Plain(HttpStatusCode.ServiceUnavailable), Plain(HttpStatusCode.ServiceUnavailable), Plain(HttpStatusCode.ServiceUnavailable));
        var probe = new DiscoveryLimits { OperationMaxAttempts = 1, OperationMaxRetries = 0, OperationMaxWork = TimeSpan.FromSeconds(15) };

        var result = await rig.Provider.FindSeriesClaimsForBookAsync(FourthWingVolume, rig.Op(probe), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Failed, result.Outcome);
        CollectionAssert.Contains(result.Notes.ToArray(), DiscoveryCodes.ProviderError);
        Assert.AreEqual(1, rig.Requests);
        Assert.AreEqual(1, result.Usage.Attempts);
        Assert.AreEqual(0, result.Usage.Retries);
    }

    [TestMethod]
    public async Task RepeatedTransientFailuresStopAtTheBudgetsOneRetryNotTheHandlersTwo()
    {
        using var rig = Rig.With(
            Plain(HttpStatusCode.BadGateway), Plain(HttpStatusCode.BadGateway), Plain(HttpStatusCode.BadGateway), Json(CapturedFourthWing));

        var result = await rig.Provider.FindSeriesClaimsForBookAsync(FourthWingVolume, rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Failed, result.Outcome);
        Assert.AreEqual(2, rig.Requests, "The ordinary handler would have made three.");
        Assert.AreEqual(1, result.Usage.Retries);
    }

    [TestMethod]
    public async Task ARetryAfterThatDoesNotFitEndsTheOperationInsteadOfRetryingEarly()
    {
        using var rig = Rig.With(Plain(HttpStatusCode.ServiceUnavailable, retryAfterSeconds: 120), Json(CapturedFourthWing));

        var result = await rig.Provider.FindSeriesClaimsForBookAsync(FourthWingVolume, rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Throttled, result.Outcome);
        CollectionAssert.Contains(result.Notes.ToArray(), DiscoveryCodes.RetryAfterTooLong);
        Assert.AreEqual(1, rig.Requests, "No early retry and no shortened wait.");
    }

    [TestMethod]
    public async Task ARetryAfterThatFitsIsWaitedOutInFullBeforeTheOneRetry()
    {
        using var rig = Rig.With(Plain(HttpStatusCode.ServiceUnavailable, retryAfterSeconds: 1), Json(CapturedFourthWing));
        var clock = Stopwatch.StartNew();

        var result = await rig.Provider.FindSeriesClaimsForBookAsync(FourthWingVolume, rig.Op(), CancellationToken.None);

        clock.Stop();
        Assert.AreEqual(DiscoveryOutcome.Ok, result.Outcome);
        Assert.AreEqual(2, rig.Requests);
        Assert.IsGreaterThanOrEqualTo(900, clock.ElapsedMilliseconds, "The announced second is honoured, not the handler's 5 ms.");
    }

    [TestMethod]
    public async Task A429IsReportedOnceAndNeverRetried()
    {
        using var rig = Rig.With(Plain(HttpStatusCode.TooManyRequests), Json(CapturedFourthWing));

        var result = await rig.Provider.FindSeriesClaimsForBookAsync(FourthWingVolume, rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Throttled, result.Outcome);
        CollectionAssert.Contains(result.Notes.ToArray(), DiscoveryCodes.Throttled);
        Assert.AreEqual(1, rig.Requests);
    }

    [TestMethod]
    public async Task A429WithALongRetryAfterSaysItIsTooLongAndStillSendsOnce()
    {
        using var rig = Rig.With(Plain(HttpStatusCode.TooManyRequests, retryAfterSeconds: 3600));

        var result = await rig.Provider.FindSeriesClaimsForBookAsync(FourthWingVolume, rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Throttled, result.Outcome);
        CollectionAssert.Contains(result.Notes.ToArray(), DiscoveryCodes.RetryAfterTooLong);
        Assert.AreEqual(1, rig.Requests);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Unauthorized, DiscoveryOutcome.Unavailable, "authorization-denied")]
    [DataRow(HttpStatusCode.Forbidden, DiscoveryOutcome.Unavailable, "authorization-denied")]
    [DataRow(HttpStatusCode.NotFound, DiscoveryOutcome.Failed, "subject-not-found")]
    [DataRow(HttpStatusCode.BadRequest, DiscoveryOutcome.Failed, "provider-error")]
    public async Task ANonRetryableStatusIsOneRequestAndAStableCode(HttpStatusCode status, DiscoveryOutcome outcome, string code)
    {
        using var rig = Rig.With(Plain(status), Json(CapturedFourthWing));

        var result = await rig.Provider.FindSeriesClaimsForBookAsync(FourthWingVolume, rig.Op(), CancellationToken.None);

        Assert.AreEqual(outcome, result.Outcome);
        CollectionAssert.AreEqual(new[] { code }, result.Notes.ToArray());
        Assert.AreEqual(1, rig.Requests);
    }

    [TestMethod]
    public async Task AMissingKeyIsUnavailableSetupAndSendsNothing()
    {
        using var rig = Rig.With(apiKey: null, Json(CapturedFourthWing));

        var result = await rig.Provider.FindSeriesClaimsForBookAsync(FourthWingVolume, rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Unavailable, result.Outcome);
        CollectionAssert.Contains(result.Notes.ToArray(), DiscoveryCodes.SetupMissing);
        Assert.AreEqual(0, rig.Requests);
    }

    [TestMethod]
    public async Task AnExhaustedAttemptCeilingRefusesBeforeDispatch()
    {
        using var rig = Rig.With(Json(CapturedFourthWing), Json(CapturedFourthWing));
        var limits = new DiscoveryLimits { OperationMaxAttempts = 1 };
        var operation = rig.Op(limits);

        var first = await rig.Provider.FindSeriesClaimsForBookAsync(FourthWingVolume, operation, CancellationToken.None);
        var second = await rig.Provider.FindSeriesClaimsForBookAsync(FourthWingVolume, operation, CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Ok, first.Outcome);
        Assert.AreEqual(DiscoveryOutcome.Failed, second.Outcome);
        CollectionAssert.Contains(second.Notes.ToArray(), DiscoveryCodes.AttemptCeiling);
        Assert.AreEqual(1, rig.Requests);
    }

    [TestMethod]
    public async Task AnExpiredJourneyRefusesBeforeDispatch()
    {
        using var rig = Rig.With(Json(CapturedFourthWing));
        var operation = rig.Op();
        rig.Clock.Advance(TimeSpan.FromMinutes(11));

        var result = await rig.Provider.FindSeriesClaimsForBookAsync(FourthWingVolume, operation, CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Failed, result.Outcome);
        CollectionAssert.Contains(result.Notes.ToArray(), DiscoveryCodes.JourneyExpired);
        Assert.AreEqual(0, rig.Requests);
    }

    [TestMethod]
    public async Task ARequestThatOutlastsTheProviderWorkDeadlineIsTimedOutAfterOneAttempt()
    {
        using var rig = Rig.WithHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return Plain(HttpStatusCode.OK);
        });
        var limits = new DiscoveryLimits { OperationMaxWork = TimeSpan.FromMilliseconds(250) };
        var operation = new DiscoveryJourney(Guid.NewGuid(), limits, TimeProvider.System)
            .BeginOperation(DiscoveryOperation.FindSeriesForBook, "googlebooks", null)!;

        var result = await rig.Provider.FindSeriesClaimsForBookAsync(FourthWingVolume, operation, CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.TimedOut, result.Outcome);
        CollectionAssert.Contains(result.Notes.ToArray(), DiscoveryCodes.DeadlineReached);
        Assert.AreEqual(1, rig.Requests);
    }

    [TestMethod]
    public async Task CallerCancellationPropagatesAndStopsFurtherDispatch()
    {
        using var cancel = new CancellationTokenSource();
        using var rig = Rig.WithHandler(async (_, token) =>
        {
            await cancel.CancelAsync();
            await Task.Delay(Timeout.Infinite, token);
            return Plain(HttpStatusCode.OK);
        });

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            rig.Provider.FindSeriesClaimsForBookAsync(FourthWingVolume, rig.Op(), cancel.Token));

        Assert.AreEqual(1, rig.Requests);
    }

    [TestMethod]
    public async Task ABodyThatFailsAfterTheHeadersIsATypedFailureAndTheStreamIsDisposed()
    {
        var stream = new ThrowingStream(() => new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely."));
        using var rig = Rig.With(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });

        var result = await rig.Provider.FindSeriesClaimsForBookAsync(FourthWingVolume, rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Failed, result.Outcome);
        CollectionAssert.AreEqual(new[] { DiscoveryCodes.ProviderError }, result.Notes.ToArray());
        Assert.IsTrue(stream.Disposed);
    }

    [TestMethod]
    public async Task AProgrammingErrorWhileReadingTheBodyIsNotSwallowed()
    {
        var stream = new ThrowingStream(() => new InvalidOperationException("bug"));
        using var rig = Rig.With(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            rig.Provider.FindSeriesClaimsForBookAsync(FourthWingVolume, rig.Op(), CancellationToken.None));
    }

    [TestMethod]
    [DataRow("not json")]
    [DataRow("null")]
    [DataRow("[1,2]")]
    public async Task AnUnreadableBodyIsAFailureNotAnEmptyScope(string body)
    {
        using var rig = Rig.With(Json(body));

        var result = await rig.Provider.FindSeriesClaimsForBookAsync(FourthWingVolume, rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Failed, result.Outcome);
        Assert.AreEqual(1, rig.Requests);
    }

    [TestMethod]
    public async Task AnOversizedBodyIsRefusedWhetherOrNotItAnnouncesItsLength()
    {
        var huge = "{\"id\":\"E-OLEAAAQBAJ\",\"pad\":\"" + new string('x', 3 * 1024 * 1024) + "\"}";
        using var announced = Rig.With(Json(huge));
        using var streamed = Rig.With(new HttpResponseMessage(HttpStatusCode.OK)
        {
            // A seekable stream would announce its length; a forward-only one is the case with no Content-Length header.
            Content = new StreamContent(new ForwardOnlyStream(new MemoryStream(Encoding.UTF8.GetBytes(huge))))
        });

        var first = await announced.Provider.FindSeriesClaimsForBookAsync(FourthWingVolume, announced.Op(), CancellationToken.None);
        var second = await streamed.Provider.FindSeriesClaimsForBookAsync(FourthWingVolume, streamed.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Failed, first.Outcome);
        Assert.AreEqual(DiscoveryOutcome.Failed, second.Outcome);
        CollectionAssert.Contains(first.Notes.ToArray(), "response-too-large");
        CollectionAssert.Contains(second.Notes.ToArray(), "response-too-large");
    }

    // ================================================================ the key never leaves the pipeline

    [TestMethod]
    public async Task NoResultCarriesTheKeyEvenWhenTheTransportFailureTextEchoesTheAddress()
    {
        using var failing = Rig.WithHandler((request, _) =>
            throw new HttpRequestException("connection failed for " + request.RequestUri));
        using var ok = Rig.With(Json(CapturedFourthWing));
        using var denied = Rig.With(Plain(HttpStatusCode.Forbidden));

        var results = new[]
        {
            await failing.Provider.FindSeriesClaimsForBookAsync(FourthWingVolume, failing.Op(), CancellationToken.None),
            await ok.Provider.FindSeriesClaimsForBookAsync(FourthWingVolume, ok.Op(), CancellationToken.None),
            await denied.Provider.FindSeriesClaimsForBookAsync(FourthWingVolume, denied.Op(), CancellationToken.None)
        };

        Assert.AreEqual(DiscoveryOutcome.Failed, results[0].Outcome);
        StringAssert.Contains(failing.Uris[0], Key, "The scripted transport did receive the key, so this test can detect a leak.");
        foreach (var result in results)
        {
            var visible = string.Join('|', result.Notes)
                + result.Scope.Filter + result.Scope.Subject?.ExternalId
                + string.Join('|', result.Items.Select(item => item.Series.Label + item.Series.Id?.ExternalId + item.Position?.Label));
            Assert.DoesNotContain(Key, visible);
            Assert.DoesNotContain("key=", visible);
            Assert.DoesNotContain("http", visible);
        }
    }

    // ================================================================ ordinary search/detail is unchanged

    [TestMethod]
    public async Task OrdinaryDetailNeverRequestsSeriesInfoAndKeepsItsMetadataAndAnEmptySeriesList()
    {
        using var rig = Rig.With(Json(
            """{"id":"v1","volumeInfo":{"title":"Fourth Wing","authors":["Rebecca Yarros"],"publisher":"Entangled Publishing, LLC","publishedDate":"2023","language":"en","seriesInfo":{"bookDisplayNumber":"1","volumeSeries":[{"seriesId":"sA","orderNumber":1}]}}}"""));

        var candidate = await rig.Provider.GetDetailsAsync("v1", CancellationToken.None);

        Assert.IsNotNull(candidate);
        Assert.IsEmpty(candidate.Series, "seriesInfo names no series, so no ordinary series can be written from it.");
        Assert.AreEqual("Fourth Wing", candidate.Title);
        Assert.AreEqual("Rebecca Yarros", candidate.Authors.Single());
        Assert.IsEmpty(candidate.AuthorCredits ?? []);
        Assert.AreEqual("Entangled Publishing, LLC", candidate.Publisher);
        Assert.AreEqual("en", candidate.Language);
        Assert.AreEqual(new DateOnly(2023, 1, 1), candidate.PublicationDate);
        Assert.AreEqual(DatePrecision.Year, candidate.PublicationDatePrecision);
        Assert.AreEqual(DateBasis.EditionDerived, candidate.PublicationDateBasis);
        Assert.AreEqual(1, rig.Requests);
        Assert.DoesNotContain("includeNonComicsSeries", rig.Uris[0]);
        StringAssert.Contains(rig.Uris[0], "projection=full");
    }

    [TestMethod]
    public async Task OrdinarySearchStillRetriesThroughItsOwnPolicyWithoutABudget()
    {
        // No budget on the request: the handler's historical two-retry policy still applies to ordinary traffic.
        using var rig = Rig.With(
            Plain(HttpStatusCode.ServiceUnavailable), Plain(HttpStatusCode.ServiceUnavailable), Json("""{"items":[]}"""));

        var page = await rig.Provider.SearchAsync(new BookSearchQuery("Dune"), CancellationToken.None);

        Assert.IsEmpty(page.Candidates);
        Assert.AreEqual(3, rig.Requests);
    }

    // ================================================================ helpers

    private static void AssertUnsupported<T>(DiscoveryResult<T> result, string reason)
    {
        Assert.AreEqual(DiscoveryOutcome.Unsupported, result.Outcome);
        Assert.IsEmpty(result.Items);
        Assert.AreEqual(DiscoveryCompletion.NotChecked, result.Completion);
        CollectionAssert.AreEqual(new[] { reason }, result.Notes.ToArray());
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Plain(HttpStatusCode status, int? retryAfterSeconds = null)
    {
        var response = new HttpResponseMessage(status);
        if (retryAfterSeconds is { } seconds)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
        }

        return response;
    }

    private sealed class ForwardOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class ThrowingStream(Func<Exception> failure) : Stream
    {
        public bool Disposed { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw failure();

        public override int Read(byte[] buffer, int offset, int count) => throw failure();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    // The production key handler and retry handler over a scripted transport. Retry delays are a few milliseconds so tests do
    // not wait the real 300 ms / 900 ms; the handler itself is the real one.
    private sealed class Rig : IDisposable
    {
        private readonly HttpClient client;
        private int requests;

        private Rig(string? apiKey, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            var retry = new GoogleBooksTransientRetryHandler([TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(10)])
            {
                InnerHandler = new Transport((request, token) =>
                {
                    Interlocked.Increment(ref requests);
                    lock (Uris)
                    {
                        Uris.Add(request.RequestUri!.PathAndQuery);
                        AllGet &= request.Method == HttpMethod.Get;
                    }

                    return handler(request, token);
                })
            };
            client = new HttpClient(new GoogleBooksApiKeyHandler(new Credentials(apiKey)) { InnerHandler = retry })
            {
                BaseAddress = new Uri("https://www.googleapis.com/books/v1/")
            };
            Provider = new GoogleBooksBookMetadataProvider(
                client,
                Options.Create(new GoogleBooksMetadataOptions { Enabled = true, ApiKey = Key, MaxResults = 10, TimeoutSeconds = 15 }),
                Clock);
        }

        public GoogleBooksBookMetadataProvider Provider { get; }

        public ManualClock Clock { get; } = new();

        public List<string> Uris { get; } = [];

        public bool AllGet { get; private set; } = true;

        public int Requests => Volatile.Read(ref requests);

        public static Rig With(params HttpResponseMessage[] responses) => With(Key, responses);

        public static Rig With(string? apiKey, params HttpResponseMessage[] responses)
        {
            var queue = new Queue<HttpResponseMessage>(responses);
            return new Rig(apiKey, (_, _) =>
            {
                lock (queue)
                {
                    return Task.FromResult(queue.Count > 0 ? queue.Dequeue() : new HttpResponseMessage(HttpStatusCode.InternalServerError));
                }
            });
        }

        public static Rig WithHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) => new(Key, handler);

        public DiscoveryOperationBudget Op(DiscoveryLimits? limits = null)
        {
            var journey = new DiscoveryJourney(Guid.NewGuid(), limits ?? DiscoveryLimits.Default, Clock);
            return journey.BeginOperation(DiscoveryOperation.FindSeriesForBook, "googlebooks", null)!;
        }

        public void Dispose() => client.Dispose();

        private sealed class Credentials(string? apiKey) : IMetadataCredentialAccessor
        {
            public Task<string?> GetCredentialAsync(string providerId, CancellationToken cancellationToken) => Task.FromResult(apiKey);
        }

        private sealed class Transport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                send(request, cancellationToken);
        }
    }
}
