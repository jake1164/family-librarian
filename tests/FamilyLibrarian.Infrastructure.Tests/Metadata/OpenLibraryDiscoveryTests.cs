#pragma warning disable CA1861
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Catalog.Discovery;
using FamilyLibrarian.Domain.Catalog;
using FamilyLibrarian.Infrastructure.Metadata;
using FamilyLibrarian.Infrastructure.Tests.Catalog;
using Microsoft.Extensions.Options;

namespace FamilyLibrarian.Infrastructure.Tests.Metadata;

/// <summary>
/// Open Library discovery through the real handler chain (the production request gate) over a scripted transport, so physical
/// attempts are counted where they happen.
/// <para>
/// Fixture provenance, stated plainly. <b>Captured</b> = a live unauthenticated public GET made on 2026-11-17 (one logical
/// request each, paced, no credentials), kept verbatim apart from trimming the rows and fields the adapter does not read:
/// <see cref="CapturedEmpyreanSeries"/>, <see cref="CapturedDuneAuthorPage"/>, <see cref="CapturedFrankHerbertAuthorSearch"/>,
/// <see cref="CapturedDuneAnchorLookup"/>, <see cref="CapturedDuneAuthorRecord"/>. <b>Hand-built</b> = constructed from the
/// documented/observed shape to exercise a case no sampled response contained (a second exact-name "Frank Herbert", same-named
/// series, mismatched arrays, ranges, errors); none of those is a provider response. Only first-party documentation and these
/// samples support the shapes; the seeds route is not used.
/// </para>
/// </summary>
[TestClass]
public sealed class OpenLibraryDiscoveryTests
{
    private static readonly SourceRef Dune = new("openlibrary", "OL893414W", ExternalReferenceEntityType.Work);
    private static readonly SourceRef Empyrean = new("openlibrary", "OL331363L", ExternalReferenceEntityType.Series);
    private static readonly SourceRef HerbertNovelist = new("openlibrary", "OL79034A", ExternalReferenceEntityType.Author);
    private static readonly SourceRef Yarros = new("openlibrary", "OL7825177A", ExternalReferenceEntityType.Author);

    // ---- captured (2026-11-17), trimmed ----

    private const string CapturedEmpyreanSeries =
        """{"numFound":3,"start":0,"numFoundExact":true,"docs":[{"author_key":["OL7825177A"],"key":"/works/OL29226517W","series_key":["OL331363L"],"series_name":["The Empyrean"],"series_position":["1"],"title":"Fourth Wing"},{"author_key":["OL7825177A"],"key":"/works/OL34028551W","series_key":["OL331363L"],"series_name":["The Empyrean"],"series_position":["2"],"title":"Iron Flame"},{"author_key":["OL7825177A"],"key":"/works/OL41943074W","series_key":["OL331363L"],"series_name":["The Empyrean"],"series_position":["3"],"title":"Onyx Storm"}]}""";

    // numFound 145 was the real total; the request that produced this asked for 4 rows.
    private const string CapturedDuneAuthorPage =
        """{"numFound":145,"start":0,"numFoundExact":true,"docs":[{"author_key":["OL79034A"],"key":"/works/OL893414W","series_key":["OL331367L"],"series_name":["Dune"],"series_position":["1"],"title":"Dune"},{"author_key":["OL79034A"],"key":"/works/OL893461W","series_key":["OL331367L"],"series_name":["Dune"],"series_position":["2"],"title":"Dune Messiah"},{"author_key":["OL79034A"],"key":"/works/OL893516W","series_key":["OL331367L"],"series_name":["Dune"],"series_position":["3"],"title":"Children of Dune"},{"author_key":["OL79034A"],"key":"/works/OL893515W","series_key":["OL331367L"],"series_name":["Dune"],"series_position":["4"],"title":"God Emperor of Dune"}]}""";

    private const string CapturedFrankHerbertAuthorSearch =
        """{"numFound":101,"start":0,"numFoundExact":true,"docs":[{"key":"OL1758387A","name":"Frank Herbert Hayward","birth_date":"1872","death_date":"16.02.1954","work_count":21},{"key":"OL1861350A","name":"Simonds, Frank Herbert","birth_date":"1878","death_date":"1936","work_count":19},{"key":"OL79034A","name":"Frank Herbert","birth_date":"8 October 1920","death_date":"11 February 1986","work_count":141},{"key":"OL2520778A","name":"Frank H. Sweet","birth_date":"1856","death_date":"1930","work_count":4},{"key":"OL2593932A","name":"Frank Herbert MacPherson","birth_date":"1861","work_count":2}]}""";

    private const string CapturedDuneAnchorLookup =
        """{"numFound":1,"start":0,"numFoundExact":true,"docs":[{"author_key":["OL79034A"],"author_name":["Frank Herbert"],"key":"/works/OL893414W","title":"Dune"}]}""";

    private const string CapturedDuneAuthorRecord =
        """{"key":"/authors/OL79034A","name":"Frank Herbert","birth_date":"8 October 1920","death_date":"11 February 1986","type":{"key":"/type/author"}}""";

    // ---- hand-built: two people both named exactly "Frank Herbert" (the real sample has one exact match; this is the case that
    // makes work counts and result order tempting evidence). 21 vs 141 works, namesake first. ----

    private const string TwoExactFrankHerberts =
        """{"numFound":2,"start":0,"docs":[{"key":"OL1758387A","name":"Frank Herbert","birth_date":"1872","work_count":141},{"key":"OL79034A","name":"Frank Herbert","birth_date":"8 October 1920","work_count":21}]}""";

    private const string AnchorBookWrittenByTheSmallerCatalogue =
        """{"numFound":1,"docs":[{"author_key":["OL1758387A"],"author_name":["Frank Herbert"],"key":"/works/OL893414W"}]}""";

    // ================================================================ alternate titles (A6)

    [TestMethod]
    public async Task AlternateTitlesAreKeptAsWorkScopedEvidenceWithTheirSourceFieldAndNeverMergedIntoTheReportedTitle()
    {
        // hand-built: no live response has returned these fields (22 sampled rows, 2026-11-17)
        using var rig = Rig.With(Json(
            """{"numFound":1,"docs":[{"key":"/works/OL1W","title":"Dune","alternative_title":["Frank Herbert's Dune","Dune","  Duna  ",""],"alternative_subtitle":["Book One"],"series_key":["OL10L"],"series_name":["Dune"],"series_position":["1"]}]}"""));

        var claims = await rig.Provider.GetSeriesMembersAsync(new SeriesRef("Dune", new SourceRef("openlibrary", "OL10L", ExternalReferenceEntityType.Series)), null, rig.Op(), CancellationToken.None);

        var title = claims.Items.Single().Book.Title;
        Assert.AreEqual("Dune", title.Reported);
        Assert.IsFalse(title.WasNormalized, "Alternates never change the reported title.");
        CollectionAssert.AreEqual(new[] { "Frank Herbert's Dune", "Duna", "Book One" }, title.Alternates.Select(alternate => alternate.Text).ToArray(),
            "Source order, trimmed, no repeat of the reported title, no empties.");
        CollectionAssert.AreEqual(new[] { "alternative_title", "alternative_title", "alternative_subtitle" }, title.Alternates.Select(alternate => alternate.SourceField).ToArray());
        Assert.IsTrue(title.Alternates.All(alternate => alternate.Scope == TitleScope.Work && alternate.Language is null),
            "A Work row states no language for an alternate, and none is guessed; a translation is never folded in.");
        StringAssert.Contains(rig.Uris[0], "alternative_title");
    }

    [TestMethod]
    public async Task ARowWithNoAlternateFieldsHasNoAlternatesAndTitleFormsCompareByContent()
    {
        using var rig = Rig.With(Json("""{"numFound":1,"docs":[{"key":"/works/OL1W","title":"Dune","series_key":["OL10L"],"series_name":["Dune"],"series_position":["1"]}]}"""));

        var result = await rig.Provider.GetSeriesMembersAsync(new SeriesRef("Dune", new SourceRef("openlibrary", "OL10L", ExternalReferenceEntityType.Series)), null, rig.Op(), CancellationToken.None);

        Assert.IsEmpty(result.Items.Single().Book.Title.Alternates, "Absent fields say nothing about whether alternates exist.");
        var a = new TitleForm("T") { Alternates = [new AlternateTitle("U", "alternative_title", null, TitleScope.Work)] };
        var b = new TitleForm("T") { Alternates = [new AlternateTitle("U", "alternative_title", null, TitleScope.Work)] };
        Assert.AreEqual(a, b);
        Assert.AreNotEqual(a, new TitleForm("T"));
        Assert.AreEqual(new TitleForm("T"), new TitleForm("T"));
    }

    // ================================================================ shared 429 cooldown (A6)

    private static readonly SourceRef CooldownBook = new("openlibrary", "OL1W", ExternalReferenceEntityType.Work);

    [TestMethod]
    public async Task A429IsRememberedByEveryCallerOfTheSharedInstanceAndNothingIsSentUntilTheCooldownEnds()
    {
        using var rig = Rig.With(Plain(HttpStatusCode.TooManyRequests, retryAfterSeconds: 120), Json("""{"numFound":0,"docs":[]}"""), Json("""{"numFound":0,"docs":[]}"""));

        var first = await rig.Provider.FindSeriesForBookAsync(CooldownBook, rig.Op(), CancellationToken.None);
        var discovery = await rig.Provider.GetSeriesMembersAsync(new SeriesRef("S", Empyrean), null, rig.Op(), CancellationToken.None);
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => rig.Provider.SearchAsync(new BookSearchQuery("Dune"), CancellationToken.None));

        Assert.AreEqual(DiscoveryOutcome.Throttled, first.Outcome);
        Assert.AreEqual(DiscoveryOutcome.Throttled, discovery.Outcome, "A different discovery operation is refused from memory.");
        Assert.AreEqual(1, rig.Requests, "Only the request that was throttled reached Open Library; ordinary search and discovery made none.");

        rig.Clock.Advance(TimeSpan.FromSeconds(119));
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => rig.Provider.SearchAsync(new BookSearchQuery("Dune"), CancellationToken.None));
        Assert.AreEqual(1, rig.Requests, "One second before the stated Retry-After, still nothing is sent.");

        rig.Clock.Advance(TimeSpan.FromSeconds(2));
        var after = await rig.Provider.FindSeriesForBookAsync(CooldownBook, rig.Op(), CancellationToken.None);
        Assert.AreEqual(DiscoveryOutcome.Empty, after.Outcome);
        Assert.AreEqual(2, rig.Requests, "After the cooldown the next request is sent normally.");
    }

    [TestMethod]
    public async Task A429WithoutAUsableRetryAfterUsesTheApplicationDefault()
    {
        // Absent, malformed (neither delay-seconds nor a date), zero and already-past values carry no usable wait.
        var past = new ManualClock().GetUtcNow() - TimeSpan.FromHours(1);
        var cases = new Action<HttpResponseMessage>?[]
        {
            null,
            r => r.Headers.TryAddWithoutValidation("Retry-After", "soon"),
            r => r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero),
            r => r.Headers.RetryAfter = new RetryConditionHeaderValue(past),
        };
        foreach (var header in cases)
        {
            var response = Plain(HttpStatusCode.TooManyRequests);
            header?.Invoke(response);
            using var rig = Rig.With(response, Json("""{"numFound":0,"docs":[]}"""));

            await rig.Provider.FindSeriesForBookAsync(CooldownBook, rig.Op(), CancellationToken.None);
            rig.Clock.Advance(OpenLibraryRequestGate.DefaultCooldown - TimeSpan.FromSeconds(1));
            var stillCooling = await rig.Provider.FindSeriesForBookAsync(CooldownBook, rig.Op(), CancellationToken.None);
            rig.Clock.Advance(TimeSpan.FromSeconds(2));
            var reopened = await rig.Provider.FindSeriesForBookAsync(CooldownBook, rig.Op(), CancellationToken.None);

            Assert.AreEqual(DiscoveryOutcome.Throttled, stillCooling.Outcome);
            Assert.AreEqual(DiscoveryOutcome.Empty, reopened.Outcome);
            Assert.AreEqual(2, rig.Requests);
        }
    }

    [TestMethod]
    public async Task ALongRetryAfterIsHonouredInFullByLaterCallersWhateverEachOperationIsWillingToWaitFor()
    {
        // Delay-seconds and HTTP-date forms; each keeps the source closed past the old ten-minute ceiling and for the full wait.
        var start = new ManualClock().GetUtcNow();
        var cases = new Action<HttpResponseMessage>[]
        {
            r => r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromDays(1)),
            r => r.Headers.RetryAfter = new RetryConditionHeaderValue(start + TimeSpan.FromDays(1)),
        };
        foreach (var header in cases)
        {
            var response = Plain(HttpStatusCode.TooManyRequests);
            header(response);
            using var rig = Rig.With(response, Json("""{"numFound":0,"docs":[]}"""));

            var first = await rig.Provider.FindSeriesForBookAsync(CooldownBook, rig.Op(), CancellationToken.None);
            Assert.AreEqual(DiscoveryOutcome.Throttled, first.Outcome);
            CollectionAssert.Contains(first.Notes.ToArray(), DiscoveryCodes.RetryAfterTooLong, "A wait that does not fit the operation is reported as such.");

            foreach (var elapsed in new[] { TimeSpan.FromMinutes(11), TimeSpan.FromHours(23) })
            {
                rig.Clock.Advance(elapsed - (rig.Clock.GetUtcNow() - start));
                var later = await rig.Provider.FindSeriesForBookAsync(CooldownBook, rig.Op(), CancellationToken.None);
                Assert.AreEqual(DiscoveryOutcome.Throttled, later.Outcome, $"Still closed {elapsed} into a one-day Retry-After.");
                await Assert.ThrowsExactlyAsync<HttpRequestException>(() => rig.Provider.SearchAsync(new BookSearchQuery("Dune"), CancellationToken.None));
                Assert.AreEqual(1, rig.Requests, "Nothing is sent while the stated wait is in force.");
            }

            rig.Clock.Advance(TimeSpan.FromDays(1) - (rig.Clock.GetUtcNow() - start) + TimeSpan.FromSeconds(1));
            var reopened = await rig.Provider.FindSeriesForBookAsync(CooldownBook, rig.Op(), CancellationToken.None);
            Assert.AreEqual(DiscoveryOutcome.Empty, reopened.Outcome, "The source reopens once the stated wait has passed.");
            Assert.AreEqual(2, rig.Requests);
        }
    }

    [TestMethod]
    public void AShorterLaterRetryAfterNeverShortensACooldownAndAnAbsurdOneDoesNotOverflow()
    {
        var clock = new ManualClock();
        using var gate = new OpenLibraryRequestGate(Options.Create(new OpenLibraryMetadataOptions { Enabled = true, RequestsPerSecond = 1000 }), clock);

        // A request already in flight when the first 429 arrived can itself be answered 429 with less.
        gate.NoteThrottled(TimeSpan.FromHours(1));
        gate.NoteThrottled(TimeSpan.FromSeconds(5));
        gate.NoteThrottled((TimeSpan?)null);
        Assert.AreEqual(TimeSpan.FromHours(1), gate.RemainingCooldown(), "Neither a short header nor the default shortens a longer cooldown.");

        gate.NoteThrottled(TimeSpan.MaxValue);
        Assert.IsTrue(gate.RemainingCooldown() > TimeSpan.FromDays(365 * 1000), "An unrepresentable wait fails closed.");
        gate.NoteThrottled(TimeSpan.FromSeconds(5));
        Assert.IsTrue(gate.RemainingCooldown() > TimeSpan.FromDays(365 * 1000), "Nor does a later short wait reopen it.");
    }

    [TestMethod]
    public async Task ARefusalReportsTheRemainingWaitAndAnAbsurdWaitAsTheLargestDelayTheHeaderCanCarry()
    {
        var clock = new ManualClock();
        using var gate = new OpenLibraryRequestGate(Options.Create(new OpenLibraryMetadataOptions { Enabled = true, RequestsPerSecond = 1000 }), clock);
        using var handler = new OpenLibraryRateLimitHandler(gate) { InnerHandler = new NeverSent() };
        using var invoker = new HttpMessageInvoker(handler);

        gate.NoteThrottled(TimeSpan.FromSeconds(120));
        clock.Advance(TimeSpan.FromMilliseconds(500));
        var refused = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://openlibrary.org/x"), CancellationToken.None);
        Assert.AreEqual(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.AreEqual(TimeSpan.FromSeconds(120), refused.Headers.RetryAfter?.Delta, "119.5 s remain, reported rounded up.");

        gate.NoteThrottled(TimeSpan.FromDays(365 * 5_000));
        var absurd = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://openlibrary.org/x"), CancellationToken.None);
        Assert.AreEqual(TimeSpan.FromSeconds(int.MaxValue), absurd.Headers.RetryAfter?.Delta);
    }

    // ---- queued requests re-check the cooldown after pacing (G1) ----

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var until = Environment.TickCount64 + 10_000;
        while (!condition())
        {
            if (Environment.TickCount64 > until) Assert.Fail($"Timed out waiting for {what}.");
            await Task.Delay(5);
        }
    }

    private static async Task<string> CallAsync(Rig rig, bool discovery, CancellationToken cancellationToken = default)
    {
        if (discovery)
        {
            var result = await rig.Provider.FindSeriesForBookAsync(CooldownBook, rig.Op(), cancellationToken);
            return result.Outcome == DiscoveryOutcome.Throttled ? "throttled" : result.Outcome.ToString();
        }

        try
        {
            await rig.Provider.SearchAsync(new BookSearchQuery("Dune"), cancellationToken);
            return "sent";
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return "throttled";
        }
    }

    [TestMethod]
    public async Task ARequestQueuedBehindPacingIsRefusedWhenTheRequestAheadOfItIsAnswered429()
    {
        // A is in flight; B waits on the one-second pace; A returns 429 Retry-After 120; B's pace then elapses. B must not be sent.
        foreach (var (firstIsDiscovery, secondIsDiscovery) in new[] { (true, true), (false, true), (true, false), (false, false) })
        {
            var clock = new SteppedClock();
            var inFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            using var rig = Rig.Paced(clock, async (_, ct) =>
            {
                if (Interlocked.Increment(ref calls) > 1) return Json("""{"numFound":0,"docs":[]}""");
                inFlight.SetResult();
                await release.Task.WaitAsync(ct);
                return Plain(HttpStatusCode.TooManyRequests, retryAfterSeconds: 120);
            });

            var a = CallAsync(rig, firstIsDiscovery);
            await inFlight.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var b = CallAsync(rig, secondIsDiscovery);
            await WaitUntilAsync(() => clock.PendingTimers == 1, "the second request to wait on the pace");

            release.SetResult();
            Assert.AreEqual("throttled", await a);
            clock.Advance(TimeSpan.FromSeconds(1));
            var second = await b;

            Assert.AreEqual(1, rig.Requests, $"The queued request was sent after the 429 (first discovery={firstIsDiscovery}, second discovery={secondIsDiscovery}).");
            Assert.AreEqual("throttled", second, "The queued request is refused locally as throttled.");
        }
    }

    [TestMethod]
    public async Task ACancelledQueuedRequestStopsAtThePacingWaitAndIsNeverSent()
    {
        var clock = new SteppedClock();
        var inFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var rig = Rig.Paced(clock, async (_, ct) =>
        {
            inFlight.TrySetResult();
            await release.Task.WaitAsync(ct);
            return Json("""{"numFound":0,"docs":[]}""");
        });

        var a = CallAsync(rig, discovery: true);
        await inFlight.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var cancel = new CancellationTokenSource();
        var b = CallAsync(rig, discovery: true, cancel.Token);
        await WaitUntilAsync(() => clock.PendingTimers == 1, "the second request to wait on the pace");
        await cancel.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await b);
        release.SetResult();
        await a;
        Assert.AreEqual(1, rig.Requests, "The cancelled request was never sent.");
    }

    private sealed class NeverSent : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A refused request must not reach the transport.");
    }

    [TestMethod]
    public async Task ARefusedCooldownRequestSaysItDoesNotFitTheOperationAndSendsNothing()
    {
        using var rig = Rig.With(Plain(HttpStatusCode.TooManyRequests, retryAfterSeconds: 300));

        await rig.Provider.FindSeriesForBookAsync(CooldownBook, rig.Op(), CancellationToken.None);
        var refused = await rig.Provider.FindSeriesForBookAsync(CooldownBook, rig.Op(new DiscoveryLimits { OperationMaxWork = TimeSpan.FromSeconds(15) }), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Throttled, refused.Outcome);
        CollectionAssert.Contains(refused.Notes.ToArray(), DiscoveryCodes.RetryAfterTooLong, "The remaining cooldown does not fit the 15 s operation, and is said so.");
        Assert.AreEqual(1, rig.Requests);
    }

    // ================================================================ book -> series

    // ---- shared series-claims operation (A5a) ----

    private const string WorkWithTwoNativeSeriesAndAnUnboundOne =
        """{"numFound":1,"start":0,"docs":[{"key":"/works/OL1W","title":"Legacy Book","series_key":["OL10L","OL11L","OL12L"],"series_name":["Legacy","Legacy","Cycle"],"series_position":["1","2.5","three"]}]}""";

    [TestMethod]
    public async Task TheSharedClaimsOperationCarriesEachNativeSeriesWithItsOwnPositionFromOneRequest()
    {
        using var rig = Rig.With(Json(WorkWithTwoNativeSeriesAndAnUnboundOne));
        var shared = Assert.IsInstanceOfType<IBookMetadataDiscoveryProvider>(rig.Provider);

        var result = await shared.FindSeriesClaimsForBookAsync(
            new SourceRef("openlibrary", "OL1W", ExternalReferenceEntityType.Work), rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Ok, result.Outcome);
        Assert.HasCount(3, result.Items);
        Assert.AreEqual("OL10L", result.Items[0].Series.Id!.ExternalId);
        Assert.AreEqual("1", result.Items[0].Position!.Label);
        Assert.AreEqual(1m, result.Items[0].Position!.Sort);
        Assert.AreEqual("OL11L", result.Items[1].Series.Id!.ExternalId, "Same-named series under distinct keys stay distinct.");
        Assert.AreEqual(2.5m, result.Items[1].Position!.Sort);
        Assert.AreEqual("three", result.Items[2].Position!.Label);
        Assert.IsNull(result.Items[2].Position!.Sort, "A non-numeric position keeps its label and no sort value.");
        Assert.AreEqual(result.Items[1].Series.Id, result.Items[1].Position!.Series);
        Assert.AreEqual(1, rig.Requests);
        Assert.AreEqual(1, result.Usage.Attempts);
    }

    [TestMethod]
    public async Task TheSeriesOnlyViewIsTheSameSingleReadAndKeepsTheNotes()
    {
        using var claimsRig = Rig.With(Json(WorkWithTwoNativeSeriesAndAnUnboundOne));
        using var seriesRig = Rig.With(Json(WorkWithTwoNativeSeriesAndAnUnboundOne));
        var book = new SourceRef("openlibrary", "OL1W", ExternalReferenceEntityType.Work);

        var claims = await ((IBookMetadataDiscoveryProvider)claimsRig.Provider).FindSeriesClaimsForBookAsync(book, claimsRig.Op(), CancellationToken.None);
        var series = await ((IBookMetadataDiscoveryProvider)seriesRig.Provider).FindSeriesForBookAsync(book, seriesRig.Op(), CancellationToken.None);

        Assert.AreEqual(claimsRig.Requests, seriesRig.Requests);
        Assert.HasCount(3, series.Items);
        CollectionAssert.AreEqual(claims.Items.Select(claim => claim.Series).ToArray(), series.Items.ToArray());
        CollectionAssert.AreEqual(claims.Notes.ToArray(), series.Notes.ToArray());
        Assert.AreEqual(claims.Scope, series.Scope);
    }

    [TestMethod]
    public async Task UnusableAndLabelOnlyClaimsKeepTheirStateThroughBothViews()
    {
        using var mismatch = Rig.With(Json(
            """{"numFound":1,"docs":[{"key":"/works/OL1W","title":"X","series_key":["OL10L","OL11L"],"series_name":["Only One"],"series_position":["1","2"]}]}"""));
        using var labelOnly = Rig.With(Json(
            """{"numFound":1,"docs":[{"key":"/works/OL1W","title":"X","series_name":["Mystery Set"],"series_position":["2"]}]}"""));
        var book = new SourceRef("openlibrary", "OL1W", ExternalReferenceEntityType.Work);

        var unusable = await ((IBookMetadataDiscoveryProvider)mismatch.Provider).FindSeriesClaimsForBookAsync(book, mismatch.Op(), CancellationToken.None);
        var unbound = await ((IBookMetadataDiscoveryProvider)labelOnly.Provider).FindSeriesClaimsForBookAsync(book, labelOnly.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.NoUsableItems, unusable.Outcome);
        CollectionAssert.Contains(unusable.Notes.ToArray(), "series-arrays-mismatch");
        Assert.IsFalse(unbound.Items.Single().Series.IsBound);
        Assert.AreEqual("2", unbound.Items.Single().Position!.Label);
        Assert.IsNull(unbound.Items.Single().Position!.Series, "A label-only claim never gains a native series reference.");
        CollectionAssert.Contains(unbound.Notes.ToArray(), DiscoveryCodes.NoNativeSeriesId);
    }

    [TestMethod]
    public async Task ABooksSeriesComeBackAsNativeKeysFromOneRequest()
    {
        using var rig = Rig.With(Json(
            """{"numFound":1,"start":0,"docs":[{"key":"/works/OL29226517W","title":"Fourth Wing","author_key":["OL7825177A"],"series_key":["OL331363L"],"series_name":["The Empyrean"],"series_position":["1"]}]}"""));

        var result = await rig.Provider.FindSeriesForBookAsync(
            new SourceRef("openlibrary", "OL29226517W", ExternalReferenceEntityType.Work), rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Ok, result.Outcome);
        var series = result.Items.Single();
        Assert.AreEqual("The Empyrean", series.Label);
        Assert.AreEqual(Empyrean, series.Id);
        Assert.AreEqual(1, rig.Requests, "A series lookup for one book is one physical request.");
        Assert.AreEqual(1, result.Usage.Attempts);
        Assert.AreEqual(0, result.Usage.Retries);
        StringAssert.Contains(rig.Uris[0], "q=key:/works/OL29226517W");
        Assert.IsTrue(rig.AllGet, "Discovery only reads.");
    }

    [TestMethod]
    public async Task SameNamedSeriesWithDifferentKeysStayDistinctChoices()
    {
        using var rig = Rig.With(Json(
            """{"numFound":1,"docs":[{"key":"/works/OL1W","title":"Legacy Book","series_key":["OL10L","OL11L"],"series_name":["Legacy","Legacy"],"series_position":["1","2"]}]}"""));

        var result = await rig.Provider.FindSeriesForBookAsync(
            new SourceRef("openlibrary", "OL1W", ExternalReferenceEntityType.Work), rig.Op(), CancellationToken.None);

        Assert.HasCount(2, result.Items);
        Assert.AreEqual("OL10L", result.Items[0].Id!.ExternalId);
        Assert.AreEqual("OL11L", result.Items[1].Id!.ExternalId);
        Assert.AreEqual(result.Items[0].Label, result.Items[1].Label);
    }

    [TestMethod]
    public async Task ABookWithNoSeriesFieldsOrNoIndexRowIsEmptyInThatScopeOnly()
    {
        using var none = Rig.With(Json("""{"numFound":1,"docs":[{"key":"/works/OL1W","title":"Standalone"}]}"""));
        using var absent = Rig.With(Json("""{"numFound":0,"docs":[]}"""));
        var book = new SourceRef("openlibrary", "OL1W", ExternalReferenceEntityType.Work);

        var noSeries = await none.Provider.FindSeriesForBookAsync(book, none.Op(), CancellationToken.None);
        var missing = await absent.Provider.FindSeriesForBookAsync(book, absent.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Empty, noSeries.Outcome);
        Assert.AreEqual(DiscoveryOutcome.Empty, missing.Outcome);
        Assert.AreEqual("series-claims-in-work-search-index", noSeries.Scope.Filter, "Empty names what was actually checked.");
    }

    [TestMethod]
    public async Task MismatchedSeriesArraysAreUnusableNeverGuessedIntoPairs()
    {
        // hand-built: two keys, one name
        using var rig = Rig.With(Json(
            """{"numFound":1,"docs":[{"key":"/works/OL1W","title":"X","series_key":["OL10L","OL11L"],"series_name":["Only One"],"series_position":["1","2"]}]}"""));

        var result = await rig.Provider.FindSeriesForBookAsync(
            new SourceRef("openlibrary", "OL1W", ExternalReferenceEntityType.Work), rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.NoUsableItems, result.Outcome);
        Assert.IsEmpty(result.Items);
        CollectionAssert.Contains(result.Notes.ToArray(), "series-arrays-mismatch");
    }

    [TestMethod]
    public async Task ASeriesNameWithoutAKeyIsKeptUnboundAndNeverGainsAnId()
    {
        using var rig = Rig.With(Json(
            """{"numFound":1,"docs":[{"key":"/works/OL1W","title":"X","series_name":["Mystery Set"],"series_position":["2"]}]}"""));

        var result = await rig.Provider.FindSeriesForBookAsync(
            new SourceRef("openlibrary", "OL1W", ExternalReferenceEntityType.Work), rig.Op(), CancellationToken.None);

        Assert.IsFalse(result.Items.Single().IsBound);
        Assert.IsNull(result.Items.Single().Id);
        CollectionAssert.Contains(result.Notes.ToArray(), DiscoveryCodes.NoNativeSeriesId);
    }

    [TestMethod]
    public async Task ARowForADifferentWorkIsNotReadAsThisWorksClaims()
    {
        using var rig = Rig.With(Json(
            """{"numFound":1,"docs":[{"key":"/works/OL999W","title":"Other","series_key":["OL10L"],"series_name":["S"]}]}"""));

        var result = await rig.Provider.FindSeriesForBookAsync(
            new SourceRef("openlibrary", "OL1W", ExternalReferenceEntityType.Work), rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.NoUsableItems, result.Outcome);
        CollectionAssert.Contains(result.Notes.ToArray(), "book-anchor-mismatch");
    }

    [TestMethod]
    [DataRow("hardcover", "OL1W", ExternalReferenceEntityType.Work)]
    [DataRow("openlibrary", "OL1W", ExternalReferenceEntityType.Edition)]
    [DataRow("openlibrary", "OL1M", ExternalReferenceEntityType.Work)]
    [DataRow("openlibrary", "OL1W/../x", ExternalReferenceEntityType.Work)]
    [DataRow("openlibrary", "OLW", ExternalReferenceEntityType.Work)]
    public async Task ASubjectThatIsNotAnOpenLibraryWorkKeyMakesNoRequest(string provider, string id, ExternalReferenceEntityType kind)
    {
        using var rig = Rig.With();

        var result = await rig.Provider.FindSeriesForBookAsync(new SourceRef(provider, id, kind), rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Failed, result.Outcome);
        CollectionAssert.Contains(result.Notes.ToArray(), DiscoveryCodes.SubjectInvalid);
        Assert.AreEqual(0, rig.Requests);
    }

    // ================================================================ series -> members

    [TestMethod]
    public async Task CapturedEmpyreanMembersCarryNativeSeriesPositionsAndOneRequest()
    {
        using var rig = Rig.With(Json(CapturedEmpyreanSeries));

        var result = await rig.Provider.GetSeriesMembersAsync(new SeriesRef("The Empyrean", Empyrean), null, rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Ok, result.Outcome);
        Assert.AreEqual(DiscoveryCompletion.ExhaustedInCheckedScope, result.Completion);
        Assert.AreEqual("Fourth Wing|Iron Flame|Onyx Storm", string.Join('|', result.Items.Select(member => member.Book.Title.Reported)));
        CollectionAssert.AreEqual(new decimal?[] { 1, 2, 3 }, result.Items.Select(member => member.Positions.Single().Sort).ToArray());
        Assert.IsTrue(result.Items.All(member => member.Series == Empyrean));
        // The captured sample was fetched without author_name, so a key has no label to be a credit with; no credit is invented.
        Assert.IsTrue(result.Items.All(member => member.Book.Credits.Count == 0));
        CollectionAssert.Contains(result.Notes.ToArray(), "credit-arrays-mismatch");
        Assert.AreEqual(MembershipRole.Unknown, result.Items[0].Role, "Open Library states no role; none is invented.");
        Assert.IsNull(result.Items[0].Book.Version, "Work class is not assessed from these fields.");
        Assert.AreEqual(1, rig.Requests);
        Assert.AreEqual(1, result.Usage.Pages);
        StringAssert.Contains(rig.Uris[0], "q=series_key:OL331363L");
        StringAssert.Contains(rig.Uris[0], "limit=50");
        StringAssert.Contains(rig.Uris[0], "page=1");
    }

    [TestMethod]
    public async Task APositionIsReadFromTheIndexOfTheRequestedSeriesNotTheFirstSeries()
    {
        using var rig = Rig.With(Json(
            """{"numFound":1,"start":0,"docs":[{"key":"/works/OL5W","title":"Crossover","series_key":["OL10L","OL11L"],"series_name":["Legacy","Legacy"],"series_position":["1","7"]}]}"""));

        var result = await rig.Provider.GetSeriesMembersAsync(
            new SeriesRef("Legacy", new SourceRef("openlibrary", "OL11L", ExternalReferenceEntityType.Series)), null, rig.Op(), CancellationToken.None);

        var position = result.Items.Single().Positions.Single();
        Assert.AreEqual("7", position.Label);
        Assert.AreEqual(7m, position.Sort);
        Assert.AreEqual("OL11L", position.Series!.ExternalId);
    }

    [TestMethod]
    public async Task ConflictingPositionsForTheSameSeriesAreBothKept()
    {
        using var rig = Rig.With(Json(
            """{"numFound":1,"start":0,"docs":[{"key":"/works/OL5W","title":"Twice","series_key":["OL10L","OL10L"],"series_name":["Dark Tower","Dark Tower"],"series_position":["4","4.5"]}]}"""));

        var result = await rig.Provider.GetSeriesMembersAsync(
            new SeriesRef("Dark Tower", new SourceRef("openlibrary", "OL10L", ExternalReferenceEntityType.Series)), null, rig.Op(), CancellationToken.None);

        CollectionAssert.AreEqual(new decimal?[] { 4m, 4.5m }, result.Items.Single().Positions.Select(claim => claim.Sort).ToArray());
    }

    [TestMethod]
    public async Task FractionalRangeAndWordPositionsKeepTheirLabelAndOnlyNumbersSort()
    {
        using var rig = Rig.With(Json(
            """{"numFound":3,"start":0,"docs":[{"key":"/works/OL1W","title":"A","series_key":["OL10L"],"series_name":["S"],"series_position":["3.5"]},{"key":"/works/OL2W","title":"B","series_key":["OL10L"],"series_name":["S"],"series_position":["1-7"]},{"key":"/works/OL3W","title":"C","series_key":["OL10L"],"series_name":["S"],"series_position":["prequel"]}]}"""));

        var result = await rig.Provider.GetSeriesMembersAsync(
            new SeriesRef("S", new SourceRef("openlibrary", "OL10L", ExternalReferenceEntityType.Series)), null, rig.Op(), CancellationToken.None);

        Assert.AreEqual("3.5|1-7|prequel", string.Join('|', result.Items.Select(member => member.Positions.Single().Label)));
        CollectionAssert.AreEqual(new decimal?[] { 3.5m, null, null }, result.Items.Select(member => member.Positions.Single().Sort).ToArray());
        Assert.IsTrue(result.Items.All(member => member.Positions.Single().Ordering is null), "No ordering is assumed.");
    }

    [TestMethod]
    public async Task AFilterThatDriftedLeavesOutRowsWithoutTheRequestedSeriesKey()
    {
        using var rig = Rig.With(Json(
            """{"numFound":2,"start":0,"docs":[{"key":"/works/OL1W","title":"Mine","series_key":["OL10L"],"series_name":["S"],"series_position":["1"]},{"key":"/works/OL2W","title":"Not mine","series_key":["OL99L"],"series_name":["Other"],"series_position":["1"]}]}"""));

        var result = await rig.Provider.GetSeriesMembersAsync(
            new SeriesRef("S", new SourceRef("openlibrary", "OL10L", ExternalReferenceEntityType.Series)), null, rig.Op(), CancellationToken.None);

        Assert.AreEqual("Mine", result.Items.Single().Book.Title.Reported);
        CollectionAssert.Contains(result.Notes.ToArray(), "series-anchor-mismatch");
    }

    [TestMethod]
    public async Task TheSeriesSuffixIsLiftedOnlyWhenTheRowsOwnSeriesClaimEvidencesIt()
    {
        using var rig = Rig.With(Json(
            """{"numFound":4,"start":0,"docs":[{"key":"/works/OL1W","title":"Wilder - The Renegades #1","series_key":["OL10L"],"series_name":["The Renegades"],"series_position":["1"]},{"key":"/works/OL2W","title":"Rebel - The Renegades #3","series_key":["OL10L"],"series_name":["The Renegades"],"series_position":["2"]},{"key":"/works/OL3W","title":"Ender's Game - Special Edition","series_key":["OL10L"],"series_name":["The Renegades"],"series_position":["3"]},{"key":"/works/OL4W","title":"Moon - Sun: A Tale","series_key":["OL10L"],"series_name":["The Renegades"]}]}"""));

        var result = await rig.Provider.GetSeriesMembersAsync(
            new SeriesRef("The Renegades", new SourceRef("openlibrary", "OL10L", ExternalReferenceEntityType.Series)), null, rig.Op(), CancellationToken.None);

        var titles = result.Items.Select(member => member.Book.Title).ToArray();
        Assert.AreEqual("Wilder", titles[0].Normalized);
        Assert.AreEqual("series-suffix", titles[0].RuleCode);
        Assert.AreEqual("Wilder - The Renegades #1", titles[0].Reported, "The reported title is never lost.");
        Assert.IsFalse(titles[1].WasNormalized, "#3 does not match its own claimed position 2, so nothing is evidenced.");
        Assert.AreEqual("Rebel - The Renegades #3", titles[1].Reported);
        Assert.IsFalse(titles[2].WasNormalized, "An arbitrary ' - ' subtitle is not stripped.");
        Assert.IsFalse(titles[3].WasNormalized);
    }

    [TestMethod]
    public async Task OnlyAYearIsReportedAndItIsNeverMistakenForADay()
    {
        using var rig = Rig.With(Json(
            """{"numFound":2,"start":0,"docs":[{"key":"/works/OL1W","title":"Dated","first_publish_year":2023,"series_key":["OL10L"],"series_name":["S"],"series_position":["1"]},{"key":"/works/OL2W","title":"Undated","series_key":["OL10L"],"series_name":["S"],"series_position":["2"]}]}"""));

        var result = await rig.Provider.GetSeriesMembersAsync(
            new SeriesRef("S", new SourceRef("openlibrary", "OL10L", ExternalReferenceEntityType.Series)), null, rig.Op(), CancellationToken.None);

        var dated = result.Items[0].Book.Publication;
        Assert.AreEqual(PublicationDateState.Dated, dated.State);
        Assert.AreEqual(new DateOnly(2023, 1, 1), dated.Date);
        Assert.AreEqual(DatePrecision.Year, dated.Precision);
        Assert.AreEqual(DateBasis.Original, dated.Basis);
        Assert.AreEqual(PublicationDateState.NotStated, result.Items[1].Book.Publication.State, "A missing date is not-stated, not 'no date'.");
    }

    [TestMethod]
    public async Task APageWithMoreWorksOffersAContinuationAndTheNextRequestAsksForThatPage()
    {
        using var rig = Rig.With(
            Json(PageOfWorks(count: 50, firstId: 1, total: 120, start: 0)),
            Json(PageOfWorks(count: 50, firstId: 51, total: 120, start: 50)),
            Json(PageOfWorks(count: 20, firstId: 101, total: 120, start: 100)));
        var series = new SeriesRef("Big", new SourceRef("openlibrary", "OL10L", ExternalReferenceEntityType.Series));
        var operation = rig.Op();

        var first = await rig.Provider.GetSeriesMembersAsync(series, null, operation, CancellationToken.None);
        var second = await rig.Provider.GetSeriesMembersAsync(series, first.Next, operation, CancellationToken.None);
        var third = await rig.Provider.GetSeriesMembersAsync(series, second.Next, operation, CancellationToken.None);

        Assert.AreEqual(DiscoveryCompletion.MoreAvailable, first.Completion);
        Assert.AreEqual("p:2", first.Next!.Value);
        Assert.AreEqual("p:3", second.Next!.Value);
        Assert.AreEqual(DiscoveryCompletion.ExhaustedInCheckedScope, third.Completion);
        Assert.IsNull(third.Next);
        StringAssert.Contains(rig.Uris[1], "page=2");
        StringAssert.Contains(rig.Uris[2], "page=3");
        Assert.AreEqual(3, rig.Requests);
        Assert.AreEqual(3, third.Usage.Attempts, "The operation's attempts accumulate across its continuations.");
        Assert.AreEqual(3, third.Usage.Pages);
    }

    [TestMethod]
    public async Task ADuplicateWorkOnOnePageIsDroppedAndSaidSo()
    {
        using var rig = Rig.With(Json(
            """{"numFound":2,"start":0,"docs":[{"key":"/works/OL1W","title":"A","series_key":["OL10L"],"series_name":["S"],"series_position":["1"]},{"key":"/works/OL1W","title":"A","series_key":["OL10L"],"series_name":["S"],"series_position":["1"]}]}"""));

        var result = await rig.Provider.GetSeriesMembersAsync(
            new SeriesRef("S", new SourceRef("openlibrary", "OL10L", ExternalReferenceEntityType.Series)), null, rig.Op(), CancellationToken.None);

        Assert.HasCount(1, result.Items);
        CollectionAssert.Contains(result.Notes.ToArray(), DiscoveryCodes.DuplicateDropped);
    }

    [TestMethod]
    public async Task AFullPageOfUnusableRowsIsNotEmptyAndStillOffersTheNextPage()
    {
        var rows = string.Join(',', Enumerable.Range(1, 50).Select(_ => """{"title":"No key"}"""));
        using var rig = Rig.With(Json($$"""{"numFound":120,"start":0,"docs":[{{rows}}]}"""));

        var result = await rig.Provider.GetSeriesMembersAsync(
            new SeriesRef("S", new SourceRef("openlibrary", "OL10L", ExternalReferenceEntityType.Series)), null, rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.NoUsableItems, result.Outcome);
        Assert.AreEqual(DiscoveryCompletion.MoreAvailable, result.Completion);
        Assert.AreEqual("p:2", result.Next!.Value);
        CollectionAssert.Contains(result.Notes.ToArray(), DiscoveryCodes.ItemMissingId);
    }

    [TestMethod]
    public async Task AnEmptyLaterPageMeansTheDataMovedAndIsNeverVerifiedEmpty()
    {
        using var rig = Rig.With(Json("""{"numFound":40,"start":50,"docs":[]}"""));

        var result = await rig.Provider.GetSeriesMembersAsync(
            new SeriesRef("S", new SourceRef("openlibrary", "OL10L", ExternalReferenceEntityType.Series)),
            new DiscoveryCursor("p:2"), rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.NoUsableItems, result.Outcome);
        CollectionAssert.Contains(result.Notes.ToArray(), DiscoveryCodes.ProviderMutatedBetweenPages);
    }

    [TestMethod]
    public async Task ASeriesKeyWithNoIndexedWorksIsEmptyOnlyForTheFirstPage()
    {
        using var rig = Rig.With(Json("""{"numFound":0,"start":0,"docs":[]}"""));

        var result = await rig.Provider.GetSeriesMembersAsync(
            new SeriesRef("S", new SourceRef("openlibrary", "OL10L", ExternalReferenceEntityType.Series)), null, rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Empty, result.Outcome);
        Assert.AreEqual("works-indexed-with-this-series-key", result.Scope.Filter);
    }

    [TestMethod]
    public async Task ALabelOnlySeriesAnotherProvidersIdOrABadCursorNeverReachTheNetwork()
    {
        using var rig = Rig.With();
        var operation = rig.Op();

        var labelOnly = await rig.Provider.GetSeriesMembersAsync(new SeriesRef("Legacy", null), null, operation, CancellationToken.None);
        var foreign = await rig.Provider.GetSeriesMembersAsync(
            new SeriesRef("Legacy", new SourceRef("hardcover", "OL10L", ExternalReferenceEntityType.Series)), null, operation, CancellationToken.None);
        var badCursor = await rig.Provider.GetSeriesMembersAsync(
            new SeriesRef("Legacy", new SourceRef("openlibrary", "OL10L", ExternalReferenceEntityType.Series)),
            new DiscoveryCursor("o:50"), operation, CancellationToken.None);

        CollectionAssert.Contains(labelOnly.Notes.ToArray(), DiscoveryCodes.SubjectInvalid);
        CollectionAssert.Contains(foreign.Notes.ToArray(), DiscoveryCodes.SubjectInvalid);
        CollectionAssert.Contains(badCursor.Notes.ToArray(), DiscoveryCodes.CursorInvalid);
        Assert.AreEqual(0, rig.Requests);
    }

    // ================================================================ author resolution (Frank Herbert)

    [TestMethod]
    public async Task TwoExactNamesWithDifferentWorkCountsAreChoicesWhenNothingAnchorsEither()
    {
        using var rig = Rig.With(Json(TwoExactFrankHerberts));

        var result = await rig.Provider.ResolveAuthorAsync(AuthorAnchor.FromNameOnly("Frank Herbert"), rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Ambiguous, result.Outcome);
        Assert.AreEqual("OL1758387A|OL79034A", string.Join('|', result.Items.Select(author => author.Id.ExternalId).Order(StringComparer.Ordinal)));
        CollectionAssert.Contains(result.Notes.ToArray(), DiscoveryCodes.AnchorInsufficient);
        Assert.AreEqual(141, result.Items.Single(author => author.Id.ExternalId == "OL1758387A").WorkCount, "Counts are shown, never used to choose.");
        Assert.AreEqual(1, rig.Requests);
    }

    [TestMethod]
    public async Task AnAnchorBookBeatsBothResultOrderAndWorkCount()
    {
        // The anchor book is credited to OL79034A... in the captured lookup. Here the hand-built search puts the namesake first
        // with 141 works; the novelist (21) must still win because the book vouches for her key, not because of order or count.
        using var rig = Rig.With(Json(TwoExactFrankHerberts), Json(CapturedDuneAnchorLookup));
        var anchor = AuthorAnchor.FromNameAndBooks("Frank Herbert", [AnchorBook.FromSource(Dune)]);

        var result = await rig.Provider.ResolveAuthorAsync(anchor, rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Ok, result.Outcome);
        Assert.AreEqual(HerbertNovelist, result.Items.Single().Id);
        Assert.AreEqual(2, rig.Requests);
        Assert.AreEqual(2, result.Usage.Attempts);
        StringAssert.Contains(rig.Uris[1], "key:/works/OL893414W");
        Assert.IsFalse(rig.Uris[1].Contains("OL1758387A", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task TheAnchorPicksTheOtherNamesakeWhenTheBookIsHers()
    {
        using var rig = Rig.With(Json(TwoExactFrankHerberts), Json(AnchorBookWrittenByTheSmallerCatalogue));
        var anchor = AuthorAnchor.FromNameAndBooks("Frank Herbert", [AnchorBook.FromSource(Dune)]);

        var result = await rig.Provider.ResolveAuthorAsync(anchor, rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Ok, result.Outcome);
        Assert.AreEqual("OL1758387A", result.Items.Single().Id.ExternalId, "Selection follows the anchor, whichever candidate has more works.");
    }

    [TestMethod]
    public async Task AnAnchorThatVouchesForNobodyLeavesBothNamesakesAsChoices()
    {
        using var rig = Rig.With(Json(TwoExactFrankHerberts), Json("""{"numFound":0,"docs":[]}"""));
        var anchor = AuthorAnchor.FromNameAndBooks("Frank Herbert", [AnchorBook.FromSource(Dune)]);

        var result = await rig.Provider.ResolveAuthorAsync(anchor, rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Ambiguous, result.Outcome);
        Assert.HasCount(2, result.Items);
    }

    [TestMethod]
    public async Task AFailedAnchorLookupKeepsTheChoicesAsPartialAndSelectsNobody()
    {
        using var rig = Rig.With(Json(TwoExactFrankHerberts), Plain(HttpStatusCode.InternalServerError));
        var anchor = AuthorAnchor.FromNameAndBooks("Frank Herbert", [AnchorBook.FromIsbn13("9780441172719")]);

        var result = await rig.Provider.ResolveAuthorAsync(anchor, rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Partial, result.Outcome);
        Assert.HasCount(2, result.Items);
        CollectionAssert.Contains(result.Notes.ToArray(), "anchor-lookup-failed");
        CollectionAssert.Contains(result.Notes.ToArray(), DiscoveryCodes.AnchorInsufficient);
        Assert.AreEqual(2, rig.Requests, "The failed anchor request is not retried.");
        StringAssert.Contains(rig.Uris[1], "isbn:9780441172719");
    }

    [TestMethod]
    public async Task AnAnchorBookFromAnotherProviderCannotVouchAndCostsNoRequest()
    {
        using var rig = Rig.With(Json(TwoExactFrankHerberts));
        var anchor = AuthorAnchor.FromNameAndBooks(
            "Frank Herbert", [AnchorBook.FromSource(new SourceRef("hardcover", "5", ExternalReferenceEntityType.Work))]);

        var result = await rig.Provider.ResolveAuthorAsync(anchor, rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Ambiguous, result.Outcome);
        Assert.AreEqual(1, rig.Requests);
    }

    [TestMethod]
    public async Task CapturedSearchHasOneExactNameAndStillACommonNameAloneNeverSelectsIt()
    {
        using var rig = Rig.With(Json(CapturedFrankHerbertAuthorSearch));

        var result = await rig.Provider.ResolveAuthorAsync(AuthorAnchor.FromNameOnly("Frank Herbert"), rig.Op(), CancellationToken.None);

        // "Frank Herbert Hayward", "Simonds, Frank Herbert" and "Frank Herbert MacPherson" are other people; only the primary name counts.
        Assert.AreEqual(DiscoveryOutcome.Ambiguous, result.Outcome);
        Assert.AreEqual("OL79034A", result.Items.Single().Id.ExternalId);
        Assert.AreEqual("8 October 1920 – 11 February 1986", result.Items.Single().Disambiguation);
        CollectionAssert.Contains(result.Notes.ToArray(), DiscoveryCodes.AnchorInsufficient);
        CollectionAssert.Contains(result.Notes.ToArray(), DiscoveryCodes.ListTruncated, "101 hits exist; only the first page was read.");
    }

    [TestMethod]
    public async Task ANativeAuthorKeyIsResolvedFromItsRecord()
    {
        using var rig = Rig.With(Json(CapturedDuneAuthorRecord));

        var result = await rig.Provider.ResolveAuthorAsync(AuthorAnchor.FromSourceId(HerbertNovelist), rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Ok, result.Outcome);
        Assert.AreEqual("Frank Herbert", result.Items.Single().Name);
        Assert.AreEqual(1, rig.Requests);
        StringAssert.EndsWith(rig.Uris[0], "authors/OL79034A.json");
    }

    [TestMethod]
    public async Task AMissingAuthorRecordIsEmptyARedirectIsUnverifiedAndAForeignKeyIsUnusable()
    {
        using var missing = Rig.With(Plain(HttpStatusCode.NotFound));
        using var redirect = Rig.With(Json("""{"type":{"key":"/type/redirect"},"location":"/authors/OL1A","key":"/authors/OL79034A"}"""));
        using var foreign = Rig.With(Json("""{"key":"/authors/OL5A","name":"Someone Else","type":{"key":"/type/author"}}"""));
        var anchor = AuthorAnchor.FromSourceId(HerbertNovelist);

        var gone = await missing.Provider.ResolveAuthorAsync(anchor, missing.Op(), CancellationToken.None);
        var merged = await redirect.Provider.ResolveAuthorAsync(anchor, redirect.Op(), CancellationToken.None);
        var wrong = await foreign.Provider.ResolveAuthorAsync(anchor, foreign.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Empty, gone.Outcome);
        Assert.AreEqual(DiscoveryOutcome.Unverified, merged.Outcome);
        CollectionAssert.Contains(merged.Notes.ToArray(), "author-redirect-unfollowed");
        Assert.AreEqual(1, redirect.Requests, "A redirect is reported, never followed.");
        Assert.AreEqual(DiscoveryOutcome.NoUsableItems, wrong.Outcome);
    }

    [TestMethod]
    [DataRow("openlibrary", "Frank Herbert", ExternalReferenceEntityType.Author)]
    [DataRow("openlibrary", "OL79034W", ExternalReferenceEntityType.Author)]
    [DataRow("hardcover", "OL79034A", ExternalReferenceEntityType.Author)]
    public async Task AnAuthorIdThatIsNotAnOpenLibraryAuthorKeyIsRefusedBeforeAnyRequest(string provider, string id, ExternalReferenceEntityType kind)
    {
        using var rig = Rig.With();

        var result = await rig.Provider.ResolveAuthorAsync(
            AuthorAnchor.FromSourceId(new SourceRef(provider, id, kind)), rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Failed, result.Outcome);
        Assert.AreEqual(0, rig.Requests);
    }

    // ================================================================ author works / author series

    [TestMethod]
    public async Task CapturedDuneAuthorPageKeepsNativeIdsAndOffersTheNextPage()
    {
        using var rig = Rig.With(Json(CapturedDuneAuthorPage));
        var author = new AuthorRef("Frank Herbert", HerbertNovelist);

        var result = await rig.Provider.GetAuthorWorksAsync(author, null, rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Ok, result.Outcome);
        Assert.AreEqual(DiscoveryCompletion.MoreAvailable, result.Completion, "numFound 145 against 4 rows read.");
        Assert.AreEqual("p:2", result.Next!.Value);
        Assert.HasCount(4, result.Items);
        var dune = result.Items[0];
        Assert.AreEqual("OL893414W", dune.Book.Source.ExternalId);
        Assert.AreEqual(ExternalReferenceEntityType.Work, dune.Book.Source.Kind);
        Assert.AreEqual("OL331367L", dune.Series.Single().Series.Id!.ExternalId);
        Assert.AreEqual("1", dune.Series.Single().Position!.Label);
        Assert.AreEqual(1, rig.Requests);
        StringAssert.Contains(rig.Uris[0], "author=OL79034A");
        StringAssert.Contains(rig.Uris[0], "limit=50");
    }

    [TestMethod]
    public async Task ARowNotCreditedToTheRequestedAuthorKeyIsDroppedBecauseTheFilterIsRechecked()
    {
        using var rig = Rig.With(Json(
            """{"numFound":2,"start":0,"docs":[{"key":"/works/OL1W","title":"Mine","author_key":["OL79034A"],"author_name":["Frank Herbert"]},{"key":"/works/OL2W","title":"Namesake work","author_key":["OL1758387A"],"author_name":["Frank Herbert"]}]}"""));

        var result = await rig.Provider.GetAuthorWorksAsync(new AuthorRef("Frank Herbert", HerbertNovelist), null, rig.Op(), CancellationToken.None);

        Assert.AreEqual("Mine", result.Items.Single().Book.Title.Reported);
        CollectionAssert.Contains(result.Notes.ToArray(), "author-anchor-mismatch");
    }

    [TestMethod]
    public async Task SameNamedCoAuthorsWithDifferentKeysKeepSeparateCreditsAndAreUnprojectableForTheLegacyWriter()
    {
        using var rig = Rig.With(Json(
            """{"numFound":1,"start":0,"docs":[{"key":"/works/OL1W","title":"Twin Book","author_key":["OL79034A","OL1758387A"],"author_name":["Frank Herbert","Frank Herbert"]}]}"""));

        var result = await rig.Provider.GetAuthorWorksAsync(new AuthorRef("Frank Herbert", HerbertNovelist), null, rig.Op(), CancellationToken.None);

        var credits = result.Items.Single().Book.Credits;
        Assert.AreEqual("0|1", string.Join('|', credits.Select(credit => credit.Ordinal)));
        Assert.AreEqual("OL79034A|OL1758387A", string.Join('|', credits.Select(credit => credit.AuthorId!.ExternalId)));
        var projection = LegacyCreditProjection.From(credits);
        Assert.IsFalse(projection.Projectable, "The name-matching writer must not be handed two same-named ids.");
        Assert.AreEqual(LegacyCreditProjection.AmbiguousSameName, projection.ReasonCode);
    }

    [TestMethod]
    public async Task MissingOrMismatchedCreditArraysKeepLabelsWithoutAnyIdAndSayWhy()
    {
        using var rig = Rig.With(Json(
            """{"numFound":2,"start":0,"docs":[{"key":"/works/OL1W","title":"Mismatch","author_key":["OL79034A","OL5A","OL6A"],"author_name":["Frank Herbert","Brian Herbert"]},{"key":"/works/OL2W","title":"Keys only","author_key":["OL79034A"]}]}"""));

        var result = await rig.Provider.GetAuthorWorksAsync(new AuthorRef("Frank Herbert", HerbertNovelist), null, rig.Op(), CancellationToken.None);

        var mismatch = result.Items[0].Book.Credits;
        Assert.AreEqual("Frank Herbert|Brian Herbert", string.Join('|', mismatch.Select(credit => credit.Label)));
        Assert.IsTrue(mismatch.All(credit => credit.AuthorId is null), "Order is never used to guess which key is whose.");
        Assert.IsEmpty(result.Items[1].Book.Credits);
        CollectionAssert.Contains(result.Notes.ToArray(), "credit-arrays-mismatch");
    }

    [TestMethod]
    public async Task AWorkWhoseSeriesArraysCannotBePairedIsWithheldNotKeptAsHavingNoSeries()
    {
        using var rig = Rig.With(Json(
            """{"numFound":2,"start":0,"docs":[{"key":"/works/OL1W","title":"Bad","author_key":["OL79034A"],"series_key":["OL10L","OL11L"],"series_name":["Just One"]},{"key":"/works/OL2W","title":"Good","author_key":["OL79034A"],"series_key":["OL10L"],"series_name":["Dune"],"series_position":["1"]}]}"""));

        var result = await rig.Provider.GetAuthorWorksAsync(new AuthorRef("Frank Herbert", HerbertNovelist), null, rig.Op(), CancellationToken.None);

        Assert.AreEqual("Good", result.Items.Single().Book.Title.Reported);
        CollectionAssert.Contains(result.Notes.ToArray(), "series-arrays-mismatch");
    }

    [TestMethod]
    public async Task AuthorSeriesAreDerivedFromTheCheckedWorksWithNativeTargetsAndADerivedEdge()
    {
        using var rig = Rig.With(Json(
            """{"numFound":5,"start":0,"docs":[{"key":"/works/OL1W","author_key":["OL7825177A"],"series_key":["OL331363L"],"series_name":["The Empyrean"],"series_position":["1"]},{"key":"/works/OL2W","author_key":["OL7825177A"],"series_key":["OL331363L"],"series_name":["The Empyrean"],"series_position":["2"]},{"key":"/works/OL3W","author_key":["OL7825177A"],"series_key":["OL331365L"],"series_name":["The Renegades"],"series_position":["1"]},{"key":"/works/OL4W","author_key":["OL7825177A"]},{"key":"/works/OL5W","author_key":["OL7825177A"],"series_name":["Mystery Set"]}]}"""));

        var result = await rig.Provider.GetAuthorSeriesAsync(new AuthorRef("Rebecca Yarros", Yarros), null, rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Ok, result.Outcome);
        Assert.AreEqual(DiscoveryCompletion.ExhaustedInCheckedScope, result.Completion);
        Assert.AreEqual(
            "The Empyrean|The Renegades|Mystery Set", string.Join('|', result.Items.Select(entry => entry.Series.Label)), "Deduplicated within the page.");
        Assert.AreEqual("OL331363L", result.Items[0].Series.Id!.ExternalId);
        Assert.IsTrue(result.Items.All(entry => entry.Relationship.Basis == RelationshipBasis.Derived));
        Assert.AreEqual("author-works-checked-pages", result.Items[0].Relationship.DerivedScope);
        Assert.IsNull(result.Items[2].Series.Id, "A label alone never becomes a native series id.");
        Assert.IsFalse(result.Items[2].Series.IsBound);
        CollectionAssert.Contains(result.Notes.ToArray(), DiscoveryCodes.NoNativeSeriesId);
        CollectionAssert.Contains(result.Notes.ToArray(), "derived-from-author-works");
        Assert.AreEqual("series-claimed-by-checked-author-works", result.Scope.Filter);
        Assert.AreEqual(1, rig.Requests, "Derivation reads works pages; there is no per-series or per-work follow-up.");
    }

    [TestMethod]
    public async Task SameNamedSeriesInDistinctScopesStayTwoEntriesWithTheirOwnKeys()
    {
        using var rig = Rig.With(Json(
            """{"numFound":2,"start":0,"docs":[{"key":"/works/OL1W","author_key":["OL7825177A"],"series_key":["OL10L"],"series_name":["Legacy"],"series_position":["1"]},{"key":"/works/OL2W","author_key":["OL7825177A"],"series_key":["OL11L"],"series_name":["Legacy"],"series_position":["1"]}]}"""));

        var result = await rig.Provider.GetAuthorSeriesAsync(new AuthorRef("Rebecca Yarros", Yarros), null, rig.Op(), CancellationToken.None);

        Assert.HasCount(2, result.Items);
        Assert.AreEqual("OL10L|OL11L", string.Join('|', result.Items.Select(entry => entry.Series.Id!.ExternalId)));
        Assert.AreEqual("Legacy", result.Items[0].Series.Label);
        Assert.AreEqual("Legacy", result.Items[1].Series.Label);
    }

    [TestMethod]
    public async Task ADifferentLabelForOneKeyKeepsTheFirstAndSaysSo()
    {
        using var rig = Rig.With(Json(
            """{"numFound":2,"start":0,"docs":[{"key":"/works/OL1W","author_key":["OL7825177A"],"series_key":["OL10L"],"series_name":["The Empyrean"]},{"key":"/works/OL2W","author_key":["OL7825177A"],"series_key":["OL10L"],"series_name":["Empyrean"]}]}"""));

        var result = await rig.Provider.GetAuthorSeriesAsync(new AuthorRef("Rebecca Yarros", Yarros), null, rig.Op(), CancellationToken.None);

        Assert.AreEqual("The Empyrean", result.Items.Single().Series.Label);
        CollectionAssert.Contains(result.Notes.ToArray(), "series-label-conflict");
    }

    [TestMethod]
    public async Task APageOfWorksWithoutSeriesIsEmptyWhenFinalAndNoUsableWhenMoreWorksFollow()
    {
        using var last = Rig.With(Json("""{"numFound":1,"start":0,"docs":[{"key":"/works/OL1W","author_key":["OL7825177A"]}]}"""));
        using var more = Rig.With(Json("""{"numFound":90,"start":0,"docs":[{"key":"/works/OL1W","author_key":["OL7825177A"]}]}"""));
        var author = new AuthorRef("Rebecca Yarros", Yarros);

        var finalPage = await last.Provider.GetAuthorSeriesAsync(author, null, last.Op(), CancellationToken.None);
        var middle = await more.Provider.GetAuthorSeriesAsync(author, null, more.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.NoUsableItems, finalPage.Outcome, "Rows were read; none carried series (a derived, scoped statement).");
        CollectionAssert.Contains(finalPage.Notes.ToArray(), "no-series-claims-on-page");
        Assert.AreEqual(DiscoveryOutcome.NoUsableItems, middle.Outcome);
        Assert.AreEqual(DiscoveryCompletion.MoreAvailable, middle.Completion);
        Assert.AreEqual("p:2", middle.Next!.Value);
    }

    [TestMethod]
    public async Task AnAuthorWithNoIndexedWorksIsEmptyForTheDerivedScopeOnly()
    {
        using var rig = Rig.With(Json("""{"numFound":0,"start":0,"docs":[]}"""));

        var result = await rig.Provider.GetAuthorSeriesAsync(new AuthorRef("Nobody", Yarros), null, rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Empty, result.Outcome);
        Assert.AreEqual("series-claimed-by-checked-author-works", result.Scope.Filter);
    }

    // ================================================================ O1: anchor evidence must be the admitted anchor

    // hand-built: two people both named exactly "Frank Herbert" with different keys.
    private const string TwinFrankHerberts =
        """{"numFound":2,"start":0,"docs":[{"key":"OL1A","name":"Frank Herbert","work_count":5},{"key":"OL2A","name":"Frank Herbert","work_count":9}]}""";

    private const string DuneIsbn13 = "9780441172719";
    private const string DuneIsbn10 = "0441172717";

    private static readonly SourceRef AnchorWork = new("openlibrary", "OL1W", ExternalReferenceEntityType.Work);

    [TestMethod]
    public async Task AnAnchorRowThatIsNotTheAdmittedWorkCannotVouchForAnAuthor()
    {
        // Reviewer trace 1: the anchor is OL1W, the lookup answers with an unrelated OL999W credited to OL2A.
        using var rig = Rig.With(
            Json(TwinFrankHerberts),
            Json("""{"numFound":1,"start":0,"docs":[{"key":"/works/OL999W","author_key":["OL2A"]}]}"""));
        var anchor = AuthorAnchor.FromNameAndBooks("Frank Herbert", [AnchorBook.FromSource(AnchorWork)]);

        var result = await rig.Provider.ResolveAuthorAsync(anchor, rig.Op(), CancellationToken.None);

        Assert.AreNotEqual(DiscoveryOutcome.Ok, result.Outcome, "An unrelated row is not evidence for anyone.");
        Assert.HasCount(2, result.Items, "Both namesakes stay as choices.");
        CollectionAssert.Contains(result.Notes.ToArray(), "anchor-row-unmatched");
        CollectionAssert.Contains(result.Notes.ToArray(), DiscoveryCodes.AnchorInsufficient);
        Assert.AreEqual(2, rig.Requests);
    }

    [TestMethod]
    public async Task ATruncatedIsbnAnchorPageNeverCertifiesAUniqueAuthor()
    {
        // Reviewer trace 2: numFound 11, the first 10 rows credit OL1A; the unreturned row might credit OL2A.
        var rows = string.Join(',', Enumerable.Range(1, 10).Select(index =>
            $$"""{"key":"/works/OL{{index + 100}}W","author_key":["OL1A"],"isbn":["{{DuneIsbn13}}"]}"""));
        using var rig = Rig.With(Json(TwinFrankHerberts), Json($$"""{"numFound":11,"start":0,"docs":[{{rows}}]}"""));
        var anchor = AuthorAnchor.FromNameAndBooks("Frank Herbert", [AnchorBook.FromIsbn13(DuneIsbn13)]);

        var result = await rig.Provider.ResolveAuthorAsync(anchor, rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Partial, result.Outcome);
        Assert.HasCount(2, result.Items);
        CollectionAssert.Contains(result.Notes.ToArray(), "anchor-evidence-truncated");
        Assert.AreEqual(2, rig.Requests, "The unread rows are not chased: one anchor request, no paging.");
        StringAssert.Contains(rig.Uris[1], "isbn");
    }

    [TestMethod]
    [DataRow(DuneIsbn13)]
    [DataRow(DuneIsbn10)]
    public async Task AnIsbnAnchorRowThatListsTheIsbnInEitherFormVouchesForItsAuthor(string listed)
    {
        using var rig = Rig.With(
            Json(TwinFrankHerberts),
            Json($$"""{"numFound":1,"start":0,"docs":[{"key":"/works/OL7W","author_key":["OL1A"],"isbn":["{{listed}}","0000000000"]}]}"""));
        var anchor = AuthorAnchor.FromNameAndBooks("Frank Herbert", [AnchorBook.FromIsbn13(DuneIsbn13)]);

        var result = await rig.Provider.ResolveAuthorAsync(anchor, rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Ok, result.Outcome);
        Assert.AreEqual("OL1A", result.Items.Single().Id.ExternalId);
        StringAssert.Contains(rig.Uris[1], "isbn:" + DuneIsbn13);
    }

    [TestMethod]
    public async Task AnIsbnRowWithADifferentOrMissingIsbnIsNotEvidence()
    {
        using var different = Rig.With(
            Json(TwinFrankHerberts),
            Json("""{"numFound":1,"docs":[{"key":"/works/OL7W","author_key":["OL1A"],"isbn":["9781111111111"]}]}"""));
        using var missing = Rig.With(
            Json(TwinFrankHerberts),
            Json("""{"numFound":1,"docs":[{"key":"/works/OL7W","author_key":["OL1A"]}]}"""));
        var anchor = AuthorAnchor.FromNameAndBooks("Frank Herbert", [AnchorBook.FromIsbn13(DuneIsbn13)]);

        var wrong = await different.Provider.ResolveAuthorAsync(anchor, different.Op(), CancellationToken.None);
        var unknown = await missing.Provider.ResolveAuthorAsync(anchor, missing.Op(), CancellationToken.None);

        Assert.AreNotEqual(DiscoveryOutcome.Ok, wrong.Outcome);
        Assert.AreNotEqual(DiscoveryOutcome.Ok, unknown.Outcome);
        Assert.HasCount(2, wrong.Items);
        Assert.HasCount(2, unknown.Items);
        CollectionAssert.Contains(wrong.Notes.ToArray(), "anchor-row-unmatched");
    }

    [TestMethod]
    public async Task AWorkAndAnIsbnAnchorThatAgreeVouchTogether()
    {
        using var rig = Rig.With(
            Json(TwinFrankHerberts),
            Json($$"""{"numFound":2,"docs":[{"key":"/works/OL1W","author_key":["OL1A"]},{"key":"/works/OL7W","author_key":["OL1A"],"isbn":["{{DuneIsbn13}}"]}]}"""));
        var anchor = AuthorAnchor.FromNameAndBooks("Frank Herbert", [AnchorBook.FromSource(AnchorWork), AnchorBook.FromIsbn13(DuneIsbn13)]);

        var result = await rig.Provider.ResolveAuthorAsync(anchor, rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Ok, result.Outcome);
        Assert.AreEqual("OL1A", result.Items.Single().Id.ExternalId);
        StringAssert.Contains(rig.Uris[1], "key:/works/OL1W");
        StringAssert.Contains(rig.Uris[1], "isbn:" + DuneIsbn13);
        Assert.AreEqual(2, rig.Requests);
    }

    [TestMethod]
    public async Task AnchorsThatCreditDifferentNamesakesStayChoices()
    {
        using var rig = Rig.With(
            Json(TwinFrankHerberts),
            Json("""{"numFound":2,"docs":[{"key":"/works/OL1W","author_key":["OL1A"]},{"key":"/works/OL2W","author_key":["OL2A"]}]}"""));
        var anchor = AuthorAnchor.FromNameAndBooks(
            "Frank Herbert", [AnchorBook.FromSource(AnchorWork), AnchorBook.FromSource(new SourceRef("openlibrary", "OL2W", ExternalReferenceEntityType.Work))]);

        var result = await rig.Provider.ResolveAuthorAsync(anchor, rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Ambiguous, result.Outcome);
        Assert.HasCount(2, result.Items);
    }

    [TestMethod]
    public async Task AnAnchorRowWithoutReadableCreditsOrANullRowLeavesTheEvidenceUnreadable()
    {
        using var noCredit = Rig.With(
            Json(TwinFrankHerberts),
            Json("""{"numFound":2,"docs":[{"key":"/works/OL1W","author_key":["OL1A"]},{"key":"/works/OL2W"}]}"""));
        using var nullRow = Rig.With(
            Json(TwinFrankHerberts),
            Json("""{"numFound":2,"docs":[{"key":"/works/OL1W","author_key":["OL1A"]},null]}"""));
        var anchor = AuthorAnchor.FromNameAndBooks(
            "Frank Herbert", [AnchorBook.FromSource(AnchorWork), AnchorBook.FromSource(new SourceRef("openlibrary", "OL2W", ExternalReferenceEntityType.Work))]);

        var hidden = await noCredit.Provider.ResolveAuthorAsync(anchor, noCredit.Op(), CancellationToken.None);
        var unreadable = await nullRow.Provider.ResolveAuthorAsync(anchor, nullRow.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Partial, hidden.Outcome, "A matched anchor row with no readable credits could hide the other namesake.");
        CollectionAssert.Contains(hidden.Notes.ToArray(), "anchor-evidence-unreadable");
        Assert.AreEqual(DiscoveryOutcome.Partial, unreadable.Outcome);
        CollectionAssert.Contains(unreadable.Notes.ToArray(), "anchor-evidence-unreadable");
        Assert.HasCount(2, hidden.Items);
    }

    [TestMethod]
    public async Task AWorkAnchorWithNoTotalButAFullPageIsTreatedAsPossiblyTruncated()
    {
        var rows = string.Join(',', Enumerable.Range(1, 10).Select(index =>
            $$"""{"key":"/works/OL{{index}}W","author_key":["OL1A"]}"""));
        using var rig = Rig.With(Json(TwinFrankHerberts), Json($$"""{"docs":[{{rows}}]}"""));
        var anchor = AuthorAnchor.FromNameAndBooks(
            "Frank Herbert", [AnchorBook.FromSource(AnchorWork), AnchorBook.FromSource(new SourceRef("openlibrary", "OL2W", ExternalReferenceEntityType.Work))]);

        var result = await rig.Provider.ResolveAuthorAsync(anchor, rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Partial, result.Outcome);
        CollectionAssert.Contains(result.Notes.ToArray(), "anchor-evidence-truncated");
    }

    [TestMethod]
    public async Task AnAuthorSearchWithAnUnreadableRowIsNeverEmptyAndNeverSelectsOneNamesake()
    {
        using var onlyUnreadable = Rig.With(Json("""{"numFound":2,"start":0,"docs":[null,{"name":"Frank Herbert"}]}"""));
        using var oneReadable = Rig.With(Json("""{"numFound":2,"start":0,"docs":[{"key":"OL1A","name":"Frank Herbert"},null]}"""));
        var anchor = AuthorAnchor.FromNameOnly("Frank Herbert");

        var none = await onlyUnreadable.Provider.ResolveAuthorAsync(anchor, onlyUnreadable.Op(), CancellationToken.None);
        var some = await oneReadable.Provider.ResolveAuthorAsync(anchor, oneReadable.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.NoUsableItems, none.Outcome, "Rows that could not be read are not an empty scope.");
        Assert.AreEqual(DiscoveryOutcome.Partial, some.Outcome, "The unreadable row might be the author the caller meant.");
        Assert.HasCount(1, some.Items);
    }

    // ================================================================ O2: raw page accounting across the three page operations

    private delegate Task<(DiscoveryOutcome Outcome, DiscoveryCompletion Completion, string? Next, string[] Notes, int Items, int Attempts, int Pages)> PageCall(
        Rig rig, DiscoveryCursor? cursor);

    private static readonly (string Name, PageCall Call)[] PageOperations =
    [
        ("members", async (rig, cursor) =>
        {
            var r = await rig.Provider.GetSeriesMembersAsync(new SeriesRef("S", new SourceRef("openlibrary", "OL10L", ExternalReferenceEntityType.Series)), cursor, rig.Op(), CancellationToken.None);
            return (r.Outcome, r.Completion, r.Next?.Value, r.Notes.ToArray(), r.Items.Count, r.Usage.Attempts, r.Usage.Pages);
        }),
        ("works", async (rig, cursor) =>
        {
            var r = await rig.Provider.GetAuthorWorksAsync(new AuthorRef("Frank Herbert", HerbertNovelist), cursor, rig.Op(), CancellationToken.None);
            return (r.Outcome, r.Completion, r.Next?.Value, r.Notes.ToArray(), r.Items.Count, r.Usage.Attempts, r.Usage.Pages);
        }),
        ("author series", async (rig, cursor) =>
        {
            var r = await rig.Provider.GetAuthorSeriesAsync(new AuthorRef("Frank Herbert", HerbertNovelist), cursor, rig.Op(), CancellationToken.None);
            return (r.Outcome, r.Completion, r.Next?.Value, r.Notes.ToArray(), r.Items.Count, r.Usage.Attempts, r.Usage.Pages);
        })
    ];

    private static string NullRows(int count) => string.Join(',', Enumerable.Repeat("null", count));

    [TestMethod]
    public async Task AFullPageOfNullRowsIsNoUsableItemsWithTheNextPageInEveryPageOperation()
    {
        // Reviewer trace: numFound 100, start 0, 50 null rows. 100 records exist; none of the 50 slots could be used.
        foreach (var (name, call) in PageOperations)
        {
            using var rig = Rig.With(Json($$"""{"numFound":100,"start":0,"docs":[{{NullRows(50)}}]}"""));

            var result = await call(rig, null);

            Assert.AreEqual(DiscoveryOutcome.NoUsableItems, result.Outcome, name);
            Assert.AreEqual(DiscoveryCompletion.MoreAvailable, result.Completion, name);
            Assert.AreEqual("p:2", result.Next, name);
            CollectionAssert.Contains(result.Notes, DiscoveryCodes.ItemMissingId, name);
            Assert.AreEqual(1, rig.Requests, name);
            Assert.AreEqual(1, result.Attempts, name);
            Assert.AreEqual(1, result.Pages, name);
        }
    }

    [TestMethod]
    public async Task AFullPageOfNullRowsWithoutATotalStillOffersTheNextPage()
    {
        foreach (var (name, call) in PageOperations)
        {
            using var rig = Rig.With(Json($$"""{"docs":[{{NullRows(50)}}]}"""));

            var result = await call(rig, null);

            Assert.AreEqual(DiscoveryOutcome.NoUsableItems, result.Outcome, name);
            Assert.AreEqual("p:2", result.Next, name);
        }
    }

    [TestMethod]
    public async Task TheLastPageOfNullRowsIsNoUsableItemsAndExhaustedNotEmpty()
    {
        foreach (var (name, call) in PageOperations)
        {
            using var rig = Rig.With(Json($$"""{"numFound":100,"start":50,"docs":[{{NullRows(50)}}]}"""));

            var result = await call(rig, new DiscoveryCursor("p:2"));

            Assert.AreEqual(DiscoveryOutcome.NoUsableItems, result.Outcome, name);
            Assert.AreEqual(DiscoveryCompletion.ExhaustedInCheckedScope, result.Completion, name);
            Assert.IsNull(result.Next, name);
        }
    }

    [TestMethod]
    public async Task APageOfNullsAndUsableRowsKeepsTheUsableRowsAndTheRawContinuation()
    {
        // 25 nulls + 25 usable rows = 50 consumed slots against numFound 100: one more page exists.
        var usable = string.Join(',', Enumerable.Range(1, 25).Select(id =>
            $$"""{"key":"/works/OL{{id}}W","title":"Book {{id}}","author_key":["OL79034A"],"series_key":["OL10L"],"series_name":["S"],"series_position":["{{id}}"]}"""));
        foreach (var (name, call) in PageOperations)
        {
            using var rig = Rig.With(Json($$"""{"numFound":100,"start":0,"docs":[{{NullRows(25)}},{{usable}}]}"""));

            var result = await call(rig, null);

            Assert.AreEqual(DiscoveryOutcome.Ok, result.Outcome, name);
            Assert.AreEqual(DiscoveryCompletion.MoreAvailable, result.Completion, name);
            Assert.AreEqual("p:2", result.Next, name);
            Assert.IsGreaterThan(0, result.Items, name);
            CollectionAssert.Contains(result.Notes, DiscoveryCodes.ItemMissingId, name);
        }
    }

    [TestMethod]
    public async Task OnlyAGenuinelyEmptyFirstPageIsEmptyAndANonZeroTotalWithNoRowsIsNot()
    {
        foreach (var (name, call) in PageOperations)
        {
            using var genuine = Rig.With(Json("""{"numFound":0,"start":0,"docs":[]}"""));
            using var contradictory = Rig.With(Json("""{"numFound":40,"start":0,"docs":[]}"""));

            var empty = await call(genuine, null);
            var odd = await call(contradictory, null);

            Assert.AreEqual(DiscoveryOutcome.Empty, empty.Outcome, name);
            Assert.AreEqual(DiscoveryOutcome.NoUsableItems, odd.Outcome, name + ": the provider says 40 records exist and sent none.");
            CollectionAssert.Contains(odd.Notes, DiscoveryCodes.ProviderError, name);
        }
    }

    // ================================================================ O3: response-body failures stay inside the typed result contract

    [TestMethod]
    public async Task AResponseBodyThatFailsAfterHeadersIsATypedFailureAndTheStreamIsDisposed()
    {
        var stream = FaultingStream.Sync(_ => throw new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely."));
        using var rig = Rig.With(StreamResponse(stream));

        var result = await rig.Provider.GetAuthorWorksAsync(new AuthorRef("Frank Herbert", HerbertNovelist), null, rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Failed, result.Outcome);
        CollectionAssert.Contains(result.Notes.ToArray(), DiscoveryCodes.ProviderError);
        Assert.AreEqual(1, rig.Requests, "A failed body is not retried.");
        Assert.AreEqual(1, result.Usage.Attempts);
        Assert.IsTrue(stream.Disposed, "The response content is released.");
        foreach (var note in result.Notes)
        {
            Assert.IsFalse(note.Contains("premature", StringComparison.OrdinalIgnoreCase), "Only stable codes travel, never exception text.");
        }
    }

    [TestMethod]
    public async Task ABodyThatBreaksPartWayIsAFailureNotAPartialOrEmptyResultInEveryOperation()
    {
        var calls = new Func<Rig, Task<DiscoveryOutcome>>[]
        {
            async rig => (await rig.Provider.GetSeriesMembersAsync(new SeriesRef("S", Empyrean), null, rig.Op(), CancellationToken.None)).Outcome,
            async rig => (await rig.Provider.FindSeriesForBookAsync(Dune, rig.Op(), CancellationToken.None)).Outcome,
            async rig => (await rig.Provider.ResolveAuthorAsync(AuthorAnchor.FromSourceId(HerbertNovelist), rig.Op(), CancellationToken.None)).Outcome,
            async rig => (await rig.Provider.ResolveAuthorAsync(AuthorAnchor.FromNameOnly("Frank Herbert"), rig.Op(), CancellationToken.None)).Outcome,
            async rig => (await rig.Provider.GetAuthorSeriesAsync(new AuthorRef("Frank Herbert", HerbertNovelist), null, rig.Op(), CancellationToken.None)).Outcome
        };
        foreach (var call in calls)
        {
            var sent = false;
            var stream = FaultingStream.Sync(_ =>
            {
                if (sent)
                {
                    throw new IOException("connection reset");
                }

                sent = true;
                return Encoding.UTF8.GetBytes("""{"numFound":3,"docs":[{"key":""");
            });
            using var rig = Rig.With(StreamResponse(stream));

            var outcome = await call(rig);

            Assert.AreEqual(DiscoveryOutcome.Failed, outcome);
            Assert.IsTrue(stream.Disposed);
        }
    }

    [TestMethod]
    public async Task AProgrammingErrorWhileReadingTheBodyIsNotSwallowed()
    {
        var stream = FaultingStream.Sync(_ => throw new InvalidOperationException("a bug, not a network condition"));
        using var rig = Rig.With(StreamResponse(stream));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            rig.Provider.GetSeriesMembersAsync(new SeriesRef("S", Empyrean), null, rig.Op(), CancellationToken.None));
    }

    [TestMethod]
    public async Task ABodyThatNeverArrivesIsTimedOutAtTheDeadlineAndTheStreamIsDisposed()
    {
        var stream = new FaultingStream(async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Array.Empty<byte>();
        });
        using var rig = Rig.With(StreamResponse(stream));
        var limits = new DiscoveryLimits { OperationMaxWork = TimeSpan.FromMilliseconds(150), JourneyMaxWork = TimeSpan.FromMilliseconds(150) };

        var result = await rig.Provider.GetSeriesMembersAsync(new SeriesRef("S", Empyrean), null, rig.Op(limits, TimeProvider.System), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.TimedOut, result.Outcome);
        CollectionAssert.Contains(result.Notes.ToArray(), DiscoveryCodes.DeadlineReached);
        Assert.IsTrue(stream.Disposed);
    }

    [TestMethod]
    public async Task CallerCancellationWhileTheBodyIsBeingReadStillPropagates()
    {
        using var cancelled = new CancellationTokenSource();
        var stream = new FaultingStream(async token =>
        {
            await cancelled.CancelAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Array.Empty<byte>();
        });
        using var rig = Rig.With(StreamResponse(stream));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            rig.Provider.GetSeriesMembersAsync(new SeriesRef("S", Empyrean), null, rig.Op(), cancelled.Token));
        Assert.IsTrue(stream.Disposed);
    }

    [TestMethod]
    public async Task AnOversizedBodyWithoutALengthHeaderIsRefusedAsItStreams()
    {
        var stream = FaultingStream.Sync(_ => new byte[1024 * 1024]);
        using var rig = Rig.With(StreamResponse(stream));

        var result = await rig.Provider.GetSeriesMembersAsync(new SeriesRef("S", Empyrean), null, rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Failed, result.Outcome);
        CollectionAssert.Contains(result.Notes.ToArray(), "response-too-large");
        Assert.IsTrue(stream.Disposed);
    }

    // ================================================================ transport, budgets, errors, cancellation

    [TestMethod]
    public async Task ATooManyRequestsAnswerIsThrottledAndNeverRetried()
    {
        using var short1 = Rig.With(Plain(HttpStatusCode.TooManyRequests, retryAfterSeconds: 1));
        using var long1 = Rig.With(Plain(HttpStatusCode.TooManyRequests, retryAfterSeconds: 3600));
        var series = new SeriesRef("S", new SourceRef("openlibrary", "OL10L", ExternalReferenceEntityType.Series));

        var brief = await short1.Provider.GetSeriesMembersAsync(series, null, short1.Op(), CancellationToken.None);
        var lengthy = await long1.Provider.GetSeriesMembersAsync(series, null, long1.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Throttled, brief.Outcome);
        CollectionAssert.Contains(brief.Notes.ToArray(), DiscoveryCodes.Throttled);
        Assert.AreEqual(1, short1.Requests, "One physical attempt; Open Library's pipeline does not retry and discovery adds none.");
        Assert.AreEqual(0, brief.Usage.Retries);
        CollectionAssert.Contains(lengthy.Notes.ToArray(), DiscoveryCodes.RetryAfterTooLong);
        Assert.AreEqual(1, long1.Requests);
    }

    [TestMethod]
    public async Task AServerErrorIsAFailureNotAnEmptyScopeAndIsSentOnce()
    {
        using var rig = Rig.With(Plain(HttpStatusCode.InternalServerError), Json(CapturedEmpyreanSeries));

        var result = await rig.Provider.GetSeriesMembersAsync(new SeriesRef("S", Empyrean), null, rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Failed, result.Outcome);
        CollectionAssert.Contains(result.Notes.ToArray(), DiscoveryCodes.ProviderError);
        Assert.AreEqual(1, rig.Requests);
        Assert.AreEqual(1, result.Usage.Attempts);
    }

    [TestMethod]
    public async Task ForbiddenIsUnavailableAndMalformedOrDocslessBodiesAreFailures()
    {
        using var forbidden = Rig.With(Plain(HttpStatusCode.Forbidden));
        using var malformed = Rig.With(Json("""{"docs":[{"key":"""));
        using var docsless = Rig.With(Json("""{"numFound":3}"""));
        var series = new SeriesRef("S", Empyrean);

        var denied = await forbidden.Provider.GetSeriesMembersAsync(series, null, forbidden.Op(), CancellationToken.None);
        var broken = await malformed.Provider.GetSeriesMembersAsync(series, null, malformed.Op(), CancellationToken.None);
        var noDocs = await docsless.Provider.GetSeriesMembersAsync(series, null, docsless.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Unavailable, denied.Outcome);
        Assert.AreEqual(DiscoveryOutcome.Failed, broken.Outcome);
        Assert.AreEqual(DiscoveryOutcome.Failed, noDocs.Outcome, "A missing docs list is a bad answer, not an empty scope.");
    }

    [TestMethod]
    public async Task AnOversizedBodyIsRefusedBeforeItIsParsed()
    {
        var response = Json(CapturedEmpyreanSeries);
        response.Content = new ByteArrayContent(new byte[3 * 1024 * 1024]);
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var rig = Rig.With(response);

        var result = await rig.Provider.GetSeriesMembersAsync(new SeriesRef("S", Empyrean), null, rig.Op(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Failed, result.Outcome);
        CollectionAssert.Contains(result.Notes.ToArray(), "response-too-large");
    }

    [TestMethod]
    public async Task TheAttemptCeilingStopsFurtherRequestsAndIsALimitNotAnEmptyResult()
    {
        using var rig = Rig.With(Json(CapturedEmpyreanSeries), Json(CapturedEmpyreanSeries), Json(CapturedEmpyreanSeries));
        var operation = rig.Op(new DiscoveryLimits { OperationMaxAttempts = 2 });
        var book = new SourceRef("openlibrary", "OL29226517W", ExternalReferenceEntityType.Work);

        _ = await rig.Provider.FindSeriesForBookAsync(book, operation, CancellationToken.None);
        _ = await rig.Provider.FindSeriesForBookAsync(book, operation, CancellationToken.None);
        var third = await rig.Provider.FindSeriesForBookAsync(book, operation, CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Failed, third.Outcome);
        CollectionAssert.Contains(third.Notes.ToArray(), DiscoveryCodes.AttemptCeiling);
        Assert.AreEqual(2, rig.Requests, "The refused attempt never reached the transport.");
    }

    [TestMethod]
    public async Task ThePageCeilingStopsTheSixthPageBeforeAnyRequest()
    {
        var pages = Enumerable.Range(0, 6).Select(index => Json(PageOfWorks(50, 1 + index * 50, 1000, index * 50))).ToArray();
        using var rig = Rig.With(pages);
        var series = new SeriesRef("Big", new SourceRef("openlibrary", "OL10L", ExternalReferenceEntityType.Series));
        var operation = rig.Op();
        DiscoveryCursor? cursor = null;
        DiscoveryResult<SeriesMember>? last = null;

        for (var page = 0; page < 6; page++)
        {
            last = await rig.Provider.GetSeriesMembersAsync(series, cursor, operation, CancellationToken.None);
            cursor = last.Next ?? new DiscoveryCursor("p:" + (page + 2));
        }

        Assert.AreEqual(DiscoveryOutcome.Failed, last!.Outcome);
        CollectionAssert.Contains(last.Notes.ToArray(), DiscoveryCodes.PageCeiling);
        Assert.AreEqual(5, rig.Requests);
    }

    [TestMethod]
    public async Task AnExpiredJourneyIsRefusedWithoutARequest()
    {
        using var rig = Rig.With(Json(CapturedEmpyreanSeries));
        var operation = rig.Op();
        rig.Clock.Advance(TimeSpan.FromMinutes(11));

        var result = await rig.Provider.GetSeriesMembersAsync(new SeriesRef("S", Empyrean), null, operation, CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Failed, result.Outcome);
        Assert.AreEqual(0, rig.Requests);
    }

    [TestMethod]
    public async Task AnInternalDeadlineReturnsTimedOutAndStopsTheRequest()
    {
        using var rig = Rig.WithHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Json("{}");
        });
        var limits = new DiscoveryLimits { OperationMaxWork = TimeSpan.FromMilliseconds(150), JourneyMaxWork = TimeSpan.FromMilliseconds(150) };
        var operation = rig.Op(limits, TimeProvider.System);

        var result = await rig.Provider.GetSeriesMembersAsync(new SeriesRef("S", Empyrean), null, operation, CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.TimedOut, result.Outcome);
        CollectionAssert.Contains(result.Notes.ToArray(), DiscoveryCodes.DeadlineReached);
        Assert.AreEqual(1, rig.Requests);
    }

    [TestMethod]
    public async Task CallerCancellationPropagatesAndIsNeverAnEmptyOrTimedOutResult()
    {
        using var cancelled = new CancellationTokenSource();
        using var rig = Rig.WithHandler(async (_, token) =>
        {
            await cancelled.CancelAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Json("{}");
        });
        var series = new SeriesRef("S", Empyrean);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            rig.Provider.GetSeriesMembersAsync(series, null, rig.Op(), cancelled.Token));
        using var before = new CancellationTokenSource();
        await before.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            rig.Provider.GetSeriesMembersAsync(series, null, rig.Op(), before.Token));
    }

    [TestMethod]
    public async Task EveryDiscoveryRequestIsAGetAndAnIdentifyingUserAgentIsNotRequiredByTheAdapter()
    {
        using var rig = Rig.With(Json(CapturedDuneAuthorPage), Json(CapturedDuneAuthorPage), Json(CapturedDuneAuthorRecord));
        var operation = rig.Op();

        _ = await rig.Provider.GetAuthorWorksAsync(new AuthorRef("Frank Herbert", HerbertNovelist), null, operation, CancellationToken.None);
        _ = await rig.Provider.GetAuthorSeriesAsync(new AuthorRef("Frank Herbert", HerbertNovelist), null, rig.Op(), CancellationToken.None);
        _ = await rig.Provider.ResolveAuthorAsync(AuthorAnchor.FromSourceId(HerbertNovelist), rig.Op(), CancellationToken.None);

        Assert.AreEqual(3, rig.Requests);
        Assert.IsTrue(rig.AllGet, "Discovery reads only; nothing is posted and nothing is persisted.");
    }

    [TestMethod]
    public void CapabilitiesAreDeclaredPerOperationAndDoNotClaimTheUnverifiedSeedsRoute()
    {
        using var rig = Rig.With();
        var capabilities = ((IBookMetadataDiscoveryProvider)rig.Provider).Capabilities;

        foreach (var operation in Enum.GetValues<DiscoveryOperation>().Where(operation => operation.IsDiscoveryCapability()))
        {
            Assert.AreEqual(CapabilityState.Supported, capabilities.Get(operation).State, operation.ToString());
        }

        Assert.AreEqual("openlibrary", ((IBookMetadataDiscoveryProvider)rig.Provider).ProviderId);
    }

    // ================================================================ ordinary candidates (A3b mapping boundary)

    [TestMethod]
    public async Task OrdinarySearchCarriesAuthorKeysWhenTheArraysPairAndNoSeries()
    {
        using var rig = Rig.With(Json(
            """{"num_found":1,"docs":[{"key":"/works/OL1W","title":"Dune","author_name":["Frank Herbert"],"author_key":["OL79034A"],"series_key":["OL331367L"],"series_name":["Dune"]}]}"""));

        var page = await rig.Provider.SearchAsync(new BookSearchQuery("Dune"), CancellationToken.None);

        var candidate = page.Candidates.Single();
        Assert.AreEqual("OL79034A", candidate.AuthorCredits.Single().SourceAuthorId);
        Assert.AreEqual("Frank Herbert", candidate.AuthorCredits.Single().Name);
        Assert.IsEmpty(candidate.Series, "Ordinary candidates keep an empty series list; discovery does not change what Resolve writes.");
        StringAssert.Contains(rig.Uris[0], "author_key");
    }

    [TestMethod]
    public async Task OrdinarySearchGivesNoIdWhenTheArraysAreMissingOrMismatched()
    {
        using var rig = Rig.With(Json(
            """{"num_found":2,"docs":[{"key":"/works/OL1W","title":"Mismatch","author_name":["A One","B Two"],"author_key":["OL1A"]},{"key":"/works/OL2W","title":"No keys","author_name":["C Three"]}]}"""));

        var page = await rig.Provider.SearchAsync(new BookSearchQuery("x"), CancellationToken.None);

        Assert.HasCount(2, page.Candidates);
        Assert.IsTrue(page.Candidates.All(candidate => candidate.AuthorCredits.Count == 0), "Position is never used to guess whose key is whose.");
        Assert.AreEqual("A One|B Two", string.Join('|', page.Candidates[0].Authors));
    }

    [TestMethod]
    public async Task SameNamedAuthorsWithDifferentKeysAreNotOfferedAsAnOrdinaryCandidate()
    {
        using var rig = Rig.With(Json(
            """{"num_found":2,"docs":[{"key":"/works/OL1W","title":"Twin Book","author_name":["Frank Herbert","Frank Herbert"],"author_key":["OL79034A","OL1758387A"]},{"key":"/works/OL2W","title":"Same person twice","author_name":["Frank Herbert","Frank Herbert"],"author_key":["OL79034A","OL79034A"]}]}"""));

        var page = await rig.Provider.SearchAsync(new BookSearchQuery("x"), CancellationToken.None);

        Assert.AreEqual("Same person twice", page.Candidates.Single().Title, "Only the unambiguous record reaches the unchanged name-matching writer.");
        Assert.AreEqual("OL79034A", page.Candidates.Single().AuthorCredits.Single().SourceAuthorId);
        using var detail = Rig.With(Json(
            """{"num_found":1,"docs":[{"key":"/works/OL1W","title":"Twin Book","author_name":["Frank Herbert","Frank Herbert"],"author_key":["OL79034A","OL1758387A"]}]}"""));
        Assert.IsNull(await detail.Provider.GetDetailsAsync("OL1W", CancellationToken.None));
        Assert.AreEqual(1, detail.Requests, "No edition fetch is made for a record that cannot be offered.");
    }

    // ---- helpers ----

    private static string PageOfWorks(int count, int firstId, int total, int start)
    {
        var builder = new StringBuilder($$"""{"numFound":{{total}},"start":{{start}},"docs":[""");
        for (var row = 0; row < count; row++)
        {
            if (row > 0)
            {
                builder.Append(',');
            }

            var id = firstId + row;
            builder.Append(CultureInfo.InvariantCulture, $$"""{"key":"/works/OL{{id}}W","title":"Book {{id}}","series_key":["OL10L"],"series_name":["Big"],"series_position":["{{id}}"]}""");
        }

        return builder.Append("]}").ToString();
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage StreamResponse(Stream body) => new(HttpStatusCode.OK)
    {
        Content = new StreamContent(body)
    };

    // A response body that is read after the headers have arrived, the way a real connection delivers it. Each read asks the
    // script for the next chunk (an empty chunk ends the body) or throws what the network would.
    private sealed class FaultingStream : Stream
    {
        private readonly Func<CancellationToken, Task<byte[]>> next;

        public FaultingStream(Func<CancellationToken, Task<byte[]>> next) => this.next = next;

        public static FaultingStream Sync(Func<CancellationToken, byte[]> script) => new(token => Task.FromResult(script(token)));

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

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var chunk = await next(cancellationToken);
            var count = Math.Min(chunk.Length, buffer.Length);
            chunk.AsMemory(0, count).CopyTo(buffer);
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

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

    private static HttpResponseMessage Plain(HttpStatusCode status, int? retryAfterSeconds = null)
    {
        var response = new HttpResponseMessage(status);
        if (retryAfterSeconds is { } seconds)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
        }

        return response;
    }

    // The production request gate and handler over a scripted transport. RequestsPerSecond is high only so the tests do not wait
    // the real one-second pace; the gate itself is the real one.
    private sealed class Rig : IDisposable
    {
        private readonly HttpClient client;
        private readonly OpenLibraryRequestGate gate;
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler;
        private int requests;

        private Rig(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler, TimeProvider? time = null, int requestsPerSecond = 1000)
        {
            this.handler = handler;
            Time = time ?? Clock;
            var options = Options.Create(new OpenLibraryMetadataOptions { Enabled = true, MaxResults = 10, TimeoutSeconds = 15, RequestsPerSecond = requestsPerSecond });
            gate = new OpenLibraryRequestGate(options, Time);
            client = new HttpClient(new OpenLibraryRateLimitHandler(gate) { InnerHandler = new Transport(Handle) })
            {
                BaseAddress = new Uri("https://openlibrary.org/")
            };
            Provider = new OpenLibraryBookMetadataProvider(client, options, Time);
        }

        public OpenLibraryBookMetadataProvider Provider { get; }

        public ManualClock Clock { get; } = new();

        /// <summary>The clock the gate, provider and budgets run on: <see cref="Clock"/> unless the rig was built paced.</summary>
        public TimeProvider Time { get; }

        public List<string> Uris { get; } = [];

        public bool AllGet { get; private set; } = true;

        public int Requests => Volatile.Read(ref requests);

        public static Rig With(params HttpResponseMessage[] responses)
        {
            var queue = new Queue<HttpResponseMessage>(responses);
            return new Rig((_, _) =>
            {
                lock (queue)
                {
                    return Task.FromResult(queue.Count > 0 ? queue.Dequeue() : new HttpResponseMessage(HttpStatusCode.InternalServerError));
                }
            });
        }

        public static Rig WithHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) => new(handler);

        /// <summary>The production one-request-per-second pace, on a clock whose pacing waits only end when the test advances it.</summary>
        public static Rig Paced(SteppedClock clock, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) =>
            new(handler, clock, requestsPerSecond: 1);

        public DiscoveryOperationBudget Op(DiscoveryLimits? limits = null, TimeProvider? clock = null)
        {
            var journey = new DiscoveryJourney(Guid.NewGuid(), limits ?? DiscoveryLimits.Default, clock ?? Time);
            return journey.BeginOperation(DiscoveryOperation.GetSeriesMembers, "openlibrary", null)!;
        }

        public void Dispose()
        {
            client.Dispose();
            gate.Dispose();
        }

        /// <summary>A budget for the edition-title read, entirely separate from the discovery operations above.</summary>
        public DiscoveryOperationBudget EditionsBudget(TimeProvider? clock = null)
        {
            var journey = new DiscoveryJourney(Guid.NewGuid(), DiscoveryLimits.Default, clock ?? Time);
            return journey.BeginOperation(DiscoveryOperation.GetWorkEditions, "openlibrary", null)!;
        }

        private Task<HttpResponseMessage> Handle(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requests);
            lock (Uris)
            {
                Uris.Add(Uri.UnescapeDataString(request.RequestUri!.PathAndQuery));
                AllGet &= request.Method == HttpMethod.Get;
            }

            return handler(request, cancellationToken);
        }

        private sealed class Transport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                send(request, cancellationToken);
        }
    }

    // ---- edition titles: one request, no retry, the first page only ----------------------------------------------

    [TestMethod]
    public async Task EditionTitlesAreOneRequestWithNoRetryAndKeepEachStatedLanguage()
    {
        const string editions = """
            {"entries":[
              {"key":"/books/OL1M","title":"Alder","languages":[{"key":"/languages/eng"}]},
              {"key":"/books/OL2M","title":"Alder (Large Print)"}
            ]}
            """;
        using var rig = Rig.With(Json(editions));

        var read = await rig.Provider.GetWorkEditionTitlesAsync(
            new SourceRef("openlibrary", "OL45804W", ExternalReferenceEntityType.Work), rig.EditionsBudget(), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Ok, read.Outcome);
        Assert.AreEqual(2, read.Value!.Editions.Count);
        Assert.AreEqual("OL1M", read.Value.Editions[0].EditionId);
        Assert.AreEqual("Alder", read.Value.Editions[0].Title);
        Assert.AreEqual("en", read.Value.Editions[0].Language);
        Assert.IsNull(read.Value.Editions[1].Language, "A source that states no language leaves it unknown.");
        Assert.AreEqual("title", read.Value.Editions[0].SourceField);
        Assert.AreEqual(1, rig.Requests, "One request, never one per edition.");
        StringAssert.Contains(rig.Uris.Single(), "works/OL45804W/editions.json?limit=20");
    }

    [TestMethod]
    public async Task AFailedEditionReadIsTypedAndNeverRetried()
    {
        using var rig = Rig.With(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var read = await rig.Provider.GetWorkEditionTitlesAsync(
            new SourceRef("openlibrary", "OL45804W", ExternalReferenceEntityType.Work), rig.EditionsBudget(), CancellationToken.None);

        Assert.AreNotEqual(DiscoveryOutcome.Ok, read.Outcome);
        Assert.AreEqual(1, rig.Requests, "A single attempt: there is no retry on this read.");
        Assert.IsNotEmpty(read.Notes);
    }

    [TestMethod]
    public async Task OnlyAWellFormedWorkIdIsReadAtAllAndNothingElseIsRequested()
    {
        using var rig = Rig.With(Json("""{"entries":[]}"""));

        var notAWork = await rig.Provider.GetWorkEditionTitlesAsync(
            new SourceRef("openlibrary", "OL1M", ExternalReferenceEntityType.Edition), rig.EditionsBudget(), CancellationToken.None);
        var malformed = await rig.Provider.GetWorkEditionTitlesAsync(
            new SourceRef("openlibrary", "not-a-work", ExternalReferenceEntityType.Work), rig.EditionsBudget(), CancellationToken.None);

        Assert.AreNotEqual(DiscoveryOutcome.Ok, notAWork.Outcome);
        Assert.AreNotEqual(DiscoveryOutcome.Ok, malformed.Outcome);
        Assert.AreEqual(0, rig.Requests);
    }
}
