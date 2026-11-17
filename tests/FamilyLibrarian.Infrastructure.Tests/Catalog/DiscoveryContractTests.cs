using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Catalog.Discovery;
using FamilyLibrarian.Domain.Catalog;

namespace FamilyLibrarian.Infrastructure.Tests.Catalog;

/// <summary>
/// A1 contract invariants. Every fixture here is docs- or lab-report-derived and constructed in code; none is a captured
/// provider response, and nothing here proves an HTTP chain (A2/A3/A4 and A5 tests must).
/// </summary>
[TestClass]
public sealed class DiscoveryContractTests
{
    private static readonly DateTimeOffset Observed = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly DiscoveryScope Scope = new(DiscoveryOperation.ResolveAuthor, null);

    private static SourceRef Author(string id, string provider = "openlibrary") => new(provider, id, ExternalReferenceEntityType.Author);

    private static SourceRef Series(string id, string provider = "hardcover") => new(provider, id, ExternalReferenceEntityType.Series);

    private static SourceRef Work(string id, string provider = "hardcover") => new(provider, id, ExternalReferenceEntityType.Work);

    // ---- identity ----

    [TestMethod]
    public void SourceRefRejectsAnOverlongOrPaddedIdInsteadOfTruncatingIt()
    {
        Assert.IsFalse(SourceRef.TryCreate("hardcover", new string('9', SourceRef.MaxExternalIdLength + 1), ExternalReferenceEntityType.Series, out var tooLong));
        Assert.IsNull(tooLong);
        Assert.IsFalse(SourceRef.TryCreate("hardcover", " 42", ExternalReferenceEntityType.Series, out _));
        Assert.IsFalse(SourceRef.TryCreate("hardcover", "", ExternalReferenceEntityType.Series, out _));
        Assert.IsTrue(SourceRef.TryCreate("hardcover", new string('9', SourceRef.MaxExternalIdLength), ExternalReferenceEntityType.Series, out var exact));
        Assert.AreEqual(SourceRef.MaxExternalIdLength, exact!.ExternalId.Length);
        Assert.ThrowsExactly<ArgumentException>(() => new SourceRef("hardcover", " 42", ExternalReferenceEntityType.Series));
    }

    [TestMethod]
    public void TheSameNativeIdInDifferentKindsOrProvidersIsADifferentReference()
    {
        Assert.AreNotEqual(Series("7"), Work("7"));
        Assert.AreNotEqual(Series("7"), Series("7", "openlibrary"));
        Assert.AreEqual(Series("7"), Series("7"));
    }

    [TestMethod]
    public void ASeriesWithoutANativeIdIsUnboundAndAWrongKindIdIsRefused()
    {
        var labelOnly = new SeriesRef("Legacy", null);
        Assert.IsFalse(labelOnly.IsBound);
        Assert.IsTrue(new SeriesRef("Legacy", Series("100")).IsBound);
        Assert.ThrowsExactly<ArgumentException>(() => new SeriesRef("Legacy", Work("100")));
        Assert.ThrowsExactly<ArgumentException>(() => new AuthorRef("Frank Herbert", Series("1")));
    }

    [TestMethod]
    public void ADerivedAuthorSeriesEdgeCanTargetANativeSeriesWithoutBecomingNative()
    {
        var edge = new AuthorSeriesEntry(new SeriesRef("The Empyrean", Series("OL-S-1", "openlibrary")), RelationshipEvidence.Derived("author-works-first-page"));

        Assert.AreEqual(RelationshipBasis.Derived, edge.Relationship.Basis);
        Assert.AreEqual("author-works-first-page", edge.Relationship.DerivedScope);
        Assert.IsTrue(edge.Series.IsBound, "The target keeps its native id; only the edge is derived.");
        Assert.AreEqual(RelationshipBasis.Native, RelationshipEvidence.Native.Basis);
        Assert.IsNull(RelationshipEvidence.Native.DerivedScope);
        Assert.ThrowsExactly<ArgumentException>(() => RelationshipEvidence.Derived("Author works, first page"));
    }

    // ---- credits and the legacy projection ----

    [TestMethod]
    public void TwoSameNamedAuthorsWithDifferentIdsAreNotProjectedForTheNameMatchingWriter()
    {
        var projection = LegacyCreditProjection.From(
        [
            new CreditRef(0, "Frank Herbert", "author", Author("OL1A")),
            new CreditRef(1, "Frank Herbert", "author", Author("OL2A"))
        ]);

        Assert.IsFalse(projection.Projectable);
        Assert.AreEqual(LegacyCreditProjection.AmbiguousSameName, projection.ReasonCode);
        Assert.HasCount(0, projection.Credits, "No first-match id is offered to the unchanged writer.");
    }

    [TestMethod]
    public void OneSameNamedCreditWithAnIdAndOneWithoutIsNotProjected()
    {
        var projection = LegacyCreditProjection.From(
        [
            new CreditRef(0, "Frank Herbert", null, Author("OL1A")),
            new CreditRef(1, "Frank Herbert", null, null)
        ]);

        Assert.IsFalse(projection.Projectable);
    }

    [TestMethod]
    public void AnIllustratorWithTheAuthorsNameNeverLendsItsIdToTheAuthorCredit()
    {
        var projection = LegacyCreditProjection.From(
        [
            new CreditRef(0, "Pat Smith", "author", Author("OL10A")),
            new CreditRef(1, "Pat Smith", "illustrator", Author("OL99A")),
            new CreditRef(2, "Dana Lee", "translator", Author("OL5A"))
        ]);

        Assert.IsTrue(projection.Projectable);
        Assert.AreEqual("Pat Smith", string.Join("|", projection.Authors));
        Assert.AreEqual("OL10A", projection.Credits.Single().SourceAuthorId);
    }

    [TestMethod]
    public void RepeatedIdenticalCreditsCollapseAndKeepTheirOrder()
    {
        var projection = LegacyCreditProjection.From(
        [
            new CreditRef(1, "B. Writer", null, Author("OL2A")),
            new CreditRef(0, "A. Writer", null, Author("OL1A")),
            new CreditRef(2, "A. Writer", null, Author("OL1A"))
        ]);

        Assert.IsTrue(projection.Projectable);
        Assert.AreEqual("A. Writer|B. Writer", string.Join("|", projection.Authors));
    }

    // ---- author resolution: the Frank Herbert counterexample ----

    private static AuthorCandidate Candidate(string id, string name, int works, bool corroborated) =>
        new(new AuthorRef(name, Author(id), WorkCount: works), true, corroborated);

    [TestMethod]
    public void ANameAloneNeverSelectsAnAuthorEvenWhenOneHasFarMoreWorks()
    {
        // Lab observation 2026-11-16: the first/third-hit namesakes were an educationist (21 works) and the Dune author (141).
        var educationist = Candidate("OL-EDU", "Frank Herbert", 21, false);
        var dune = Candidate("OL79034A", "Frank Herbert", 141, false);

        var result = AuthorResolutionPolicy.Decide(AuthorAnchor.FromNameOnly("Frank Herbert"), [educationist, dune], Scope, Observed, DiscoveryUsage.None);

        Assert.AreEqual(DiscoveryOutcome.Ambiguous, result.Outcome);
        Assert.HasCount(2, result.Items);
        CollectionAssert.Contains(result.Notes.ToArray(), DiscoveryCodes.AnchorInsufficient);
    }

    [TestMethod]
    public void ASingleNameOnlyCandidateIsStillAChoice()
    {
        var result = AuthorResolutionPolicy.Decide(
            AuthorAnchor.FromNameOnly("Rebecca Yarros"), [Candidate("OL7825177A", "Rebecca Yarros", 59, false)], Scope, Observed, DiscoveryUsage.None);

        Assert.AreEqual(DiscoveryOutcome.Ambiguous, result.Outcome);
    }

    [TestMethod]
    public void ABookAnchorThatOnlyOneNamesakeHasSelectsThatNamesake()
    {
        var anchor = AuthorAnchor.FromNameAndBooks("Frank Herbert", [AnchorBook.FromIsbn13("9780441013593")]);

        var result = AuthorResolutionPolicy.Decide(
            anchor, [Candidate("OL-EDU", "Frank Herbert", 21, false), Candidate("OL79034A", "Frank Herbert", 141, true)], Scope, Observed, DiscoveryUsage.None);

        Assert.AreEqual(DiscoveryOutcome.Ok, result.Outcome);
        Assert.AreEqual("OL79034A", result.Items.Single().Id.ExternalId);
    }

    [TestMethod]
    public void ABookAnchorBothNamesakesHaveOrNeitherHasStaysAChoice()
    {
        var anchor = AuthorAnchor.FromNameAndBooks("Frank Herbert", [AnchorBook.FromSource(Work("w1", "openlibrary"))]);

        var both = AuthorResolutionPolicy.Decide(
            anchor, [Candidate("A", "Frank Herbert", 1, true), Candidate("B", "Frank Herbert", 2, true)], Scope, Observed, DiscoveryUsage.None);
        var neither = AuthorResolutionPolicy.Decide(
            anchor, [Candidate("A", "Frank Herbert", 1, false), Candidate("B", "Frank Herbert", 2, false)], Scope, Observed, DiscoveryUsage.None);

        Assert.AreEqual(DiscoveryOutcome.Ambiguous, both.Outcome);
        Assert.AreEqual(DiscoveryOutcome.Ambiguous, neither.Outcome);
    }

    [TestMethod]
    public void ANativeAuthorIdSelectsOnlyAnExactIdMatch()
    {
        var anchor = AuthorAnchor.FromSourceId(Author("OL79034A"));

        var found = AuthorResolutionPolicy.Decide(anchor, [Candidate("OL-EDU", "Frank Herbert", 21, false), Candidate("OL79034A", "Frank Herbert", 141, false)], Scope, Observed, DiscoveryUsage.None);
        var missing = AuthorResolutionPolicy.Decide(anchor, [Candidate("OL-EDU", "Frank Herbert", 21, false)], Scope, Observed, DiscoveryUsage.None);

        Assert.AreEqual(DiscoveryOutcome.Ok, found.Outcome);
        Assert.AreEqual(DiscoveryOutcome.Empty, missing.Outcome);
    }

    [TestMethod]
    public void AnAnchorNeedsABookOrAnIdBeforeItCanSelect()
    {
        Assert.ThrowsExactly<ArgumentException>(() => AuthorAnchor.FromNameAndBooks("Frank Herbert", []));
        Assert.ThrowsExactly<ArgumentException>(() => AuthorAnchor.FromSourceId(Series("1")));
        Assert.ThrowsExactly<ArgumentException>(() => AnchorBook.FromIsbn13("12345"));
        Assert.ThrowsExactly<ArgumentException>(() => AnchorBook.FromSource(Author("OL1A")));
        Assert.AreEqual(AuthorAnchorKind.NameOnly, AuthorAnchor.FromNameOnly(" Frank Herbert ").Kind);
        Assert.AreEqual("Frank Herbert", AuthorAnchor.FromNameOnly(" Frank Herbert ").Name);
    }

    // ---- results: states that must not be confusable ----

    private static DiscoveryResult<int> Page(DiscoveryCompletion completion, DiscoveryCursor? next, params int[] items) =>
        DiscoveryResult.Found(items, Scope, completion, Observed, DiscoveryUsage.None, next);

    [TestMethod]
    public void MoreAvailableNeedsACursorAndNothingElseMayCarryOne()
    {
        var more = Page(DiscoveryCompletion.MoreAvailable, new DiscoveryCursor("page-2"), 1, 2);

        Assert.AreEqual(DiscoveryCompletion.MoreAvailable, more.Completion);
        Assert.AreEqual("page-2", more.Next!.Value);
        Assert.ThrowsExactly<ArgumentException>(() => Page(DiscoveryCompletion.MoreAvailable, null, 1));
        Assert.ThrowsExactly<ArgumentException>(() => Page(DiscoveryCompletion.ExhaustedInCheckedScope, new DiscoveryCursor("x"), 1));
        Assert.ThrowsExactly<ArgumentException>(() => Page(DiscoveryCompletion.BudgetTruncated, new DiscoveryCursor("x"), 1));
    }

    [TestMethod]
    public void AFoundResultCannotBeEmptyOrUncheckedAndAnEmptyResultIsOnlyAnExhaustedCheck()
    {
        Assert.ThrowsExactly<ArgumentException>(() => Page(DiscoveryCompletion.ExhaustedInCheckedScope, null));
        Assert.ThrowsExactly<ArgumentException>(() => Page(DiscoveryCompletion.NotChecked, null, 1));
        Assert.ThrowsExactly<ArgumentException>(() => Page(DiscoveryCompletion.ProviderFailedPartial, null, 1));

        var empty = DiscoveryResult.Empty<int>(Scope, Observed, DiscoveryUsage.None);
        Assert.AreEqual(DiscoveryOutcome.Empty, empty.Outcome);
        Assert.AreEqual(DiscoveryCompletion.ExhaustedInCheckedScope, empty.Completion);
        Assert.AreEqual(Scope, empty.Scope, "Empty always states what was checked; it is not global absence.");
    }

    [TestMethod]
    public void UnsupportedUnverifiedUnavailableAndFailedAreNeverEmpty()
    {
        foreach (var outcome in new[]
                 {
                     DiscoveryOutcome.Unsupported, DiscoveryOutcome.Unverified, DiscoveryOutcome.Unavailable,
                     DiscoveryOutcome.Failed, DiscoveryOutcome.TimedOut, DiscoveryOutcome.Throttled
                 })
        {
            var result = DiscoveryResult.NotAnswered<int>(outcome, Scope, Observed, DiscoveryUsage.None, ["provider-disabled"]);

            Assert.AreEqual(outcome, result.Outcome);
            Assert.AreEqual(DiscoveryCompletion.NotChecked, result.Completion);
            Assert.HasCount(0, result.Items);
            Assert.AreNotEqual(DiscoveryOutcome.Empty, result.Outcome);
        }

        Assert.ThrowsExactly<ArgumentException>(() => DiscoveryResult.NotAnswered<int>(DiscoveryOutcome.Empty, Scope, Observed, DiscoveryUsage.None, ["x"]));
        Assert.ThrowsExactly<ArgumentException>(() => DiscoveryResult.NotAnswered<int>(DiscoveryOutcome.Failed, Scope, Observed, DiscoveryUsage.None, []));
    }

    [TestMethod]
    public void PartialFailureKeepsItsValidItemsAndSaysWhyWithoutRawProviderText()
    {
        var partial = DiscoveryResult.PartialSuccess([7, 8], Scope, Observed, DiscoveryUsage.None, [DiscoveryCodes.PartialFieldError]);

        Assert.AreEqual(DiscoveryOutcome.Partial, partial.Outcome);
        Assert.AreEqual(DiscoveryCompletion.ProviderFailedPartial, partial.Completion);
        Assert.AreEqual("7,8", string.Join(",", partial.Items));
        Assert.ThrowsExactly<ArgumentException>(() => DiscoveryResult.PartialSuccess([7], Scope, Observed, DiscoveryUsage.None, ["HTTP 500: Bearer abc.def"]));
        Assert.ThrowsExactly<ArgumentException>(() => DiscoveryResult.PartialSuccess<int>([], Scope, Observed, DiscoveryUsage.None, ["partial-field-error"]));

        var timedOut = DiscoveryResult.Interrupted(DiscoveryOutcome.TimedOut, [1], Scope, Observed, DiscoveryUsage.None, [DiscoveryCodes.DeadlineReached]);
        Assert.AreEqual(DiscoveryOutcome.TimedOut, timedOut.Outcome);
        Assert.HasCount(1, timedOut.Items);
    }

    [TestMethod]
    public void CodesAreStableLowerCaseTokensOnly()
    {
        Assert.IsTrue(DiscoveryCodes.IsCode("query-capacity"));
        Assert.IsFalse(DiscoveryCodes.IsCode("Query capacity"));
        Assert.IsFalse(DiscoveryCodes.IsCode("https://api.example/x?key=1"));
        Assert.IsFalse(DiscoveryCodes.IsCode(new string('a', DiscoveryCodes.MaxLength + 1)));
        Assert.IsFalse(DiscoveryCodes.IsCode(null));
        Assert.ThrowsExactly<ArgumentException>(() => new DiscoveryCursor(new string('c', DiscoveryCursor.MaxLength + 1)));
    }

    [TestMethod]
    public void ContextCapacityCutsReportTheCutWithoutAContinuationOrAFalseExhaustion()
    {
        var more = Page(DiscoveryCompletion.MoreAvailable, new DiscoveryCursor("page-2"), 1, 2, 3, 4, 5);
        var exhausted = Page(DiscoveryCompletion.ExhaustedInCheckedScope, null, 1, 2, 3, 4, 5);

        var retainedMore = more.Retain(2);
        var retainedExhausted = exhausted.Retain(2);

        foreach (var retained in new[] { retainedMore, retainedExhausted })
        {
            Assert.AreEqual("1,2", string.Join(",", retained.Items));
            Assert.AreEqual(DiscoveryCompletion.ContextCapacity, retained.Completion);
            Assert.IsNull(retained.Next, "A continuation after dropped items would silently skip them.");
        }

        Assert.AreSame(more, more.Retain(5), "Nothing is cut when everything fits.");
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => more.Retain(0));
    }

    [TestMethod]
    public void ContextCapacityDoesNotHideAnEarlierProviderFailure()
    {
        var partial = DiscoveryResult.PartialSuccess([1, 2, 3], Scope, Observed, DiscoveryUsage.None, [DiscoveryCodes.PartialFieldError]);

        Assert.AreEqual(DiscoveryCompletion.ProviderFailedPartial, partial.Retain(1).Completion);
    }

    // ---- dates, classification, positions ----

    [TestMethod]
    public void UnstatedUndatedAndDatedAreThreeDifferentFacts()
    {
        var unknown = PublicationEvidence.NotStated;
        var undated = PublicationEvidence.KnownUndated();
        var yearOnly = PublicationEvidence.Dated(new DateOnly(2026, 1, 1), DatePrecision.Year, DateBasis.EditionDerived);

        Assert.AreEqual(PublicationDateState.NotStated, unknown.State);
        Assert.AreEqual(PublicationStatus.Unknown, unknown.SourceStatus);
        Assert.AreEqual(PublicationDateState.KnownUndated, undated.State);
        Assert.AreEqual(PublicationStatus.Forthcoming, undated.SourceStatus);
        Assert.IsNull(undated.Date);
        Assert.AreEqual(DatePrecision.Year, yearOnly.Precision);
        Assert.AreEqual(DateBasis.EditionDerived, yearOnly.Basis, "A reprint's edition date is never relabelled original.");
        Assert.AreEqual(DateBasis.Unknown, undated.Basis);
    }

    [TestMethod]
    public void ABookKeepsItsReportedTitleSourceKindAndIndependentFacts()
    {
        var edition = new SourceRef("openlibrary", "OL1M", ExternalReferenceEntityType.Edition);
        var book = new DiscoveredBook(
            edition,
            new TitleForm("Ignite - Legacy #0.75", "Ignite", "series-suffix"),
            [new CreditRef(0, "Rebecca Yarros", "author", Author("OL7825177A"))],
            PublicationEvidence.NotStated,
            Observed,
            language: "en",
            format: "Paperback",
            version: new BookCandidateVersion("Collection", "Collection or combined volume", "Marked compilation by the source."));

        Assert.AreEqual(ExternalReferenceEntityType.Edition, book.Source.Kind);
        Assert.AreEqual("Ignite - Legacy #0.75", book.Title.Reported);
        Assert.IsTrue(book.Title.WasNormalized);
        Assert.AreEqual("Paperback", book.Format);
        Assert.AreEqual("Collection", book.Version!.Kind);
        Assert.AreEqual(MembershipRole.Unknown, new SeriesMember(book, null, []).Role, "Role is never inferred from version or format.");
        Assert.ThrowsExactly<ArgumentException>(() => new DiscoveredBook(Author("OL1A"), new TitleForm("x"), [], PublicationEvidence.NotStated, Observed));
        Assert.ThrowsExactly<ArgumentException>(() => new DiscoveredBook(edition, new TitleForm(" "), [], PublicationEvidence.NotStated, Observed));
    }

    [TestMethod]
    public void AlternativeAndFractionalPositionsAreKeptAsSeparateClaims()
    {
        var empyrean = Series("1234");
        var claims = new[]
        {
            new PositionClaim("3.5", 3.5m, SeriesOrderingKind.Publication, empyrean),
            new PositionClaim(null, null, null, empyrean),
            new PositionClaim("3", 3m, SeriesOrderingKind.Reading, empyrean)
        };

        var book = new DiscoveredBook(Work("99"), new TitleForm("Threshing Day"), [], PublicationEvidence.NotStated, Observed);
        var member = new SeriesMember(book, empyrean, claims);

        Assert.HasCount(3, member.Positions);
        Assert.IsNull(member.Positions[1].Label, "An author or publisher statement of no number stays distinct from 3.5.");
        Assert.ThrowsExactly<ArgumentException>(() => new PositionClaim("1", 1m, null, Work("1")));
    }

    // ---- canonical series (read-only preference) ----

    [TestMethod]
    public void OnlyAVerifiedSameProviderOneHopCanonicalTargetIsPreferredInAPreview()
    {
        var original = Series("10");
        var target = Series("20");

        var verified = CanonicalSeriesLink.Evaluate(original, target, true, CanonicalTargetLookup.Found, null);
        Assert.AreEqual(CanonicalLinkState.Verified, verified.State);
        Assert.AreEqual(target, verified.PreferredForPreview);
        Assert.AreEqual(original, verified.Original, "Both ids are retained.");

        Assert.AreEqual(CanonicalLinkState.NotStated, CanonicalSeriesLink.Evaluate(original, null, true, CanonicalTargetLookup.NotAttempted, null).State);
        Assert.AreEqual("canonical-self", CanonicalSeriesLink.Evaluate(original, original, true, CanonicalTargetLookup.Found, null).ReasonCode);
        Assert.AreEqual("canonical-scope-mismatch", CanonicalSeriesLink.Evaluate(original, Series("20", "openlibrary"), true, CanonicalTargetLookup.Found, null).ReasonCode);
        Assert.AreEqual("canonical-scope-mismatch", CanonicalSeriesLink.Evaluate(original, Work("20"), true, CanonicalTargetLookup.Found, null).ReasonCode);
        Assert.AreEqual("canonical-target-missing", CanonicalSeriesLink.Evaluate(original, target, true, CanonicalTargetLookup.Missing, null).ReasonCode);
        Assert.AreEqual("canonical-cycle", CanonicalSeriesLink.Evaluate(original, target, true, CanonicalTargetLookup.Found, original).ReasonCode);
        Assert.AreEqual("canonical-chain", CanonicalSeriesLink.Evaluate(original, target, true, CanonicalTargetLookup.Found, Series("30")).ReasonCode);
    }

    [TestMethod]
    public void UnverifiedCanonicalMeaningOrAnUncheckedTargetNeverRedirectsAPreview()
    {
        var original = Series("10");
        var target = Series("20");

        var unverifiedMeaning = CanonicalSeriesLink.Evaluate(original, target, false, CanonicalTargetLookup.Found, null);
        var notChecked = CanonicalSeriesLink.Evaluate(original, target, true, CanonicalTargetLookup.NotAttempted, null);
        var invalid = CanonicalSeriesLink.Evaluate(original, original, true, CanonicalTargetLookup.Found, null);

        foreach (var link in new[] { unverifiedMeaning, notChecked, invalid })
        {
            Assert.AreEqual(original, link.PreferredForPreview);
            Assert.AreNotEqual(CanonicalLinkState.Verified, link.State);
        }

        Assert.AreEqual(CanonicalLinkState.StatedUnverified, unverifiedMeaning.State);
        Assert.AreEqual(CanonicalLinkState.Invalid, invalid.State);
    }

    // ---- capabilities ----

    [TestMethod]
    public void AnOperationNobodyDeclaredIsUnverifiedNotSupportedOrUnsupported()
    {
        var capabilities = new DiscoveryCapabilities(
        [
            new(DiscoveryOperation.GetAuthorWorks, DiscoveryCapability.Supported),
            new(DiscoveryOperation.GetSeriesMembers, DiscoveryCapability.Unsupported("no-series-resource"))
        ]);

        Assert.AreEqual(CapabilityState.Supported, capabilities.Get(DiscoveryOperation.GetAuthorWorks).State);
        Assert.AreEqual(CapabilityState.Unsupported, capabilities.Get(DiscoveryOperation.GetSeriesMembers).State);
        Assert.AreEqual(CapabilityState.Unverified, capabilities.Get(DiscoveryOperation.FindSeriesForBook).State);
        Assert.ThrowsExactly<ArgumentException>(() => DiscoveryCapability.Unsupported("Series endpoints returned 401"));
    }

    // ---- R3: the typed no-usable-items state ----

    [TestMethod]
    public void NoUsableItemsKeepsItsStateAndCannotBeBuiltAsAnEmptyOrCursorlessMoreAvailable()
    {
        var more = DiscoveryResult.NoUsableItems<SeriesRef>(
            BookScope, DiscoveryCompletion.MoreAvailable, new DiscoveryCursor("o:99"), Observed, DiscoveryUsage.None, [DiscoveryCodes.ItemMissingId]);
        var last = DiscoveryResult.NoUsableItems<SeriesRef>(
            BookScope, DiscoveryCompletion.ExhaustedInCheckedScope, null, Observed, DiscoveryUsage.None, [DiscoveryCodes.ItemMissingId]);

        Assert.AreEqual(DiscoveryOutcome.NoUsableItems, more.Outcome);
        Assert.AreEqual("o:99", more.Next!.Value);
        Assert.AreEqual(0, more.Items.Count);
        Assert.AreEqual(DiscoveryOutcome.NoUsableItems, last.Outcome);
        Assert.AreNotEqual(DiscoveryOutcome.Empty, last.Outcome);
        Assert.ThrowsExactly<ArgumentException>(() =>
            DiscoveryResult.NoUsableItems<SeriesRef>(BookScope, DiscoveryCompletion.MoreAvailable, null, Observed, DiscoveryUsage.None, [DiscoveryCodes.ItemMissingId]));
        Assert.ThrowsExactly<ArgumentException>(() =>
            DiscoveryResult.NoUsableItems<SeriesRef>(BookScope, DiscoveryCompletion.ExhaustedInCheckedScope, null, Observed, DiscoveryUsage.None, []));
        Assert.ThrowsExactly<ArgumentException>(() =>
            DiscoveryResult.NotAnswered<SeriesRef>(DiscoveryOutcome.NoUsableItems, BookScope, Observed, DiscoveryUsage.None, [DiscoveryCodes.ItemMissingId]));
    }

    // ---- R6: validated results do not alias caller-owned collections ----

    private static readonly DiscoveryScope BookScope = new(DiscoveryOperation.FindSeriesForBook, Work("5"));

    [TestMethod]
    public void AFoundResultKeepsItsItemsWhenTheCallerClearsItsList()
    {
        var items = new List<SeriesRef> { new("Legacy", Series("300")) };

        var result = DiscoveryResult.Found(items, BookScope, DiscoveryCompletion.ExhaustedInCheckedScope, Observed, DiscoveryUsage.None);
        items.Clear();
        items.Add(new SeriesRef("Injected", Series("999")));

        Assert.AreEqual(DiscoveryOutcome.Ok, result.Outcome);
        Assert.HasCount(1, result.Items, "A result that was Ok with items must stay so.");
        Assert.AreEqual("300", result.Items[0].Id!.ExternalId);
    }

    [TestMethod]
    public void NotesCannotGainUnvalidatedTextAfterConstructionNorBeMutatedThroughACast()
    {
        var notes = new List<string> { DiscoveryCodes.DuplicateDropped };
        var result = DiscoveryResult.Found(
            [new SeriesRef("Legacy", Series("300"))], BookScope, DiscoveryCompletion.ExhaustedInCheckedScope, Observed,
            DiscoveryUsage.None, notes: notes);

        notes.Add("Raw PROVIDER text: secret=abc");

        CollectionAssert.AreEqual(new[] { DiscoveryCodes.DuplicateDropped }, result.Notes.ToArray());
        Assert.IsTrue(result.Notes.All(DiscoveryCodes.IsCode));
        if (result.Notes is IList<string> writable)
        {
            Assert.ThrowsExactly<NotSupportedException>(() => writable.Add("Raw text"));
        }

        if (result.Items is IList<SeriesRef> writableItems)
        {
            Assert.ThrowsExactly<NotSupportedException>(() => writableItems.Clear());
        }
    }

    [TestMethod]
    public void PartialAndRetainedResultsAlsoSnapshotTheirLists()
    {
        var items = new List<SeriesRef> { new("A", Series("1")), new("B", Series("2")) };
        var notes = new List<string> { DiscoveryCodes.PartialFieldError };
        var partial = DiscoveryResult.PartialSuccess(items, BookScope, Observed, DiscoveryUsage.None, notes);

        items.Clear();
        notes.Add("not a code");

        Assert.HasCount(2, partial.Items);
        Assert.HasCount(1, partial.Notes);
        Assert.HasCount(1, partial.Retain(1).Items);
    }

    [TestMethod]
    public void NestedCreditPositionAndAnchorListsAreSnapshotsToo()
    {
        var credits = new List<CreditRef> { new(0, "Alex Twin", null, new SourceRef("hardcover", "7", ExternalReferenceEntityType.Author)) };
        var book = new DiscoveredBook(Work("5"), new TitleForm("Dune"), credits, PublicationEvidence.NotStated, Observed);
        var positions = new List<PositionClaim> { new("1", 1m) };
        var member = new SeriesMember(book, Series("100"), positions);
        var anchorBooks = new List<AnchorBook> { AnchorBook.FromIsbn13("9780441013593") };
        var anchor = AuthorAnchor.FromNameAndBooks("Frank Herbert", anchorBooks);

        credits.Add(new CreditRef(1, "Alex Twin", null, new SourceRef("hardcover", "8", ExternalReferenceEntityType.Author)));
        positions.Clear();
        anchorBooks.Clear();

        Assert.HasCount(1, book.Credits, "A later same-name credit must not change what the record was validated as.");
        Assert.HasCount(1, member.Positions);
        Assert.HasCount(1, anchor.Books, "An anchor must keep the book that vouches for it.");
        Assert.IsNotInstanceOfType<AnchorBook[]>(anchor.Books, "The anchor's list must not be a writable array.");
    }

    // ---- TitleForm.Alternates is a private snapshot too (G3) ----

    private static AlternateTitle Alt(string text) => new(text, "alternative_title", null, TitleScope.Work);

    [TestMethod]
    public void TitleFormAlternatesAreAPrivateReadOnlySnapshotWhetherSetByListArrayOrWith()
    {
        var list = new List<AlternateTitle> { Alt("One") };
        var fromList = new TitleForm("Dune") { Alternates = list };
        list.Add(Alt("Two"));
        list[0] = Alt("Changed");
        Assert.HasCount(1, fromList.Alternates, "A later add to the caller's list must not reach the form.");
        Assert.AreEqual("One", fromList.Alternates[0].Text);

        var array = new[] { Alt("One") };
        var fromArray = new TitleForm("Dune") { Alternates = array };
        array[0] = Alt("Changed");
        Assert.AreEqual("One", fromArray.Alternates[0].Text, "Writing through the caller's array must not reach the form.");

        var viaWith = new TitleForm("Dune") with { Alternates = list };
        list.Clear();
        Assert.HasCount(2, viaWith.Alternates, "A with-assignment snapshots too.");
        Assert.AreEqual("Changed", viaWith.Alternates[0].Text);

        Assert.IsNotInstanceOfType<List<AlternateTitle>>(fromList.Alternates);
        Assert.IsNotInstanceOfType<AlternateTitle[]>(fromArray.Alternates);
        Assert.Throws<NotSupportedException>(() => ((IList<AlternateTitle>)fromList.Alternates).Add(Alt("Cast")));
        Assert.Throws<NotSupportedException>(() => ((IList<AlternateTitle>)fromList.Alternates)[0] = Alt("Cast"));
        Assert.HasCount(1, fromList.Alternates);
    }

    [TestMethod]
    public void TitleFormAlternatesDefaultToEmptyRejectNullAndKeepContentEquality()
    {
        Assert.IsEmpty(new TitleForm("Dune").Alternates);
        Assert.IsEmpty(new TitleForm("Dune") { Alternates = [] }.Alternates);
        Assert.ThrowsExactly<ArgumentNullException>(() => _ = new TitleForm("Dune") { Alternates = null! });

        var a = new TitleForm("Dune") { Alternates = new List<AlternateTitle> { Alt("One"), Alt("Two") } };
        var b = new TitleForm("Dune") { Alternates = new[] { Alt("One"), Alt("Two") } };
        Assert.AreEqual(a, b, "Equality is by content, whatever held the alternates.");
        Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
        Assert.AreNotEqual(a, new TitleForm("Dune"));
        Assert.AreNotEqual(a, new TitleForm("Dune") { Alternates = [Alt("One")] });
        Assert.AreNotEqual(a, new TitleForm("Dune") { Alternates = [Alt("Two"), Alt("One")] }, "Order is part of what the source stated.");
        Assert.AreEqual(new TitleForm("Dune"), new TitleForm("Dune") { Alternates = [] });
        Assert.AreEqual(new TitleForm("Dune").GetHashCode(), new TitleForm("Dune") { Alternates = [] }.GetHashCode());
    }

    [TestMethod]
    public void ADiscoveredBookKeepsTheSnapshotOfItsTitleAlternates()
    {
        var list = new List<AlternateTitle> { Alt("One") };
        var book = new DiscoveredBook(Work("5"), new TitleForm("Dune") { Alternates = list }, [], PublicationEvidence.NotStated, Observed);
        list.Add(Alt("Two"));
        Assert.HasCount(1, book.Title.Alternates);
    }
}
