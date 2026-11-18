// Expected-value arrays in assertions are clearer inline than as static fields; CA1861 targets repeated calls.
#pragma warning disable CA1861
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Catalog.Answers;
using FamilyLibrarian.Application.Catalog.Discovery;
using FamilyLibrarian.Domain.Catalog;
using Claim = FamilyLibrarian.Application.Catalog.Discovery.PositionClaim;

namespace FamilyLibrarian.Infrastructure.Tests.Catalog;

/// <summary>
/// The deterministic answer rules over controlled, fictional entries and a pinned day (2026-11-17). Every fact is made up; nothing
/// here is a provider response or a real publication date. The policy functions are pure, so no clock, I/O or model is involved.
/// </summary>
[TestClass]
public sealed class CatalogAnswerPolicyTests
{
    private static readonly DateOnly Today = new(2026, 11, 17);
    private static readonly CatalogPolicyScope Full = new(Exhausted: true, SavedOnly: false, SourceGaps: false);
    private static readonly CatalogPolicyScope Partial = new(Exhausted: false, SavedOnly: false, SourceGaps: false);

    private static SourceRef Work(string id) => new("fiction", id, ExternalReferenceEntityType.Work);

    private static Claim At(decimal sort, SeriesOrderingKind? order = null) => new(sort.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), sort, order);

    private static CatalogAnswerEntry Entry(
        string id,
        string title,
        DateOnly? date,
        DatePrecision precision = DatePrecision.Day,
        DateBasis basis = DateBasis.Original,
        PublicationStatus status = PublicationStatus.Unknown,
        CatalogWorkClass workClass = CatalogWorkClass.Unknown,
        MembershipRole role = MembershipRole.Unknown,
        string? language = null,
        ExternalReferenceEntityType kind = ExternalReferenceEntityType.Work,
        params Claim[] positions) => new()
    {
        Source = new SourceRef("fiction", id, kind),
        ProviderId = "fiction",
        Title = title,
        Publication = date is null ? PublicationEvidence.NotStated : PublicationEvidence.Dated(date.Value, precision, basis, status),
        Class = workClass,
        Role = role,
        Language = language,
        Positions = positions
    };

    private static CatalogAnswerEntry Positioned(string id, string title, DateOnly? date, params Claim[] positions) =>
        Entry(id, title, date, positions: positions);

    // ======================================================================== F01 latest published

    [TestMethod]
    public void LatestIsTheNewestOriginalDateNotTheHighestNumberOrTheNewestReprint()
    {
        var entries = new[]
        {
            Positioned("a", "Alder", new DateOnly(2001, 5, 1), At(1)),
            Positioned("b", "Birch", new DateOnly(2005, 3, 9), At(2)),
            // The highest number, but published before Birch: the maximum position is not the latest.
            Positioned("c", "Cedar", new DateOnly(2003, 8, 2), At(3)),
            // A recent reprint of Alder: an edition date, never an original publication.
            Entry("a2", "Alder (reissue)", new DateOnly(2024, 1, 1), basis: DateBasis.EditionDerived, positions: At(1))
        };

        var result = CatalogAnswerPolicy.Latest(entries, Today, Full);

        CollectionAssert.AreEqual(new[] { "Birch" }, result.Entries.Select(entry => entry.Title).ToArray());
        CollectionAssert.AreEqual(new[] { "Alder (reissue)" }, result.Groups.Single(group => group.Code == "edition-derived-dates").Entries.Select(entry => entry.Title).ToArray());
        // The reissue is known only by an edition date past Birch's, so it cannot be ruled out as a newer work: said, not hidden.
        Assert.AreEqual(CatalogAnswerOutcome.Qualified, result.Outcome);
        Assert.IsTrue(result.Qualifiers.Contains(CatalogAnswerCodes.EditionNotOriginal));
    }

    [TestMethod]
    public void AnEditionDateOlderThanTheSelectedLatestProvesTheWorkIsNotNewer()
    {
        var entries = new[]
        {
            Entry("b", "Birch", new DateOnly(2005, 3, 9)),
            Entry("a2", "Old Edition Only", new DateOnly(1999, 1, 1), basis: DateBasis.EditionDerived)
        };

        var result = CatalogAnswerPolicy.Latest(entries, Today, Full);

        Assert.AreEqual(CatalogAnswerOutcome.Answered, result.Outcome);
        Assert.AreEqual("Birch", result.Entries.Single().Title);
    }

    [TestMethod]
    public void AnIncompleteScopeOnlyEverGivesTheLatestAmongTheKnownEntries()
    {
        var entries = new[] { Positioned("a", "Alder", new DateOnly(2001, 5, 1), At(1)), Positioned("b", "Birch", new DateOnly(2005, 3, 9), At(2)) };

        var result = CatalogAnswerPolicy.Latest(entries, Today, Partial);

        Assert.AreEqual(CatalogAnswerOutcome.Qualified, result.Outcome);
        CollectionAssert.AreEqual(new[] { "Birch" }, result.Entries.Select(entry => entry.Title).ToArray());
        Assert.IsTrue(result.Qualifiers.Contains(CatalogAnswerCodes.IncompleteCoverage));
    }

    [TestMethod]
    public void AnUnverifiedDateBasisIsShownButNeverBecomesAnOriginalPublicationFact()
    {
        var entries = new[]
        {
            Entry("a", "Alder", new DateOnly(2001, 5, 1)),
            Entry("b", "Birch", new DateOnly(2005, 3, 9), basis: DateBasis.Unknown)
        };

        var result = CatalogAnswerPolicy.Latest(entries, Today, Full);

        Assert.AreEqual(CatalogAnswerOutcome.Qualified, result.Outcome);
        Assert.AreEqual("Birch", result.Entries.Single().Title);
        Assert.IsTrue(result.Qualifiers.Contains(CatalogAnswerCodes.DateBasisUnverified));
    }

    [TestMethod]
    public void AnUndatedMemberKeepsALatestAnswerFromBeingPlain()
    {
        var entries = new[] { Entry("a", "Alder", new DateOnly(2001, 5, 1)), Entry("u", "Undated", null) };

        var result = CatalogAnswerPolicy.Latest(entries, Today, Full);

        Assert.AreEqual(CatalogAnswerOutcome.Qualified, result.Outcome);
        Assert.AreEqual("Alder", result.Entries.Single().Title);
        Assert.IsTrue(result.Qualifiers.Contains(CatalogAnswerCodes.UndatedEntriesPresent));
        Assert.AreEqual("Undated", result.Groups.Single(group => group.Code == "undated").Entries.Single().Title);
    }

    [TestMethod]
    public void AFutureDatedEntryIsNotThePublishedLatest()
    {
        var entries = new[] { Entry("a", "Alder", new DateOnly(2001, 5, 1)), Entry("f", "Fir", new DateOnly(2027, 5, 11)) };

        var result = CatalogAnswerPolicy.Latest(entries, Today, Full);

        Assert.AreEqual("Alder", result.Entries.Single().Title);
        Assert.AreEqual("Fir", result.Groups.Single(group => group.Code == "future-dated").Entries.Single().Title);
    }

    [TestMethod]
    public void NoDatedPublishedEntryIsInsufficientEvidenceNotAnAnswerOrAnAbsence()
    {
        var result = CatalogAnswerPolicy.Latest([Entry("u", "Undated", null)], Today, Full);

        Assert.AreEqual(CatalogAnswerOutcome.InsufficientEvidence, result.Outcome);
        Assert.AreEqual(0, result.Entries.Count);
        Assert.IsTrue(result.Reasons.Contains(CatalogAnswerCodes.NoDatedPublishedEntry));
    }

    [TestMethod]
    public void AnEmptyFullyReadScopeIsNoVerifiedMatchButAnEmptyPartialOneIsNot()
    {
        Assert.AreEqual(CatalogAnswerOutcome.NoVerifiedMatch, CatalogAnswerPolicy.Latest([], Today, Full).Outcome);
        Assert.AreEqual(CatalogAnswerOutcome.InsufficientEvidence, CatalogAnswerPolicy.Latest([], Today, Partial).Outcome);
        Assert.AreEqual(CatalogAnswerOutcome.InsufficientEvidence, CatalogAnswerPolicy.Latest([], Today, new CatalogPolicyScope(true, false, true)).Outcome);
    }

    // =========================================================================== F02 upcoming / next release

    [TestMethod]
    public void AFutureWorkIsAnAttributedClaimWhileAFutureEditionStaysAnEdition()
    {
        var entries = new[]
        {
            Entry("w", "Work Soon", new DateOnly(2027, 5, 11), basis: DateBasis.Unknown),
            Entry("e", "Reprint Soon", new DateOnly(2027, 2, 1), kind: ExternalReferenceEntityType.Edition),
            Entry("r", "Reissue", new DateOnly(2027, 3, 1), basis: DateBasis.EditionDerived)
        };

        var result = CatalogAnswerPolicy.Upcoming(entries, Today, nextOnly: false, Full);

        Assert.AreEqual(CatalogAnswerOutcome.Qualified, result.Outcome);
        CollectionAssert.AreEqual(new[] { "Work Soon" }, result.Entries.Select(entry => entry.Title).ToArray());
        Assert.AreEqual(2, result.Groups.Single(group => group.Code == "future-editions").Entries.Count);
        // Unknown basis and unknown status are stated, never turned into "scheduled".
        CollectionAssert.IsSubsetOf(
            new[] { CatalogAnswerCodes.DateBasisUnverified, CatalogAnswerCodes.ReleaseDateAttributed, CatalogAnswerCodes.StatusUnverified, CatalogAnswerCodes.EditionNotOriginal },
            result.Qualifiers.ToArray());
    }

    [TestMethod]
    public void OnlyASourceStatedForthcomingOriginalDateIsAPlainUpcomingAnswer()
    {
        var entry = Entry("w", "Work Soon", new DateOnly(2027, 5, 11), status: PublicationStatus.Forthcoming);

        var result = CatalogAnswerPolicy.Upcoming([entry], Today, nextOnly: true, Full);

        Assert.AreEqual(CatalogAnswerOutcome.Answered, result.Outcome);
        Assert.AreEqual(0, result.Qualifiers.Count);
    }

    [TestMethod]
    public void APublishedStatusOnAFutureDateIsAConflictNotAFact()
    {
        var entry = Entry("w", "Odd", new DateOnly(2027, 5, 11), status: PublicationStatus.Published);

        var result = CatalogAnswerPolicy.Upcoming([entry], Today, nextOnly: false, Full);

        Assert.IsTrue(result.Qualifiers.Contains(CatalogAnswerCodes.StatusDateConflict));
        Assert.AreEqual(CatalogAnswerOutcome.Qualified, result.Outcome);
    }

    [TestMethod]
    public void TheNextReleaseIsTheEarliestFutureWorkAndOverlappingMonthsAreKept()
    {
        var entries = new[]
        {
            Entry("a", "March", new DateOnly(2027, 3, 1), DatePrecision.Month, status: PublicationStatus.Forthcoming),
            Entry("b", "March First", new DateOnly(2027, 3, 1), DatePrecision.Day, status: PublicationStatus.Forthcoming),
            Entry("c", "June", new DateOnly(2027, 6, 1), status: PublicationStatus.Forthcoming)
        };

        var result = CatalogAnswerPolicy.Upcoming(entries, Today, nextOnly: true, Full);

        // "March" (a whole month) and 1 March (a day inside it) are not ordered: both stay, June is strictly later and goes.
        CollectionAssert.AreEquivalent(new[] { "March", "March First" }, result.Entries.Select(entry => entry.Title).ToArray());
        Assert.IsTrue(result.Qualifiers.Contains(CatalogAnswerCodes.TiedOrOverlappingDates));
    }

    [TestMethod]
    public void AnnouncedButUndatedIsAKnownFactNotAnAbsenceAndNotStatedIsNeither()
    {
        var announced = new CatalogAnswerEntry { Source = Work("k"), ProviderId = "fiction", Title = "Announced", Publication = PublicationEvidence.KnownUndated() };
        var silent = Entry("n", "Silent", null);

        var result = CatalogAnswerPolicy.Upcoming([announced, silent], Today, nextOnly: false, Full);

        Assert.AreEqual(CatalogAnswerOutcome.Qualified, result.Outcome);
        Assert.AreEqual(0, result.Entries.Count);
        Assert.AreEqual("Announced", result.Groups.Single(group => group.Code == "announced-undated").Entries.Single().Title);
        Assert.IsTrue(result.Reasons.Contains(CatalogAnswerCodes.NoFutureDatedEntry));
        Assert.IsTrue(result.Qualifiers.Contains(CatalogAnswerCodes.UndatedEntriesPresent), "A silent source is not an announcement and not an absence.");
    }

    [TestMethod]
    public void NoFutureEntryIsAScopedNegativeOnlyWhenTheScopeWasFullyReadAndCleanly()
    {
        var past = new[] { Entry("a", "Alder", new DateOnly(2001, 5, 1)) };

        Assert.AreEqual(CatalogAnswerOutcome.NoVerifiedMatch, CatalogAnswerPolicy.Upcoming(past, Today, false, Full).Outcome);
        Assert.AreEqual(CatalogAnswerOutcome.InsufficientEvidence, CatalogAnswerPolicy.Upcoming(past, Today, false, Partial).Outcome);
        Assert.AreEqual(CatalogAnswerOutcome.InsufficientEvidence, CatalogAnswerPolicy.Upcoming(past, Today, false, new CatalogPolicyScope(true, false, true)).Outcome);
    }

    // =========================================================================== F03 partial dates and intervals

    [TestMethod]
    public void PartialDatesAreIntervalsAgainstThePinnedDay()
    {
        CatalogDateTemporal Of(DateOnly date, DatePrecision precision) =>
            CatalogAnswerPolicy.TemporalOf(PublicationEvidence.Dated(date, precision, DateBasis.Original), Today);

        Assert.AreEqual(CatalogDateTemporal.Past, Of(new DateOnly(2025, 1, 1), DatePrecision.Year));
        Assert.AreEqual(CatalogDateTemporal.IncludesToday, Of(new DateOnly(2026, 1, 1), DatePrecision.Year));
        Assert.AreEqual(CatalogDateTemporal.Future, Of(new DateOnly(2027, 1, 1), DatePrecision.Year));
        Assert.AreEqual(CatalogDateTemporal.IncludesToday, Of(new DateOnly(2026, 11, 1), DatePrecision.Month));
        Assert.AreEqual(CatalogDateTemporal.Past, Of(new DateOnly(2026, 10, 1), DatePrecision.Month));
        Assert.AreEqual(CatalogDateTemporal.Future, Of(new DateOnly(2026, 12, 1), DatePrecision.Month));
        Assert.AreEqual(CatalogDateTemporal.Past, Of(Today, DatePrecision.Day), "A full date that is today has happened.");
        Assert.AreEqual(CatalogDateTemporal.Future, Of(Today.AddDays(1), DatePrecision.Day));
    }

    [TestMethod]
    public void AYearAndADayInsideItAreTiedNotOrderedBySyntheticJanuaryFirst()
    {
        var entries = new[]
        {
            Entry("y", "Year Only", new DateOnly(2020, 1, 1), DatePrecision.Year),
            Entry("d", "Mid Year", new DateOnly(2020, 6, 1))
        };

        var result = CatalogAnswerPolicy.Latest(entries, Today, Full);

        // A year-only date is carried as 1 January, so ordering by that value would call "Mid Year" later; the intervals say it is not provable.
        CollectionAssert.AreEquivalent(new[] { "Year Only", "Mid Year" }, result.Entries.Select(entry => entry.Title).ToArray());
        Assert.AreEqual(CatalogAnswerOutcome.Qualified, result.Outcome);
        Assert.IsTrue(result.Qualifiers.Contains(CatalogAnswerCodes.TiedOrOverlappingDates));
    }

    [TestMethod]
    public void DisjointIntervalsAreOrderedAndAnIntervalContainingTodayIsNotPublished()
    {
        var entries = new[]
        {
            Entry("a", "Earlier", new DateOnly(2020, 1, 1), DatePrecision.Year),
            Entry("b", "Later", new DateOnly(2022, 3, 4)),
            Entry("t", "This Year", new DateOnly(2026, 1, 1), DatePrecision.Year)
        };

        var result = CatalogAnswerPolicy.Latest(entries, Today, Full);

        Assert.AreEqual("Later", result.Entries.Single().Title);
        Assert.AreEqual(CatalogAnswerOutcome.Qualified, result.Outcome, "An interval that includes today could be newer and is not definitely published.");
        Assert.IsTrue(result.Qualifiers.Contains(CatalogAnswerCodes.IncludesTodayInterval));
    }

    // ============================================================================== F04 position, order, next

    [TestMethod]
    public void BookThreeIsNotBookThreeAndAHalfAndAFractionIsItsOwnPosition()
    {
        var entries = new[]
        {
            Positioned("a", "Third", new DateOnly(2003, 1, 1), At(3)),
            Positioned("b", "Third And A Half", new DateOnly(2004, 1, 1), At(3.5m)),
            Positioned("c", "Fourth", new DateOnly(2005, 1, 1), At(4))
        };

        Assert.AreEqual("Third", CatalogAnswerPolicy.Position(entries, 3m, SeriesOrderingKind.Unspecified, Full).Entries.Single().Title);
        Assert.AreEqual("Third And A Half", CatalogAnswerPolicy.Position(entries, 3.5m, SeriesOrderingKind.Unspecified, Full).Entries.Single().Title);
        Assert.AreEqual(CatalogAnswerOutcome.Answered, CatalogAnswerPolicy.Position(entries, 3m, SeriesOrderingKind.Unspecified, Full).Outcome);
    }

    [TestMethod]
    public void TwoWorksAtOnePositionAreBothKeptAsATie()
    {
        var entries = new[] { Positioned("a", "First Claim", null, At(2)), Positioned("b", "Second Claim", null, At(2)) };

        var result = CatalogAnswerPolicy.Position(entries, 2m, SeriesOrderingKind.Unspecified, Full);

        Assert.AreEqual(CatalogAnswerOutcome.Qualified, result.Outcome);
        Assert.AreEqual(2, result.Entries.Count);
        Assert.IsTrue(result.Qualifiers.Contains(CatalogAnswerCodes.TiedPosition));
    }

    [TestMethod]
    public void APositionNobodyClaimsIsNeverInventedOrPaddedWithOtherWorks()
    {
        var entries = new[] { Positioned("a", "First", null, At(1)), Positioned("u", "Unnumbered", null) };

        var full = CatalogAnswerPolicy.Position(entries, 25m, SeriesOrderingKind.Unspecified, Full);
        var partial = CatalogAnswerPolicy.Position(entries, 25m, SeriesOrderingKind.Unspecified, Partial);

        Assert.AreEqual(CatalogAnswerOutcome.NoVerifiedMatch, full.Outcome);
        Assert.AreEqual(0, full.Entries.Count);
        Assert.AreEqual(CatalogAnswerOutcome.InsufficientEvidence, partial.Outcome);
        Assert.AreEqual(0, partial.Entries.Count);
        Assert.IsTrue(partial.Reasons.Contains(CatalogAnswerCodes.PositionNotInCheckedEntries));
    }

    [TestMethod]
    public void DistinctOrdersAreNeverMixedAndMustBeChosen()
    {
        var entries = new[]
        {
            Positioned("p", "Published Second", new DateOnly(2002, 1, 1), At(2, SeriesOrderingKind.Publication)),
            Positioned("c", "Chronological Second", new DateOnly(2008, 1, 1), At(2, SeriesOrderingKind.Chronological))
        };

        var ambiguous = CatalogAnswerPolicy.Position(entries, 2m, SeriesOrderingKind.Unspecified, Full);
        var publication = CatalogAnswerPolicy.Position(entries, 2m, SeriesOrderingKind.Publication, Full);
        var chronological = CatalogAnswerPolicy.Position(entries, 2m, SeriesOrderingKind.Chronological, Full);

        Assert.AreEqual(CatalogAnswerOutcome.Clarification, ambiguous.Outcome);
        Assert.IsTrue(ambiguous.Reasons.Contains(CatalogAnswerCodes.OrderAmbiguous));
        CollectionAssert.AreEqual(new[] { SeriesOrderingKind.Publication, SeriesOrderingKind.Chronological }, ambiguous.AvailableOrders.ToArray());
        Assert.AreEqual("Published Second", publication.Entries.Single().Title);
        Assert.AreEqual("Chronological Second", chronological.Entries.Single().Title);
    }

    [TestMethod]
    public void AClaimWithNoStatedOrderingIsNotReadAsTheRequestedOrdering()
    {
        var entries = new[] { Positioned("a", "Unstated", null, At(2)) };

        var result = CatalogAnswerPolicy.Position(entries, 2m, SeriesOrderingKind.Publication, Full);

        Assert.AreEqual(0, result.Entries.Count);
        Assert.AreEqual("Unstated", result.Groups.Single(group => group.Code == "ordering-unstated").Entries.Single().Title);
    }

    [TestMethod]
    public void AfterAnAnchorIsTheNextStrictlyGreaterKnownPositionIncludingFractions()
    {
        var entries = new[]
        {
            Positioned("a", "Third", null, At(3)),
            Positioned("b", "Three And A Half", null, At(3.5m)),
            Positioned("c", "Fourth", null, At(4))
        };

        var result = CatalogAnswerPolicy.AfterAnchor(entries, CatalogAnswerEntry.KeyOf(Work("a")), null, SeriesOrderingKind.Unspecified, Full);

        Assert.AreEqual("Three And A Half", result.Entries.Single().Title);
        Assert.AreEqual(CatalogAnswerOutcome.Answered, result.Outcome);
    }

    [TestMethod]
    public void AfterAnAnchorKeepsTiesAndNeverCountsTheAnchorsOwnPositionAsLater()
    {
        var entries = new[]
        {
            Positioned("a", "Third", null, At(3)),
            Positioned("twin", "Third Twin", null, At(3)),
            Positioned("b", "Fourth One", null, At(4)),
            Positioned("c", "Fourth Two", null, At(4))
        };

        var result = CatalogAnswerPolicy.AfterAnchor(entries, CatalogAnswerEntry.KeyOf(Work("a")), null, SeriesOrderingKind.Unspecified, Full);

        CollectionAssert.AreEquivalent(new[] { "Fourth One", "Fourth Two" }, result.Entries.Select(entry => entry.Title).ToArray());
        Assert.IsTrue(result.Qualifiers.Contains(CatalogAnswerCodes.TiedPosition));
    }

    [TestMethod]
    public void AfterAnAnchorInAPartialListIsTheNextKnownEntryNotAProvenSuccessor()
    {
        var entries = new[] { Positioned("a", "Third", null, At(3)), Positioned("c", "Fifth", null, At(5)) };

        var result = CatalogAnswerPolicy.AfterAnchor(entries, CatalogAnswerEntry.KeyOf(Work("a")), null, SeriesOrderingKind.Unspecified, Partial);

        Assert.AreEqual("Fifth", result.Entries.Single().Title);
        Assert.AreEqual(CatalogAnswerOutcome.Qualified, result.Outcome);
        Assert.IsTrue(result.Qualifiers.Contains(CatalogAnswerCodes.NextKnownNotProvenImmediate));
    }

    [TestMethod]
    public void AnUnnumberedEntryMeansTheNextNumberedOneIsNotProvenImmediate()
    {
        var entries = new[] { Positioned("a", "Third", null, At(3)), Positioned("c", "Fifth", null, At(5)), Positioned("u", "Unnumbered", null) };

        var result = CatalogAnswerPolicy.AfterAnchor(entries, CatalogAnswerEntry.KeyOf(Work("a")), null, SeriesOrderingKind.Unspecified, Full);

        Assert.AreEqual(CatalogAnswerOutcome.Qualified, result.Outcome);
        Assert.IsTrue(result.Qualifiers.Contains(CatalogAnswerCodes.UnnumberedEntriesPresent));
    }

    [TestMethod]
    public void AnAnchorWithNoKnownPlaceAsksTheUserAndAConflictedOneIsInsufficient()
    {
        var unplaced = new[] { Positioned("a", "Floating", null), Positioned("b", "Fourth", null, At(4)) };
        var conflicted = new[] { Positioned("a", "Disputed", null, At(2), At(7)), Positioned("b", "Fourth", null, At(4)) };

        var ask = CatalogAnswerPolicy.AfterAnchor(unplaced, CatalogAnswerEntry.KeyOf(Work("a")), null, SeriesOrderingKind.Unspecified, Full);
        var dispute = CatalogAnswerPolicy.AfterAnchor(conflicted, CatalogAnswerEntry.KeyOf(Work("a")), null, SeriesOrderingKind.Unspecified, Full);
        var missing = CatalogAnswerPolicy.AfterAnchor(unplaced, CatalogAnswerEntry.KeyOf(Work("zzz")), null, SeriesOrderingKind.Unspecified, Full);

        Assert.AreEqual(CatalogAnswerOutcome.Clarification, ask.Outcome);
        Assert.IsTrue(ask.Reasons.Contains(CatalogAnswerCodes.AnchorPositionUnknown));
        Assert.AreEqual(CatalogAnswerOutcome.InsufficientEvidence, dispute.Outcome);
        Assert.IsTrue(dispute.Reasons.Contains(CatalogAnswerCodes.AnchorPositionConflict));
        Assert.IsTrue(missing.Reasons.Contains(CatalogAnswerCodes.AnchorNotFound));
    }

    [TestMethod]
    public void NothingAfterTheLastKnownEntryIsAScopedNegativeOnlyWhenFullyRead()
    {
        var entries = new[] { Positioned("a", "Last", null, At(9)) };

        Assert.AreEqual(CatalogAnswerOutcome.NoVerifiedMatch, CatalogAnswerPolicy.AfterAnchor(entries, CatalogAnswerEntry.KeyOf(Work("a")), null, SeriesOrderingKind.Unspecified, Full).Outcome);
        Assert.AreEqual(CatalogAnswerOutcome.InsufficientEvidence, CatalogAnswerPolicy.AfterAnchor(entries, CatalogAnswerEntry.KeyOf(Work("a")), null, SeriesOrderingKind.Unspecified, Partial).Outcome);
    }

    [TestMethod]
    public void AfterAStatedPositionNeedsNoEntryIdentity()
    {
        var entries = new[] { Positioned("a", "Third", null, At(3)), Positioned("b", "Fourth", null, At(4)) };

        var result = CatalogAnswerPolicy.AfterAnchor(entries, null, 3m, SeriesOrderingKind.Unspecified, Full);

        Assert.AreEqual("Fourth", result.Entries.Single().Title);
    }

    // ======================================================================== F06 collections, classes, filters

    [TestMethod]
    public void ACollectionAnOmnibusAndAnAdaptationAreNeverTheLatestOriginal()
    {
        var entries = new[]
        {
            Entry("a", "Alder", new DateOnly(2001, 5, 1)),
            Entry("o", "The Omnibus", new DateOnly(2015, 1, 1), workClass: CatalogWorkClass.Collection),
            Entry("g", "The Graphic Version", new DateOnly(2018, 1, 1), workClass: CatalogWorkClass.GraphicAdaptation),
            Entry("s", "A Study Guide", new DateOnly(2019, 1, 1), workClass: CatalogWorkClass.StudyGuide)
        };

        var result = CatalogAnswerPolicy.Latest(entries, Today, Full);

        Assert.AreEqual("Alder", result.Entries.Single().Title);
        Assert.AreEqual(3, result.Groups.Single(group => group.Code == "excluded-by-class").Entries.Count);
    }

    [TestMethod]
    public void UnknownClassificationIsNotPromotedToANovelWhenNovelsAreAskedFor()
    {
        var entries = new[]
        {
            Entry("a", "Known Novel", new DateOnly(2001, 5, 1), workClass: CatalogWorkClass.Novel),
            Entry("u", "Unclassified", new DateOnly(2010, 1, 1))
        };

        var result = CatalogAnswerPolicy.Latest(entries, Today, Full, new CatalogAnswerFilter(NovelOnly: true));

        Assert.AreEqual("Known Novel", result.Entries.Single().Title);
        Assert.AreEqual("Unclassified", result.Groups.Single(group => group.Code == "class-unverified").Entries.Single().Title);
        Assert.AreEqual(CatalogAnswerOutcome.Qualified, result.Outcome);
        Assert.IsTrue(result.Qualifiers.Contains(CatalogAnswerCodes.ClassUnverified));
    }

    [TestMethod]
    public void AnUnknownMembershipRoleIsNotMainAndAPrimaryFlagNeverImpliesIt()
    {
        var entries = new[]
        {
            Entry("m", "Main One", new DateOnly(2001, 5, 1), role: MembershipRole.Main),
            Entry("c", "Companion", new DateOnly(2012, 1, 1), role: MembershipRole.Companion),
            Entry("u", "Role Unknown", new DateOnly(2014, 1, 1))
        };

        var result = CatalogAnswerPolicy.Latest(entries, Today, Full, new CatalogAnswerFilter(MainOnly: true));

        Assert.AreEqual("Main One", result.Entries.Single().Title);
        Assert.AreEqual("Companion", result.Groups.Single(group => group.Code == "excluded-by-filter").Entries.Single().Title);
        Assert.AreEqual("Role Unknown", result.Groups.Single(group => group.Code == "role-unverified").Entries.Single().Title);
        Assert.IsTrue(result.Qualifiers.Contains(CatalogAnswerCodes.RoleUnverified));
    }

    [TestMethod]
    public void ALanguageFilterKeepsATranslationApartAndUnknownLanguageUnknown()
    {
        var entries = new[]
        {
            Entry("a", "English One", new DateOnly(2001, 5, 1), language: "en"),
            Entry("p", "Portuguese One", new DateOnly(2012, 1, 1), language: "pt"),
            Entry("u", "Language Unknown", new DateOnly(2013, 1, 1))
        };

        var result = CatalogAnswerPolicy.Latest(entries, Today, Full, new CatalogAnswerFilter(Language: "en"));

        Assert.AreEqual("English One", result.Entries.Single().Title);
        Assert.AreEqual("Language Unknown", result.Groups.Single(group => group.Code == "language-unverified").Entries.Single().Title);
        Assert.IsTrue(result.Qualifiers.Contains(CatalogAnswerCodes.LanguageUnverified));
    }

    [TestMethod]
    public void ACollectionThatClaimsAPositionIsFoundButFlaggedNotOriginal()
    {
        var entries = new[] { Entry("o", "Omnibus", null, workClass: CatalogWorkClass.Collection, positions: At(1)) };

        var result = CatalogAnswerPolicy.Position(entries, 1m, SeriesOrderingKind.Unspecified, Full);

        Assert.AreEqual(CatalogAnswerOutcome.Qualified, result.Outcome);
        Assert.IsTrue(result.Qualifiers.Contains(CatalogAnswerCodes.ClassNotOriginal));
    }

    // ================================================================================ F05 authors and namesakes

    [TestMethod]
    public void AuthorWorksSeparateTheCreditedAuthorFromEditorsTranslatorsAndUnverifiedCredits()
    {
        var author = CatalogSubjectRef.FromSource(CatalogSubjectKind.Author, "A. Writer", new SourceRef("fiction", "auth-1", ExternalReferenceEntityType.Author));
        var other = new SourceRef("fiction", "auth-2", ExternalReferenceEntityType.Author);
        CatalogAnswerEntry With(string id, string title, params CatalogEntryCredit[] credits) =>
            Entry(id, title, new DateOnly(2001, 1, 1)) with { Credits = credits };
        var entries = new[]
        {
            With("1", "Written", new CatalogEntryCredit(0, "A. Writer", null, author.Native, null)),
            With("2", "Translated", new CatalogEntryCredit(0, "A. Writer", "translator", author.Native, null)),
            // A namesake: same display name, a different native id. It must never count as this author.
            With("3", "Namesake Work", new CatalogEntryCredit(0, "A. Writer", null, other, null)),
            With("4", "No Credit Ids", new CatalogEntryCredit(0, "A. Writer", null, null, null))
        };

        var result = CatalogAnswerPolicy.AuthorWorks(entries, author, Full);

        CollectionAssert.AreEqual(new[] { "Written" }, result.Entries.Select(entry => entry.Title).ToArray());
        Assert.AreEqual("Translated", result.Groups.Single(group => group.Code == "other-roles").Entries.Single().Title);
        CollectionAssert.AreEquivalent(new[] { "Namesake Work", "No Credit Ids" }, result.Groups.Single(group => group.Code == "credit-unverified").Entries.Select(entry => entry.Title).ToArray());
        // A known-works page is never an exhaustive bibliography, even when its checked scope was read to the end.
        Assert.AreEqual(CatalogAnswerOutcome.Qualified, result.Outcome);
        Assert.IsTrue(result.Qualifiers.Contains(CatalogAnswerCodes.ListNotExhaustive));
        Assert.IsTrue(result.Qualifiers.Contains(CatalogAnswerCodes.CreditUnverified));
    }

    [TestMethod]
    public void AuthorWorksMatchASavedAuthorByIdNeverByName()
    {
        var id = Guid.NewGuid();
        var author = CatalogSubjectRef.Saved(CatalogSubjectKind.Author, "A. Writer", id);
        var entries = new[]
        {
            Entry("1", "Mine", new DateOnly(2001, 1, 1)) with { Credits = [new CatalogEntryCredit(0, "A. Writer", null, null, id)] },
            Entry("2", "Namesake", new DateOnly(2001, 1, 1)) with { Credits = [new CatalogEntryCredit(0, "A. Writer", null, null, Guid.NewGuid())] }
        };

        var result = CatalogAnswerPolicy.AuthorWorks(entries, author, Full);

        Assert.AreEqual("Mine", result.Entries.Single().Title);
    }

    [TestMethod]
    public void AuthorSeriesKeepsDerivedEdgesLabelledAndIsNeverExhaustive()
    {
        var native = new CatalogAnswerSeries("Alpha", new SourceRef("fiction", "s1", ExternalReferenceEntityType.Series), null, RelationshipEvidence.Native, null, null, null);
        var derived = new CatalogAnswerSeries("Beta", new SourceRef("fiction", "s2", ExternalReferenceEntityType.Series), null, RelationshipEvidence.Derived("author-works-first-page"), null, null, null);

        var result = CatalogAnswerPolicy.AuthorSeries([derived, native], Full, out var ordered);

        CollectionAssert.AreEqual(new[] { "Alpha", "Beta" }, ordered.Select(item => item.Label).ToArray());
        Assert.AreEqual(CatalogAnswerOutcome.Qualified, result.Outcome);
        Assert.IsTrue(result.Qualifiers.Contains(CatalogAnswerCodes.DerivedEdges));
    }

    // ============================================================================================ named title

    [TestMethod]
    public void ANamedTitleReturnsOnlyExactTitleRecordsAndNeverRelabelsKeywordHits()
    {
        var entries = new[]
        {
            Entry("1", "The Winter Garden", new DateOnly(2010, 1, 1)),
            Entry("2", "Winter Garden Cookbook", new DateOnly(2015, 1, 1))
        };

        var hit = CatalogAnswerPolicy.PublicationForTitle(entries, "winter garden", Today, Partial);
        var miss = CatalogAnswerPolicy.PublicationForTitle(entries, "Garden Of Spring", Today, Partial);

        Assert.AreEqual("The Winter Garden", hit.Entries.Single().Title);
        Assert.AreEqual(CatalogAnswerOutcome.NoVerifiedMatch, miss.Outcome);
        Assert.AreEqual(0, miss.Entries.Count);
        Assert.IsTrue(miss.Qualifiers.Contains(CatalogAnswerCodes.IncompleteCoverage));
        Assert.IsTrue(miss.Reasons.Contains(CatalogAnswerCodes.NoExactTitleMatch));
    }

    [TestMethod]
    public void TwoRecordsOfOneSourceUnderOneTitleAreNamesakesTheUserChoosesBetween()
    {
        var entries = new[] { Entry("1", "Red Door", new DateOnly(2001, 1, 1)), Entry("2", "Red Door", new DateOnly(1999, 1, 1)) };

        var result = CatalogAnswerPolicy.PublicationForTitle(entries, "Red Door", Today, Partial);

        Assert.AreEqual(CatalogAnswerOutcome.Clarification, result.Outcome);
        Assert.AreEqual(2, result.Entries.Count);
        Assert.IsTrue(result.Reasons.Contains(CatalogAnswerCodes.NamesakeChoices));
    }

    [TestMethod]
    public void DifferentSourcesKeepTheirOwnAttributionAndDisagreeingDatesAreFlaggedNotMerged()
    {
        var one = Entry("1", "Blue Door", new DateOnly(2001, 1, 1));
        var two = Entry("9", "Blue Door", new DateOnly(2010, 6, 1)) with { ProviderId = "other-source", Source = new SourceRef("other-source", "9", ExternalReferenceEntityType.Work) };

        var result = CatalogAnswerPolicy.PublicationForTitle([one, two], "Blue Door", Today, Partial);

        Assert.AreEqual(CatalogAnswerOutcome.Qualified, result.Outcome);
        CollectionAssert.AreEquivalent(new[] { "fiction", "other-source" }, result.Entries.Select(entry => entry.ProviderId).ToArray());
        Assert.IsTrue(result.Qualifiers.Contains(CatalogAnswerCodes.MultipleSourceRecords));
        Assert.IsTrue(result.Qualifiers.Contains(CatalogAnswerCodes.ConflictingDates));
    }

    [TestMethod]
    public void ASingleOriginalDatedTitleRecordIsAPlainAnswerButAnUnverifiedBasisIsNot()
    {
        var plain = CatalogAnswerPolicy.PublicationForTitle([Entry("1", "Green Door", new DateOnly(2001, 1, 1))], "Green Door", Today, Full);
        var unverified = CatalogAnswerPolicy.PublicationForTitle([Entry("1", "Green Door", new DateOnly(2001, 1, 1), basis: DateBasis.Unknown)], "Green Door", Today, Full);

        Assert.AreEqual(CatalogAnswerOutcome.Answered, plain.Outcome);
        Assert.AreEqual(CatalogAnswerOutcome.Qualified, unverified.Outcome);
    }

    // ================================================================================== determinism and purity

    [TestMethod]
    public void TheSameEntriesInAnyOrderGiveTheSameAnswer()
    {
        var entries = new[]
        {
            Entry("a", "Alder", new DateOnly(2001, 5, 1), positions: At(1)),
            Entry("b", "Birch", new DateOnly(2005, 3, 9), positions: At(2)),
            Entry("c", "Cedar", new DateOnly(2005, 3, 9), positions: At(2))
        };

        var forward = CatalogAnswerPolicy.Latest(entries, Today, Full);
        var reversed = CatalogAnswerPolicy.Latest([.. entries.Reverse()], Today, Full);

        CollectionAssert.AreEqual(forward.Entries.Select(entry => entry.IdentityKey).ToArray(), reversed.Entries.Select(entry => entry.IdentityKey).ToArray());
        Assert.AreEqual(2, forward.Entries.Count);
        CollectionAssert.AreEqual(forward.Qualifiers.ToArray(), reversed.Qualifiers.ToArray());
    }
}
