using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Catalog.Answers;
using FamilyLibrarian.Application.Catalog.Discovery;
using FamilyLibrarian.Domain.Catalog;
using Claim = FamilyLibrarian.Application.Catalog.Discovery.PositionClaim;

namespace FamilyLibrarian.Infrastructure.Tests.Catalog;

/// <summary>
/// The bounded context primitives: the compact versioned reference, caller binding (user, transport, scope), absolute expiry, the
/// exact retained-text byte bound, and the entry mapping that restates evidence without inferring any. Fictional data throughout.
/// </summary>
[TestClass]
public sealed class CatalogAnswerContextTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    private static readonly CatalogAnswerCaller Reader = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), CatalogAnswerTransport.Web, "session-a");

    private static CatalogSubjectRef Series(string id = "s1", string label = "The Saga", SeriesOrderingKind order = SeriesOrderingKind.Unspecified) =>
        CatalogSubjectRef.FromSource(CatalogSubjectKind.Series, label, new SourceRef("fiction", id, ExternalReferenceEntityType.Series), order);

    private static CatalogRetainedWindow Window(int entries, bool exhausted = true, string? token = null, string title = "Book") => new(
        CatalogAnswerOperation.LatestPublished, Series(), SeriesOrderingKind.Unspecified,
        Enumerable.Range(0, entries).Select(index => new CatalogAnswerEntry
        {
            Source = new SourceRef("fiction", "w" + index, ExternalReferenceEntityType.Work),
            Title = $"{title} {index}",
            ProviderId = "fiction",
            Positions = [new Claim(index.ToString(System.Globalization.CultureInfo.InvariantCulture), index, null)]
        }).ToArray(),
        [], [], exhausted, MoreAvailable: token is not null, Truncated: false, SourceGaps: false, [], "series-members-page", "fiction", token, Start);

    // ================================================================================== the compact reference

    [TestMethod]
    public void AReferenceRoundTripsWithItsSavedAndNativeSubjectsAndOrder()
    {
        var root = Guid.NewGuid();
        var saved = CatalogSubjectRef.Saved(CatalogSubjectKind.Book, "An Anchor", Guid.NewGuid());
        var reference = new CatalogAnswerContextRef(root, CatalogAnswerOperation.AfterAnchor, Series(order: SeriesOrderingKind.Reading), saved, SeriesOrderingKind.Reading);

        var json = reference.ToJson();
        Assert.IsNotNull(json);
        Assert.IsTrue(CatalogAnswerContextRef.TryParse(json, out var parsed));

        Assert.AreEqual(root, parsed!.RootId);
        Assert.AreEqual(CatalogAnswerOperation.AfterAnchor, parsed.Operation);
        Assert.AreEqual(SeriesOrderingKind.Reading, parsed.Order);
        Assert.AreEqual(reference.Subject!.Native, parsed.Subject!.Native);
        Assert.AreEqual(saved.SavedId, parsed.Anchor!.SavedId);
        Assert.IsTrue(json.Length <= CatalogAnswerContextRef.MaxJsonLength);
    }

    [TestMethod]
    public void AReferenceThatDoesNotFitIsRefusedNotCut()
    {
        var longId = new string('x', SourceRef.MaxExternalIdLength);
        var longLabel = new string('L', CatalogSubjectRef.MaxLabelLength);
        var longProvider = new string('p', SourceRef.MaxProviderIdLength);
        var big = CatalogSubjectRef.FromSource(CatalogSubjectKind.Series, longLabel, new SourceRef(longProvider, longId, ExternalReferenceEntityType.Series));
        var anchor = CatalogSubjectRef.FromSource(CatalogSubjectKind.Book, longLabel, new SourceRef(longProvider, longId, ExternalReferenceEntityType.Work));
        var reference = new CatalogAnswerContextRef(Guid.NewGuid(), CatalogAnswerOperation.AfterAnchor, big, anchor, SeriesOrderingKind.Unspecified);

        Assert.IsNull(reference.ToJson(), "A cut id would name a different thing, so an oversized reference is not issued at all.");
    }

    [TestMethod]
    public void UnknownVersionMalformedAndTamperedReferencesAreNoContext()
    {
        var good = new CatalogAnswerContextRef(Guid.NewGuid(), CatalogAnswerOperation.LatestPublished, Series(), null, SeriesOrderingKind.Unspecified).ToJson()!;

        Assert.IsFalse(CatalogAnswerContextRef.TryParse(good.Replace("\"v\":1", "\"v\":2", StringComparison.Ordinal), out _), "An unknown version recovers safely.");
        Assert.IsFalse(CatalogAnswerContextRef.TryParse("{\"v\":1", out _));
        Assert.IsFalse(CatalogAnswerContextRef.TryParse("not json", out _));
        Assert.IsFalse(CatalogAnswerContextRef.TryParse(null, out _));
        Assert.IsFalse(CatalogAnswerContextRef.TryParse("   ", out _));
        Assert.IsFalse(CatalogAnswerContextRef.TryParse(good.Replace("\"o\":1", "\"o\":99", StringComparison.Ordinal), out _));
        Assert.IsFalse(CatalogAnswerContextRef.TryParse(good.Replace("\"r\":\"", "\"r\":\"zz", StringComparison.Ordinal), out _));
        Assert.IsFalse(CatalogAnswerContextRef.TryParse(new string(' ', CatalogAnswerContextRef.MaxJsonLength + 1), out _));
    }

    [TestMethod]
    public void ASubjectNamingBothASavedIdAndASourceIsRejected()
    {
        var root = Guid.NewGuid().ToString("N");
        var both = $"{{\"v\":1,\"r\":\"{root}\",\"o\":1,\"d\":0,\"s\":{{\"k\":1,\"l\":\"X\",\"g\":\"{Guid.NewGuid():N}\",\"p\":\"fiction\",\"i\":\"s1\",\"t\":4}}}}";

        Assert.IsFalse(CatalogAnswerContextRef.TryParse(both, out _));
    }

    [TestMethod]
    public void ATitleSubjectCarriesNoIdentityAndSurvivesTheRoundTrip()
    {
        var reference = new CatalogAnswerContextRef(Guid.NewGuid(), CatalogAnswerOperation.PublicationForTitle, CatalogSubjectRef.Title("A Named Book"), null, SeriesOrderingKind.Unspecified);

        Assert.IsTrue(CatalogAnswerContextRef.TryParse(reference.ToJson(), out var parsed));
        Assert.AreEqual(CatalogSubjectKind.Title, parsed!.Subject!.Kind);
        Assert.IsNull(parsed.Subject.Native);
        Assert.IsNull(parsed.Subject.SavedId);
    }

    [TestMethod]
    public void ASubjectNeedsAMatchingKindAndAWholeLabel()
    {
        Assert.ThrowsExactly<ArgumentException>(() => CatalogSubjectRef.FromSource(CatalogSubjectKind.Author, "X", new SourceRef("fiction", "s1", ExternalReferenceEntityType.Series)));
        Assert.ThrowsExactly<ArgumentException>(() => CatalogSubjectRef.Saved(CatalogSubjectKind.Series, "X", Guid.Empty));
        Assert.ThrowsExactly<ArgumentException>(() => CatalogSubjectRef.Saved(CatalogSubjectKind.Series, new string('a', CatalogSubjectRef.MaxLabelLength + 1), Guid.NewGuid()));
    }

    [TestMethod]
    public void ACallerNeedsAnOwnerADefinedTransportAndAWholeScopeKey()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new CatalogAnswerCaller(Guid.Empty, CatalogAnswerTransport.Web, "s"));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new CatalogAnswerCaller(Guid.NewGuid(), (CatalogAnswerTransport)9, "s"));
        Assert.ThrowsExactly<ArgumentException>(() => new CatalogAnswerCaller(Guid.NewGuid(), CatalogAnswerTransport.Matrix, " "));
        Assert.ThrowsExactly<ArgumentException>(() => new CatalogAnswerCaller(Guid.NewGuid(), CatalogAnswerTransport.Matrix, new string('r', CatalogAnswerCaller.MaxScopeKeyLength + 1)));
    }

    // =================================================================================== binding and expiry

    [TestMethod]
    public void ARootIsReachableOnlyByTheExactUserTransportAndScope()
    {
        var store = new CatalogAnswerContextStore();
        var root = store.CreateRoot(Reader, Start)!;
        store.Retain(Reader, root.RootId, Window(2), Start);

        var otherUser = new CatalogAnswerCaller(Guid.NewGuid(), Reader.Transport, Reader.ScopeKey);
        var otherTransport = new CatalogAnswerCaller(Reader.OwnerId, CatalogAnswerTransport.Matrix, Reader.ScopeKey);
        var otherSession = new CatalogAnswerCaller(Reader.OwnerId, Reader.Transport, "session-b");

        Assert.IsNotNull(store.TryGetRoot(Reader, root.RootId, Start));
        foreach (var stranger in new[] { otherUser, otherTransport, otherSession })
        {
            Assert.IsNull(store.TryGetRoot(stranger, root.RootId, Start));
            Assert.IsNull(store.TryGetWindow(stranger, root.RootId, Start));
            Assert.IsNull(store.Retain(stranger, root.RootId, Window(1), Start), "Another room's number or reference cannot write here.");
            Assert.IsNull(store.TryGetChoice(stranger, root.RootId, 0, Start));
        }

        // A stranger's attempts changed nothing.
        Assert.AreEqual(2, store.TryGetWindow(Reader, root.RootId, Start)!.Entries.Count);
    }

    [TestMethod]
    public void ARootExpiresAtAnAbsoluteTenMinutesWhateverTheActivity()
    {
        var store = new CatalogAnswerContextStore();
        var root = store.CreateRoot(Reader, Start)!;
        Assert.AreEqual(Start + TimeSpan.FromMinutes(10), root.ExpiresAtUtc);

        // Reads and writes up to the last instant do not extend it.
        store.Retain(Reader, root.RootId, Window(1), Start + TimeSpan.FromMinutes(9));
        Assert.IsNotNull(store.TryGetWindow(Reader, root.RootId, Start + TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(59)));
        Assert.IsNull(store.TryGetWindow(Reader, root.RootId, Start + TimeSpan.FromMinutes(10)));
        Assert.IsNull(store.TryGetRoot(Reader, root.RootId, Start + TimeSpan.FromMinutes(10)));
        Assert.AreEqual(0, store.RootCount, "An expired root is dropped on read.");
    }

    [TestMethod]
    public void TheOldestRootIsEvictedWithinTheOwnerThenGlobally()
    {
        var store = new CatalogAnswerContextStore(new CatalogAnswerContextLimits { MaxRootsPerOwner = 2, MaxRootsTotal = 3 });
        var first = store.CreateRoot(Reader, Start)!;
        var second = store.CreateRoot(Reader, Start)!;
        var third = store.CreateRoot(Reader, Start)!;

        Assert.IsNull(store.TryGetRoot(Reader, first.RootId, Start), "The owner's third root evicts its oldest.");
        Assert.IsNotNull(store.TryGetRoot(Reader, second.RootId, Start));
        Assert.IsNotNull(store.TryGetRoot(Reader, third.RootId, Start));

        var other = new CatalogAnswerCaller(Guid.NewGuid(), CatalogAnswerTransport.Web, "s");
        var o1 = store.CreateRoot(other, Start)!;
        var o2 = store.CreateRoot(other, Start)!;
        Assert.AreEqual(3, store.RootCount, "The global bound holds.");
        Assert.IsNull(store.TryGetRoot(Reader, second.RootId, Start), "The global oldest goes.");
        Assert.IsNotNull(store.TryGetRoot(other, o1.RootId, Start));
        Assert.IsNotNull(store.TryGetRoot(other, o2.RootId, Start));
    }

    [TestMethod]
    public void AZeroedBoundHoldsNoRootAtAll()
    {
        Assert.IsNull(new CatalogAnswerContextStore(new CatalogAnswerContextLimits { MaxRootsPerOwner = 0 }).CreateRoot(Reader, Start));
        Assert.IsNull(new CatalogAnswerContextStore(new CatalogAnswerContextLimits { MaxRootsTotal = 0 }).CreateRoot(Reader, Start));
    }

    // ==================================================================================== exact byte bound

    [TestMethod]
    public void TheByteBoundIsExactAndACutWindowSaysItWasCutAndOffersNoContinuation()
    {
        var full = Window(10, token: "next-page-token");
        // The shape a cut window is stored in: truncated, not exhausted, and with no continuation.
        var fits = CatalogAnswerContextStore.Measure(Window(6, exhausted: false) with { Truncated = true });
        var store = new CatalogAnswerContextStore(new CatalogAnswerContextLimits { MaxBytesPerRoot = fits });
        var root = store.CreateRoot(Reader, Start)!;

        var kept = store.Retain(Reader, root.RootId, full, Start)!;

        Assert.AreEqual(6, kept.Entries.Count, "Exactly the entries that fit are kept, from the front.");
        Assert.IsTrue(CatalogAnswerContextStore.Measure(kept) <= fits);
        Assert.IsTrue(kept.Truncated);
        Assert.IsFalse(kept.Exhausted, "A cut window never claims it was read to its end.");
        Assert.IsFalse(kept.MoreAvailable);
        Assert.IsNull(kept.ContinuationToken, "Following it would skip what was cut.");
        Assert.AreEqual("Book 0", kept.Entries[0].Title);

        // One byte less and one fewer entry fits.
        var tighter = new CatalogAnswerContextStore(new CatalogAnswerContextLimits { MaxBytesPerRoot = fits - 1 });
        var tightRoot = tighter.CreateRoot(Reader, Start)!;
        Assert.AreEqual(5, tighter.Retain(Reader, tightRoot.RootId, full, Start)!.Entries.Count);
    }

    [TestMethod]
    public void AWindowThatFitsIsKeptWholeWithItsTokenAndTheEntryBoundAlsoCuts()
    {
        var store = new CatalogAnswerContextStore();
        var root = store.CreateRoot(Reader, Start)!;
        var whole = store.Retain(Reader, root.RootId, Window(5, token: "t"), Start)!;
        Assert.AreEqual(5, whole.Entries.Count);
        Assert.IsFalse(whole.Truncated);
        Assert.AreEqual("t", whole.ContinuationToken);

        var capped = new CatalogAnswerContextStore(new CatalogAnswerContextLimits { MaxEntriesPerRoot = 3 });
        var cappedRoot = capped.CreateRoot(Reader, Start)!;
        var cut = capped.Retain(Reader, cappedRoot.RootId, Window(5, token: "t"), Start)!;
        Assert.AreEqual(3, cut.Entries.Count);
        Assert.IsTrue(cut.Truncated);
        Assert.IsNull(cut.ContinuationToken);
    }

    [TestMethod]
    public void NothingIsRetainedWhenEvenAnEmptyWindowCannotFit()
    {
        var store = new CatalogAnswerContextStore(new CatalogAnswerContextLimits { MaxBytesPerRoot = 10 });
        var root = store.CreateRoot(Reader, Start)!;

        Assert.IsNull(store.Retain(Reader, root.RootId, Window(3), Start));
        Assert.IsNull(store.TryGetWindow(Reader, root.RootId, Start));
    }

    [TestMethod]
    public void MultiByteTextIsMeasuredInUtf8BytesNotCharacters()
    {
        var ascii = CatalogAnswerContextStore.Measure(Window(1, title: "aaaa"));
        var accented = CatalogAnswerContextStore.Measure(Window(1, title: "éééé"));

        Assert.AreEqual(ascii + 4, accented, "Four two-byte characters cost four more bytes than four one-byte ones.");
    }

    [TestMethod]
    public void OnlyTheListedChoicesCanBeSelectedByIndex()
    {
        var store = new CatalogAnswerContextStore();
        var root = store.CreateRoot(Reader, Start)!;
        var choices = new[]
        {
            new CatalogAnswerChoice(Series("s1", "One"), null, null, "fiction", "series-claim-on-seed-record"),
            new CatalogAnswerChoice(Series("s2", "Two"), null, null, "fiction", "series-claim-on-seed-record")
        };
        store.Retain(Reader, root.RootId, Window(0) with { Subject = null, Choices = choices }, Start);

        Assert.AreEqual("Two", store.TryGetChoice(Reader, root.RootId, 1, Start)!.Reference.Label);
        Assert.IsNull(store.TryGetChoice(Reader, root.RootId, 2, Start), "A forged or stale number selects nothing.");
        Assert.IsNull(store.TryGetChoice(Reader, root.RootId, -1, Start));
        Assert.IsNull(store.TryGetChoice(Reader, Guid.NewGuid(), 0, Start));
    }

    // ======================================================================================= entry mapping

    [TestMethod]
    public void ACandidateWithNoSourceKindIsNotAnEntryAndNothingIsGuessed()
    {
        var candidate = new BookCandidate("fiction", "Fiction", "w1", "A Title", ["A. Writer"], null, null, new DateOnly(2001, 1, 1), [], []);

        Assert.IsNull(CatalogAnswerEntries.FromCandidate(candidate, Start));
        Assert.IsNotNull(CatalogAnswerEntries.FromCandidate(candidate with { SourceKind = ExternalReferenceEntityType.Edition }, Start));
    }

    [TestMethod]
    public void ACandidateKeepsItsDateAsStatedAndNeverReadsAMissingBasisAsOriginal()
    {
        BookCandidate Make(DateBasis? basis, DatePrecision? precision) =>
            new("fiction", "Fiction", "w1", "A Title", ["A. Writer"], null, null, new DateOnly(2001, 1, 1), [], [])
            {
                SourceKind = ExternalReferenceEntityType.Work,
                PublicationDateBasis = basis,
                PublicationDatePrecision = precision,
                AuthorCredits = [new BookAuthorCredit("A. Writer", null, "auth-1")]
            };

        var unknown = CatalogAnswerEntries.FromCandidate(Make(null, null), Start)!;
        var edition = CatalogAnswerEntries.FromCandidate(Make(DateBasis.EditionDerived, DatePrecision.Year), Start)!;

        Assert.AreEqual(DateBasis.Unknown, unknown.Publication.Basis);
        Assert.AreEqual(DatePrecision.Day, unknown.Publication.Precision, "A null precision is the historical meaning: a full day.");
        Assert.AreEqual(DateBasis.EditionDerived, edition.Publication.Basis);
        Assert.AreEqual(DatePrecision.Year, edition.Publication.Precision);
        Assert.AreEqual(PublicationStatus.Unknown, unknown.Publication.SourceStatus, "A search hit carries no source status.");
        Assert.AreEqual(new SourceRef("fiction", "auth-1", ExternalReferenceEntityType.Author), unknown.Credits.Single().AuthorSource);
    }

    [TestMethod]
    public void ACandidatesSeriesPositionsCarryTheirOwnOrderingOnlyWhenStated()
    {
        var candidate = new BookCandidate("fiction", "Fiction", "w1", "A Title", [], null, null, null, [],
            [
                new BookSeriesCandidate("The Saga (Chronological)", "2", true, 2m) { SourceSeriesId = "s9" },
                new BookSeriesCandidate("The Saga", "1", true, 1m)
            ])
        { SourceKind = ExternalReferenceEntityType.Work };

        var entry = CatalogAnswerEntries.FromCandidate(candidate, Start)!;

        Assert.AreEqual(SeriesOrderingKind.Chronological, entry.Positions[0].Ordering);
        Assert.AreEqual(new SourceRef("fiction", "s9", ExternalReferenceEntityType.Series), entry.Positions[0].Series);
        Assert.IsNull(entry.Positions[1].Ordering, "An unqualified label is never read as publication order.");
        Assert.AreEqual(MembershipRole.Unknown, entry.Role, "A primary flag does not make a book Main.");
    }

    [TestMethod]
    public void ASavedWorkWithNoRecordedPrecisionIsReadAsTheWholeYearAndSaysSo()
    {
        var work = new SavedWorkEvidence(Guid.NewGuid(), "Legacy", new DateOnly(2020, 1, 1), null, null, PublicationStatus.Published, null, [], false);

        var entry = CatalogAnswerEntries.FromSaved(work, [], null);

        Assert.AreEqual(DatePrecision.Year, entry.Publication.Precision);
        Assert.AreEqual(DateBasis.Unknown, entry.Publication.Basis, "No recorded basis is never the original date.");
        Assert.IsTrue(entry.LegacyPrecision);
        Assert.AreEqual(PublicationStatus.Unknown, entry.Publication.SourceStatus, "The saved status is computed from the date, not a source's statement.");
    }

    [TestMethod]
    public void ASavedWorkWithRecordedPrecisionAndBasisKeepsThemAndAForthcomingUndatedOneIsKnownUndated()
    {
        var dated = CatalogAnswerEntries.FromSaved(
            new SavedWorkEvidence(Guid.NewGuid(), "Exact", new DateOnly(2020, 6, 1), DatePrecision.Month, DateBasis.Original, PublicationStatus.Published, "Collection", [], false), [], null);
        var announced = CatalogAnswerEntries.FromSaved(
            new SavedWorkEvidence(Guid.NewGuid(), "Soon", null, null, null, PublicationStatus.Forthcoming, null, [], false), [], null);
        var silent = CatalogAnswerEntries.FromSaved(
            new SavedWorkEvidence(Guid.NewGuid(), "Quiet", null, null, null, PublicationStatus.Unknown, null, [], false), [], null);

        Assert.AreEqual(DatePrecision.Month, dated.Publication.Precision);
        Assert.AreEqual(DateBasis.Original, dated.Publication.Basis);
        Assert.IsFalse(dated.LegacyPrecision);
        Assert.AreEqual(CatalogWorkClass.Collection, dated.Class);
        Assert.AreEqual(PublicationDateState.KnownUndated, announced.Publication.State);
        Assert.AreEqual(PublicationDateState.NotStated, silent.Publication.State);
    }

    [TestMethod]
    public void ClassificationMapsOnlyStatedKindsAndUnspecifiedStaysUnknown()
    {
        Assert.AreEqual(CatalogWorkClass.Novel, CatalogAnswerEntries.ClassOf("Novel"));
        Assert.AreEqual(CatalogWorkClass.Collection, CatalogAnswerEntries.ClassOf("Collection"));
        Assert.AreEqual(CatalogWorkClass.Abridgement, CatalogAnswerEntries.ClassOf("Abridged"));
        Assert.AreEqual(CatalogWorkClass.Adaptation, CatalogAnswerEntries.ClassOf("Adaptation"));
        Assert.AreEqual(CatalogWorkClass.GraphicAdaptation, CatalogAnswerEntries.ClassOf("GraphicAdaptation"));
        Assert.AreEqual(CatalogWorkClass.StudyGuide, CatalogAnswerEntries.ClassOf("StudyGuide"));
        Assert.AreEqual(CatalogWorkClass.Unknown, CatalogAnswerEntries.ClassOf("Unspecified"));
        Assert.AreEqual(CatalogWorkClass.Unknown, CatalogAnswerEntries.ClassOf("Text"));
        Assert.AreEqual(CatalogWorkClass.Unknown, CatalogAnswerEntries.ClassOf(null));
    }

    [TestMethod]
    public void TwoEntriesWithTheSameTitleHaveDifferentIdentities()
    {
        var a = new CatalogAnswerEntry { Title = "Same", Source = new SourceRef("fiction", "1", ExternalReferenceEntityType.Work) };
        var b = new CatalogAnswerEntry { Title = "Same", Source = new SourceRef("fiction", "2", ExternalReferenceEntityType.Work) };
        var edition = new CatalogAnswerEntry { Title = "Same", Source = new SourceRef("fiction", "1", ExternalReferenceEntityType.Edition) };

        Assert.AreNotEqual(a.IdentityKey, b.IdentityKey);
        Assert.AreNotEqual(a.IdentityKey, edition.IdentityKey, "A Work and an Edition with one id are two records.");
        Assert.AreEqual(CatalogAnswerEntry.KeyOf(a.Source!), a.IdentityKey);
    }
}
