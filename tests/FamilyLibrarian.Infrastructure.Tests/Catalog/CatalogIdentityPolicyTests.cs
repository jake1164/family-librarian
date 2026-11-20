using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Catalog.Identity;
using FamilyLibrarian.Domain.Catalog;

namespace FamilyLibrarian.Infrastructure.Tests.Catalog;

/// <summary>
/// The pure durable-identity policy: which provider records may join a saved Work, which of their editions may be written or count
/// as proof, and how ambiguous names bind. No database, no provider; every positive case has a collision counterpart.
/// </summary>
[TestClass]
public sealed class CatalogIdentityPolicyTests
{
    private static readonly string[] OneAndTwo = ["1", "2"];

    private const string Hardback = "9780000000002";
    private const string Paperback = "9780000000019";
    private const string Ebook = "9780000000026";
    private const string German = "9780000000033";

    private static BookCandidate Record(
        string title = "The Hobbit",
        string[]? authors = null,
        BookEditionCandidate[]? editions = null,
        string? language = null,
        string? description = null,
        DateOnly? date = null,
        DateBasis? basis = null,
        string? workTitle = null) =>
        new("example-source", "Example", "rec-1", title, authors ?? ["J. R. R. Tolkien"], description, null, date,
            editions ?? [], [], Language: language, WorkTitle: workTitle)
        {
            PublicationDateBasis = basis
        };

    private static BookEditionCandidate Edition(string title, string? isbn, string format = "Paperback", string? language = null) =>
        new(title, isbn, format, null, language);

    private static WorkIdentity Work(
        string title = "The Hobbit",
        string? versionClass = CatalogVersionClass.Text,
        string[]? languages = null,
        string[]? authors = null,
        string[]? editionTitles = null,
        DateOnly? date = null,
        DateBasis? basis = null) =>
        new(title, versionClass, new HashSet<string>(languages ?? [], StringComparer.Ordinal),
            authors ?? ["J. R. R. Tolkien"], editionTitles ?? [], date, basis);

    private static IdentityVerdict Verdict(BookCandidate candidate, WorkIdentity work) =>
        CatalogIdentityPolicy.EvaluateRecord(candidate, work, CatalogEditionProjection.Project(candidate, null).Compatible);

    // ------------------------------------------------------------------------------------------------ the eight matrix rows

    [TestMethod]
    public void PA1HardcoverPaperbackAndEbookEditionsOfOneBookAreCompatibleAndShareProof()
    {
        var hardcoverRecord = Record(editions: [Edition("The Hobbit", Hardback, "Hardcover"), Edition("The Hobbit", Paperback)]);
        var googleRecord = Record(editions: [Edition("The Hobbit", Paperback)]);

        Assert.IsTrue(Verdict(googleRecord, Work()).IsCompatible);
        var proof = CatalogProof.Assess(
            CatalogEditionProjection.Project(googleRecord, null),
            new Dictionary<string, Guid> { [Hardback] = Guid.Parse("00000000-0000-0000-0000-00000000000a"), [Paperback] = Guid.Parse("00000000-0000-0000-0000-00000000000a") });
        Assert.AreEqual(ProofKind.SingleWork, proof.Kind);
        CollectionAssert.AreEqual(new[] { Hardback, Paperback }, CatalogEditionProjection.Project(hardcoverRecord, null).ProofIsbns.ToArray());
    }

    [TestMethod]
    public void CA1TheSameBookWithNoSharedIsbnHasNoProofSoItIsNeverUnified()
    {
        var other = Record(editions: [Edition("The Hobbit", Ebook, "Ebook")]);

        // Every other check passes; the evidence that would unify the two is simply absent.
        Assert.IsTrue(Verdict(other, Work()).IsCompatible);
        var proof = CatalogProof.Assess(
            CatalogEditionProjection.Project(other, null),
            new Dictionary<string, Guid> { [Hardback] = Guid.NewGuid() });
        Assert.AreEqual(ProofKind.None, proof.Kind);
    }

    [TestMethod]
    public void CA1bASharedIsbnWithADisjointAuthorIsACollision()
    {
        var wrongBook = Record(authors: ["Someone Else"], editions: [Edition("The Hobbit", Paperback)]);

        Assert.AreEqual(IdentityRejection.Author, Verdict(wrongBook, Work()).Rejection);
    }

    [TestMethod]
    public void PA2AnAudiobookEditionOfTheSameTextAndLanguageIsCompatible()
    {
        var record = Record(editions: [Edition("The Hobbit", Ebook, "Audiobook")], language: "en");

        Assert.IsTrue(Verdict(record, Work(languages: ["en"])).IsCompatible);
        Assert.AreEqual(1, CatalogEditionProjection.Project(record, null).Compatible.Count);
    }

    [TestMethod]
    public void CA2AnAbridgedAudiobookIsNeverAttachedToTheUnabridgedWork()
    {
        var abridged = Record(title: "The Hobbit (Abridged)", editions: [Edition("The Hobbit (Abridged)", Ebook, "Audiobook")]);

        Assert.AreEqual(IdentityRejection.Class, Verdict(abridged, Work()).Rejection);
    }

    [TestMethod]
    public void PA3AnOpenLibraryFamilyKeepsOnlyItsEnglishEditionsAndTheGermanOnesStayEvidence()
    {
        var family = Record(
            language: "en",
            editions:
            [
                Edition("The Hobbit", Paperback, language: "en"),
                Edition("The Hobbit", Ebook, "Ebook"),
                Edition("Der kleine Hobbit", German, language: "de")
            ]);

        var projection = CatalogEditionProjection.Project(family, null);

        CollectionAssert.AreEqual(new[] { Paperback, Ebook }, projection.ProofIsbns.ToArray());
        Assert.AreEqual(EditionExclusion.Language, projection.Excluded.Single().Exclusion);
        Assert.AreEqual(German, projection.Excluded.Single().Isbn13);
    }

    [TestMethod]
    public void CA3AGermanRecordIsNeverAttachedToAnEnglishOrALegacyWork()
    {
        var german = Record(title: "Der kleine Hobbit", language: "de", editions: [Edition("Der kleine Hobbit", German, language: "de")],
            workTitle: "The Hobbit");

        // Known to be English: a different language.
        Assert.AreEqual(IdentityRejection.Language, Verdict(german, Work(languages: ["en"])).Rejection);
        // Legacy: nothing says what language the Work is, so a non-default language cannot be checked.
        Assert.AreEqual(IdentityRejection.LanguageUnverifiable, Verdict(german, Work(languages: [])).Rejection);
        // Known to be German: compatible, on its own.
        Assert.IsTrue(Verdict(german, Work(title: "Der kleine Hobbit", languages: ["de"])).IsCompatible);
    }

    [TestMethod]
    public void PA4AnAbridgedRecordMatchesAnAbridgedWorkOnly()
    {
        var abridged = Record(title: "The Hobbit (Abridged)", editions: [Edition("The Hobbit (Abridged)", Paperback)]);

        Assert.IsTrue(Verdict(abridged, Work(title: "The Hobbit (Abridged)", versionClass: "Abridged")).IsCompatible);
        Assert.AreEqual(IdentityRejection.Class, Verdict(abridged, Work(versionClass: CatalogVersionClass.Text)).Rejection);
    }

    [TestMethod]
    public void PA5CA5AGraphicAdaptationNeverJoinsTheOriginalNovelDespiteTheTitleCore()
    {
        var graphic = Record(title: "Dune: The Graphic Novel", authors: ["Frank Herbert"], editions: [Edition("Dune: The Graphic Novel", Paperback)]);

        Assert.AreEqual(IdentityRejection.Class,
            Verdict(graphic, Work(title: "Dune", authors: ["Frank Herbert"])).Rejection);
        Assert.IsTrue(Verdict(graphic, Work(title: "Dune: The Graphic Novel", authors: ["Frank Herbert"], versionClass: "GraphicAdaptation")).IsCompatible);
    }

    [TestMethod]
    public void PA6AnOmnibusIsItsOwnClassAndATrilogyIsbnSetCollidesAcrossWorks()
    {
        var omnibus = Record(title: "The Jack Ryan Omnibus", editions: [Edition("The Jack Ryan Omnibus", Ebook)]);
        Assert.IsTrue(Verdict(omnibus, Work(title: "The Jack Ryan Omnibus", versionClass: "Collection")).IsCompatible);
        Assert.AreEqual(IdentityRejection.Class, Verdict(omnibus, Work(title: "The Jack Ryan Omnibus")).Rejection);

        // C-A6: ISBNs that belong to two different saved Works are not proof of either.
        var trilogy = Record(editions: [Edition("Book one", Hardback), Edition("Book two", Paperback)]);
        var first = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var second = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var proof = CatalogProof.Assess(
            CatalogEditionProjection.Project(trilogy, null),
            new Dictionary<string, Guid> { [Hardback] = first, [Paperback] = second });
        Assert.AreEqual(ProofKind.MultipleWorks, proof.Kind);
        CollectionAssert.AreEqual(new[] { first, second }, proof.WorkIds.ToArray());
    }

    [TestMethod]
    public void PA7CA7AnAliasNeedsTheMatchedRecordOrAnEqualEditionTitleNeverATitleAlone()
    {
        // The record's own title differs, but one of its compatible editions is titled exactly like an edition the Work has.
        var renamed = Record(title: "Bilbo's Journey", editions: [Edition("There and Back Again", Paperback)]);
        var work = Work(editionTitles: ["There and Back Again"]);
        Assert.IsTrue(Verdict(renamed, work).IsCompatible);
        Assert.AreEqual(IdentityRejection.Title, Verdict(renamed, Work()).Rejection);

        // Same title, a different author, no ISBN proof: rejected on the author, so nothing is attached or even tentative.
        var namesakeBook = Record(authors: ["Another Writer"]);
        Assert.AreEqual(IdentityRejection.Author, Verdict(namesakeBook, Work()).Rejection);
    }

    [TestMethod]
    public void PA8CA8AReissueUnderAnIllustratedTitleIsTheSameWorkButAWrongBooksIsbnIsNot()
    {
        var reissue = Record(title: "The Hobbit (Illustrated Classics)", editions: [Edition("The Hobbit (Illustrated Classics)", Paperback)]);
        Assert.IsTrue(Verdict(reissue, Work()).IsCompatible);

        var wrongBook = Record(title: "Cooking for Two", authors: ["A. Cook"], editions: [Edition("Cooking for Two", Paperback)]);
        Assert.IsFalse(Verdict(wrongBook, Work()).IsCompatible);
    }

    // ------------------------------------------------------------------------------------------------ the record gates (U3)

    [TestMethod]
    public void TheRecordGatesRunInOrderAndEachRejectsOnItsOwn()
    {
        Assert.AreEqual(IdentityRejection.ClassUnverifiable,
            Verdict(Record(title: "The Hobbit (Abridged)"), Work(versionClass: null)).Rejection, "A non-Text record against a legacy Work.");
        Assert.AreEqual(IdentityRejection.Class,
            Verdict(Record(), Work(title: "The Hobbit Study Guide", versionClass: null)).Rejection, "A legacy Work whose own title shows another class.");
        Assert.IsTrue(Verdict(Record(), Work(versionClass: null)).IsCompatible, "A Text record against a Text-looking legacy Work is allowed.");

        Assert.IsTrue(Verdict(Record(authors: []), Work()).IsCompatible, "An authorless record cannot contradict the Work's authors.");
        Assert.IsTrue(Verdict(Record(), Work(authors: [])).IsCompatible, "A Work with no credited authors cannot be contradicted.");
        Assert.IsTrue(Verdict(Record(authors: ["Tolkien, J. R. R."]), Work(authors: ["J. R. R. Tolkien"])).IsCompatible, "Last, First is the same person.");
    }

    [TestMethod]
    public void TheOriginalYearGateOnlyComparesOriginalOrLegacyYears()
    {
        var work = Work(date: new DateOnly(1937, 1, 1), basis: DateBasis.Original);

        Assert.IsTrue(Verdict(Record(date: new DateOnly(1938, 5, 5), basis: DateBasis.Original), work).IsCompatible, "Off by one year is tolerated.");
        Assert.AreEqual(IdentityRejection.Year,
            Verdict(Record(date: new DateOnly(1999, 1, 1), basis: DateBasis.Original), work).Rejection);
        Assert.IsTrue(Verdict(Record(date: new DateOnly(2012, 9, 18), basis: DateBasis.EditionDerived), work).IsCompatible,
            "An edition's own date legitimately differs by decades.");
        Assert.IsTrue(Verdict(Record(date: new DateOnly(1999, 1, 1), basis: DateBasis.Original),
            Work(date: new DateOnly(1937, 1, 1), basis: DateBasis.EditionDerived)).IsCompatible);
        Assert.AreEqual(IdentityRejection.Year,
            Verdict(Record(date: new DateOnly(1999, 1, 1), basis: DateBasis.Unknown), Work(date: new DateOnly(1937, 1, 1), basis: null)).Rejection,
            "A legacy Work date is read as a first-publication year.");
    }

    // ------------------------------------------------------------------------------------------------ edition projection (U2)

    [TestMethod]
    public void U21AMixedStandardAndAbridgedTextRecordOnlyOffersItsStandardIsbn()
    {
        var record = Record(title: "Dune", editions: [Edition("Dune", Paperback), Edition("Dune (Abridged Edition)", Ebook, "Ebook")]);

        var projection = CatalogEditionProjection.Project(record, null);

        Assert.AreEqual("Text", CatalogVersionClass.ForRecord(record), "The record-level class hides the abridged edition: that is the defect.");
        CollectionAssert.AreEqual(new[] { Paperback }, projection.ProofIsbns.ToArray());
        Assert.AreEqual(EditionExclusion.Class, projection.Excluded.Single().Exclusion);
        CollectionAssert.AreEqual(new[] { Ebook }, projection.ExcludedOnlyIsbns.ToArray());
    }

    [TestMethod]
    public void U22AStudyGuideEditionIsExcludedFromAStandardRecord()
    {
        var record = Record(title: "Dune", editions: [Edition("Dune", Paperback), Edition("Dune Study Guide", Ebook, "Ebook")]);

        var projection = CatalogEditionProjection.Project(record, null);

        CollectionAssert.AreEqual(new[] { Paperback }, projection.ProofIsbns.ToArray());
        Assert.AreEqual(EditionExclusion.Class, projection.Excluded.Single().Exclusion);
    }

    [TestMethod]
    public void U23AnOverlapOnlyOnARejectedEditionIsNoProofNotAnAttachAndNotACollision()
    {
        var record = Record(title: "Dune", editions: [Edition("Dune", German.Replace('3', '4')), Edition("Dune (Abridged Edition)", Ebook, "Ebook")]);
        var projection = CatalogProjectionOf(record);
        var existing = new Dictionary<string, Guid> { [Ebook] = Guid.NewGuid() };

        Assert.AreEqual(ProofKind.ExcludedEditionOverlapOnly, CatalogProof.Assess(projection, existing).Kind);

        // The counter-case: the overlap is on the standard edition, so it is proof, and only that edition attaches.
        var work = Guid.NewGuid();
        var onStandard = new Dictionary<string, Guid> { [projection.ProofIsbns[0]] = work };
        var proof = CatalogProof.Assess(projection, onStandard);
        Assert.AreEqual(ProofKind.SingleWork, proof.Kind);
        Assert.AreEqual(work, proof.TargetWorkId);
        var targeted = CatalogEditionProjection.Project(record, ProjectionTarget.ForWork(
            CatalogVersionClass.Text, new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal)));
        Assert.AreEqual(1, targeted.Compatible.Count);
        Assert.AreEqual(EditionExclusion.Class, targeted.Excluded.Single().Exclusion);
    }

    [TestMethod]
    public void U24AGermanEditionSharingAnEnglishWorksIsbnIsNoProof()
    {
        var record = Record(language: "en", editions: [Edition("The Hobbit", Paperback, language: "en"), Edition("Der kleine Hobbit", Ebook, language: "de")]);
        var projection = CatalogProjectionOf(record);

        Assert.AreEqual(ProofKind.ExcludedEditionOverlapOnly,
            CatalogProof.Assess(projection, new Dictionary<string, Guid> { [Ebook] = Guid.NewGuid() }).Kind);
        Assert.AreEqual(EditionExclusion.Language, projection.Excluded.Single().Exclusion);
    }

    [TestMethod]
    public void AnExcludedEditionNeverCountsTowardAMultiWorkCollision()
    {
        var record = Record(title: "Dune", editions: [Edition("Dune", Paperback), Edition("Dune (Abridged Edition)", Ebook, "Ebook")]);
        var standardWork = Guid.NewGuid();
        var otherWork = Guid.NewGuid();

        var proof = CatalogProof.Assess(
            CatalogProjectionOf(record), new Dictionary<string, Guid> { [Paperback] = standardWork, [Ebook] = otherWork });

        Assert.AreEqual(ProofKind.SingleWork, proof.Kind, "The abridged edition's owner is not a second candidate Work.");
        Assert.AreEqual(standardWork, proof.TargetWorkId);
    }

    [TestMethod]
    public void TheTargetAwarePassAppliesThePerWorkLanguageOwnershipAndLegacyClassRules()
    {
        var record = Record(language: "en", editions:
        [
            Edition("The Hobbit", Paperback, language: "en"),
            Edition("The Hobbit", Ebook, "Ebook"),
            Edition("The Hobbit", German, language: "de")
        ]);
        var owned = new HashSet<string>([Ebook], StringComparer.Ordinal);

        var existing = CatalogEditionProjection.Project(record, ProjectionTarget.ForWork(null, new HashSet<string>(["en"], StringComparer.Ordinal), owned));

        Assert.AreEqual(EditionExclusion.OwnedByOtherWork, existing.Editions.Single(edition => edition.Isbn13 == Ebook).Exclusion);
        Assert.AreEqual(EditionExclusion.Language, existing.Editions.Single(edition => edition.Isbn13 == German).Exclusion);
        CollectionAssert.AreEqual(new[] { Paperback }, existing.ProofIsbns.ToArray());

        // Legacy Work whose editions state no language: an English edition is allowed, a German one cannot be verified. (A record that
        // lists both with no language of its own is a tied family, which no target may accept either: see the V1 cases above.)
        var legacyTarget = ProjectionTarget.ForWork(null, new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));
        var legacyEnglish = CatalogEditionProjection.Project(Record(language: "en", editions: [Edition("The Hobbit", Paperback, language: "en")]), legacyTarget);
        var legacyGerman = CatalogEditionProjection.Project(Record(editions: [Edition("Der Hobbit", German, language: "de")]), legacyTarget);
        Assert.IsTrue(legacyEnglish.Editions[0].IsCompatible);
        Assert.AreEqual(EditionExclusion.LanguageUnverifiable, legacyGerman.Editions[0].Exclusion);

        // When creating, the Work's own languages are not a constraint.
        var creation = CatalogEditionProjection.Project(
            Record(editions: [Edition("The Hobbit", Paperback, language: "en")]),
            ProjectionTarget.ForCreation(CatalogVersionClass.Text, new HashSet<string>(StringComparer.Ordinal)));
        Assert.AreEqual(1, creation.Compatible.Count);
    }

    [TestMethod]
    public void ARejectedEditionCannotEstablishProofForAnOtherwiseStandardRecordAndInvalidIsbnsAreNotIdentifiers()
    {
        var record = Record(title: "Dune", editions:
        [
            Edition("Dune Study Guide", Hardback, "Hardcover"),
            Edition("Dune", "978-0-00", "Paperback"),
            Edition("", Ebook)
        ]);

        var projection = CatalogEditionProjection.Project(record, null);

        Assert.AreEqual(0, projection.ProofIsbns.Count, "The only well-formed ISBN belongs to the rejected study guide.");
        Assert.AreEqual(EditionExclusion.Untitled, projection.Editions[2].Exclusion);
        Assert.IsNull(projection.Editions[1].Isbn13, "A malformed ISBN is no identifier.");
        Assert.IsTrue(projection.Editions[1].IsCompatible, "The edition itself may still be written; it simply has no ISBN.");
    }

    [TestMethod]
    public void TheEffectiveLanguageIsTheRecordsOwnElseAStrictMajorityOfItsEditionsElseUnknown()
    {
        Assert.AreEqual("en", CatalogEditionProjection.EffectiveLanguage(Record(language: "en-US")));
        Assert.AreEqual("en", CatalogEditionProjection.EffectiveLanguage(Record(editions:
            [Edition("a", Paperback, language: "eng"), Edition("b", Ebook, language: "en"), Edition("c", German, language: "de")])));
        Assert.IsNull(CatalogEditionProjection.EffectiveLanguage(Record(editions:
            [Edition("a", Paperback, language: "en"), Edition("b", Ebook, language: "de")])), "A tie is not a majority.");
        Assert.IsNull(CatalogEditionProjection.EffectiveLanguage(Record()));
    }

    private static EditionProjection CatalogProjectionOf(BookCandidate record) => CatalogEditionProjection.Project(record, null);

    // ------------------------------------------------------------------------------------------------ V1: a tied mixed-language family

    private static BookCandidate TiedFamily() => Record(editions:
    [
        Edition("The Hobbit", Hardback, "Hardcover", "en"),
        Edition("Der Hobbit", German, "Hardcover", "de"),
        Edition("The Hobbit", Ebook)
    ]);

    [TestMethod]
    public void AnUnknownRecordLanguageCannotWaiveAKnownContradictionBetweenItsEditions()
    {
        var record = TiedFamily();
        var existingEnglish = ProjectionTarget.ForWork(CatalogVersionClass.Text, new HashSet<string>(["en"], StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));
        var legacy = ProjectionTarget.ForWork(null, new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));
        var creation = ProjectionTarget.ForCreation(CatalogVersionClass.Text, new HashSet<string>(StringComparer.Ordinal));

        foreach (var (name, target) in new (string, ProjectionTarget?)[]
                 {
                     ("candidate-intrinsic", null), ("creation", creation), ("existing English Work", existingEnglish), ("legacy Work", legacy)
                 })
        {
            var projection = CatalogEditionProjection.Project(record, target);

            // No translation is a winner and no ISBN of either is proof: only the edition that states no language stays eligible.
            CollectionAssert.AreEqual(new[] { Ebook }, projection.ProofIsbns.ToArray(), name);
            CollectionAssert.AreEquivalent(new[] { Hardback, German }, projection.ExcludedOnlyIsbns.ToArray(), name);
            Assert.IsTrue(projection.Excluded.All(edition => edition.Edition.Language is not null), name);
            Assert.AreEqual(1, projection.Compatible.Count, name);
        }
    }

    [TestMethod]
    public void EveryShapeOfNoLanguageMajorityIsAConflictAndNeverACoherentLanguageView()
    {
        var threeWay = Record(editions:
            [Edition("a", Hardback, language: "en"), Edition("b", German, language: "de"), Edition("c", Paperback, language: "fr")]);
        var twoTwo = Record(editions:
            [Edition("a", Hardback, language: "en"), Edition("b", Paperback, language: "eng"), Edition("c", German, language: "de"), Edition("d", Ebook, language: "ger")]);

        foreach (var record in new[] { threeWay, twoTwo })
        {
            Assert.IsNull(CatalogEditionProjection.EffectiveLanguage(record));
            Assert.IsTrue(CatalogEditionProjection.HasConflictingLanguages(record));
            Assert.IsEmpty(CatalogProjectionOf(record).ProofIsbns, "No ISBN of a conflicted family is proof.");
            Assert.IsEmpty(CatalogProjectionOf(record).Compatible);
        }
    }

    [TestMethod]
    public void ACoherentLanguageViewIsStillHonouredAndOnlyTheDissentIsExcluded()
    {
        var strictMajority = Record(editions:
            [Edition("a", Hardback, language: "en"), Edition("b", Paperback, language: "eng"), Edition("c", German, language: "de")]);
        var explicitLanguage = Record(language: "en", editions: [Edition("a", Hardback, language: "en"), Edition("c", German, language: "de")]);
        var allGerman = Record(editions: [Edition("Der Hobbit", German, language: "de"), Edition("Der Hobbit", Ebook, language: "de")]);
        var noLanguageAtAll = Record(editions: [Edition("The Hobbit", Hardback), Edition("The Hobbit", Ebook)]);

        Assert.IsFalse(CatalogEditionProjection.HasConflictingLanguages(strictMajority));
        CollectionAssert.AreEquivalent(new[] { Hardback, Paperback }, CatalogProjectionOf(strictMajority).ProofIsbns.ToArray());
        Assert.AreEqual(EditionExclusion.Language, CatalogProjectionOf(strictMajority).Excluded.Single().Exclusion);

        Assert.IsFalse(CatalogEditionProjection.HasConflictingLanguages(explicitLanguage));
        CollectionAssert.AreEqual(new[] { Hardback }, CatalogProjectionOf(explicitLanguage).ProofIsbns.ToArray());

        Assert.IsFalse(CatalogEditionProjection.HasConflictingLanguages(allGerman));
        Assert.AreEqual("de", CatalogEditionProjection.EffectiveLanguage(allGerman));
        Assert.AreEqual(2, CatalogProjectionOf(allGerman).ProofIsbns.Count);

        Assert.IsFalse(CatalogEditionProjection.HasConflictingLanguages(noLanguageAtAll), "Admitted NULL-language uncertainty is not a conflict.");
        Assert.AreEqual(2, CatalogProjectionOf(noLanguageAtAll).ProofIsbns.Count);
    }

    [TestMethod]
    public void AConflictedFamilyEditionIsReportedAsALanguageConflictNeverAsAContradictionOfASavedEdition()
    {
        var projection = CatalogEditionProjection.Project(TiedFamily(), null);

        Assert.HasCount(2, projection.Excluded);
        Assert.IsTrue(projection.Excluded.All(edition => edition.Exclusion == EditionExclusion.LanguageConflict));
    }

    // ------------------------------------------------------------------------------------------------ binding (U1)

    [TestMethod]
    public void U1AnAmbiguousNameNeverBindsToAnExistingNamesake()
    {
        var oldest = new NameMatch(Guid.Parse("00000000-0000-0000-0000-00000000000a"), false);
        var newest = new NameMatch(Guid.Parse("00000000-0000-0000-0000-00000000000b"), false);

        // Whatever order the namesakes arrive in, neither acquires the credit.
        foreach (var order in new[] { new[] { oldest, newest }, new[] { newest, oldest } })
        {
            var decision = CatalogBindingPolicy.DecideAuthor(order, [], hasSourceId: false);
            Assert.AreEqual(BindingAction.CreateNew, decision.Action);
            Assert.AreEqual(AuthorBindingBasis.Provisional, decision.Basis);
            Assert.IsNull(decision.ExistingId);
        }

        Assert.AreEqual(BindingAction.LeaveUnbound, CatalogBindingPolicy.DecideSeries([oldest, newest], [], false).Action);
    }

    [TestMethod]
    public void U1NameAndCreationBindingsAreUnchangedForZeroOrOneMatch()
    {
        var only = new NameMatch(Guid.NewGuid(), false);

        var byName = CatalogBindingPolicy.DecideAuthor([only], [], false);
        Assert.AreEqual((BindingAction.BindExisting, AuthorBindingBasis.Name, (Guid?)only.Id), (byName.Action, byName.Basis, byName.ExistingId));
        var created = CatalogBindingPolicy.DecideAuthor([], [], false);
        Assert.AreEqual((BindingAction.CreateNew, AuthorBindingBasis.Created), (created.Action, created.Basis));

        var seriesByName = CatalogBindingPolicy.DecideSeries([only], [], false);
        Assert.AreEqual((BindingAction.BindExisting, SeriesBindingBasis.Name), (seriesByName.Action, seriesByName.Basis));
        Assert.AreEqual(SeriesBindingBasis.Created, CatalogBindingPolicy.DecideSeries([], [], false).Basis);
    }

    [TestMethod]
    public void U1ASourceIdSelectsExactlyOneNamesakeOrStaysProvisional()
    {
        var first = new NameMatch(Guid.Parse("00000000-0000-0000-0000-000000000001"), false);
        var second = new NameMatch(Guid.Parse("00000000-0000-0000-0000-000000000002"), false);

        var bound = CatalogBindingPolicy.DecideAuthor([first, second], [second.Id], hasSourceId: true);
        Assert.AreEqual((BindingAction.BindExisting, AuthorBindingBasis.Reference, (Guid?)second.Id), (bound.Action, bound.Basis, bound.ExistingId));

        var conflict = CatalogBindingPolicy.DecideAuthor([first, second], [first.Id, second.Id], hasSourceId: true);
        Assert.AreEqual((BindingAction.CreateNew, AuthorBindingBasis.Provisional, true), (conflict.Action, conflict.Basis, conflict.SourceIdConflict));

        var seriesBound = CatalogBindingPolicy.DecideSeries([first, second], [first.Id], true);
        Assert.AreEqual((BindingAction.BindExisting, SeriesBindingBasis.Reference, (Guid?)first.Id), (seriesBound.Action, seriesBound.Basis, seriesBound.ExistingId));
        Assert.AreEqual(BindingAction.LeaveUnbound, CatalogBindingPolicy.DecideSeries([first, second], [first.Id, second.Id], true).Action);
    }

    [TestMethod]
    public void U1ANamesakeTiedToADifferentSourceIdIsAnotherPersonNotAMatch()
    {
        var other = new NameMatch(Guid.NewGuid(), ReferencesDifferentSourceId: true);

        var author = CatalogBindingPolicy.DecideAuthor([other], [], hasSourceId: true);
        Assert.AreEqual((BindingAction.CreateNew, AuthorBindingBasis.Created), (author.Action, author.Basis));
        // Without a source id the same row is a plain name match: nothing contradicts it.
        Assert.AreEqual(AuthorBindingBasis.Name, CatalogBindingPolicy.DecideAuthor([other], [], hasSourceId: false).Basis);
    }

    // ------------------------------------------------------------------------------------------------ publication dates

    [TestMethod]
    public void PublicationStatusFollowsTheWholeIntervalOfTheStatedPrecision()
    {
        var today = new DateOnly(2026, 11, 15);

        Assert.AreEqual(PublicationStatus.Published, PublicationStatusPolicy.Evaluate(new DateOnly(2026, 11, 15), DatePrecision.Day, today));
        Assert.AreEqual(PublicationStatus.Published, PublicationStatusPolicy.Evaluate(new DateOnly(2026, 11, 14), null, today));
        Assert.AreEqual(PublicationStatus.Forthcoming, PublicationStatusPolicy.Evaluate(new DateOnly(2026, 11, 16), DatePrecision.Day, today),
            "A future full date is never published (previously stored as Published).");
        Assert.AreEqual(PublicationStatus.Published, PublicationStatusPolicy.Evaluate(new DateOnly(2025, 1, 1), DatePrecision.Year, today));
        Assert.AreEqual(PublicationStatus.Published, PublicationStatusPolicy.Evaluate(new DateOnly(2026, 10, 1), DatePrecision.Month, today));
        Assert.AreEqual(PublicationStatus.Unknown, PublicationStatusPolicy.Evaluate(new DateOnly(2026, 11, 1), DatePrecision.Month, today),
            "This month has not ended: nothing is claimed either way.");
        Assert.AreEqual(PublicationStatus.Unknown, PublicationStatusPolicy.Evaluate(new DateOnly(2026, 1, 1), DatePrecision.Year, today),
            "A year that contains today has not ended.");
        Assert.AreEqual(PublicationStatus.Forthcoming, PublicationStatusPolicy.Evaluate(new DateOnly(2027, 1, 1), DatePrecision.Year, today));
        Assert.AreEqual(PublicationStatus.Forthcoming, PublicationStatusPolicy.Evaluate(new DateOnly(2026, 12, 1), DatePrecision.Month, today));
        Assert.AreEqual(PublicationStatus.Unknown, PublicationStatusPolicy.Evaluate(null, null, today));
    }

    private static DateClaim Date(string provider, int year, int month, int day, DatePrecision precision, DateBasis basis, string source = "x") =>
        new(provider, source, new DateOnly(year, month, day), precision, basis);

    [TestMethod]
    public void AnOriginalDateSupersedesAnEditionDerivedFallbackAndTheFallbackNeverBeatsAnUnknownBasisValue()
    {
        var fallback = Date("google", 2012, 9, 18, DatePrecision.Day, DateBasis.EditionDerived);
        var original = Date("openlibrary", 1937, 1, 1, DatePrecision.Year, DateBasis.Original);
        var legacy = Date("legacy-unattributed", 1937, 1, 1, DatePrecision.Day, DateBasis.Unknown);

        Assert.AreEqual(fallback, PublicationDateSelection.Select([fallback])!.Selected, "A fallback is used while it is all there is.");
        Assert.AreEqual(original, PublicationDateSelection.Select([fallback, original])!.Selected);
        Assert.AreEqual(legacy, PublicationDateSelection.Select([fallback, legacy])!.Selected, "Google's fallback never overwrites a legacy value.");
        Assert.AreEqual(original, PublicationDateSelection.Select([legacy, fallback, original])!.Selected);
    }

    [TestMethod]
    public void WithinARankTheMostPreciseConsistentDateWinsAndDisjointOnesConflict()
    {
        var year = Date("a", 1937, 1, 1, DatePrecision.Year, DateBasis.Original);
        var month = Date("b", 1937, 9, 1, DatePrecision.Month, DateBasis.Original);
        var day = Date("c", 1937, 9, 21, DatePrecision.Day, DateBasis.Original);
        var other = Date("d", 1938, 1, 1, DatePrecision.Year, DateBasis.Original);

        var chain = PublicationDateSelection.Select([year, month, day])!;
        Assert.AreEqual(day, chain.Selected);
        Assert.IsFalse(chain.Conflict);

        var conflicted = PublicationDateSelection.Select([other, year, month])!;
        Assert.IsTrue(conflicted.Conflict);
        Assert.AreEqual(1937, conflicted.Selected.Date.Year, "Disjoint intervals show the earliest, and say so.");
    }

    [TestMethod]
    public void TheDateSelectionDoesNotDependOnArrivalOrder()
    {
        DateClaim[] claims =
        [
            Date("google", 2012, 9, 18, DatePrecision.Day, DateBasis.EditionDerived),
            Date("hardcover", 1937, 9, 21, DatePrecision.Day, DateBasis.Unknown),
            Date("openlibrary", 1937, 1, 1, DatePrecision.Year, DateBasis.Original),
            Date("example", 1937, 9, 1, DatePrecision.Month, DateBasis.Original)
        ];

        var expected = PublicationDateSelection.Select(claims)!;
        foreach (var permutation in Permutations(claims))
        {
            Assert.AreEqual(expected, PublicationDateSelection.Select(permutation));
        }
    }

    // ------------------------------------------------------------------------------------------------ series positions (R3)

    private static PositionClaim Position(string provider, decimal? sort, string? label = null, string source = "s") =>
        new(provider, source, label ?? sort?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), sort);

    [TestMethod]
    public void R3aTwoSourcesDisagreeingEndUpIdenticalInEitherArrivalOrder()
    {
        var one = Position("p1", 1m);
        var two = Position("p2", 2m);

        Assert.AreEqual(SeriesPositionState.SingleSource, SeriesPositionSelection.Select([one])!.State);
        Assert.AreEqual(SeriesPositionState.SingleSource, SeriesPositionSelection.Select([two])!.State);
        var forwards = SeriesPositionSelection.Select([one, two])!;
        var backwards = SeriesPositionSelection.Select([two, one])!;

        Assert.AreEqual(SeriesPositionState.Conflicted, forwards.State);
        Assert.AreEqual(forwards.State, backwards.State);
        Assert.AreEqual(forwards.Selected, backwards.Selected);
        Assert.AreEqual(1m, forwards.Selected.Sort, "Equal support falls to the lowest number.");
        CollectionAssert.AreEqual(OneAndTwo, forwards.DistinctValues.ToArray());
    }

    [TestMethod]
    public void R3bASourceChangingItsMindReplacesItsOwnValueAndStillConflictsWithAnother()
    {
        var firstValue = Position("p1", 1m);
        var laterValue = Position("p1", 2m);
        var other = Position("p2", 1m);

        Assert.AreEqual(SeriesPositionState.SingleSource, SeriesPositionSelection.Select([laterValue])!.State);
        Assert.AreEqual(2m, SeriesPositionSelection.Select([laterValue])!.Selected.Sort);
        Assert.AreEqual(SeriesPositionState.Conflicted, SeriesPositionSelection.Select([laterValue, other])!.State);
        Assert.AreEqual(SeriesPositionState.Agreed, SeriesPositionSelection.Select([firstValue, other])!.State);
    }

    [TestMethod]
    public void R3cR3dCorrelationLimitsTheTieBreakNeverTheDisagreement()
    {
        // Two sources of one family disagreeing is still a conflict.
        var sameFamily = SeriesPositionSelection.Select([Position("p1", 1m, source: "a"), Position("p1", 2m, source: "b")])!;
        Assert.AreEqual(SeriesPositionState.Conflicted, sameFamily.State);

        // A majority with dissent: the majority value is selected and the dissent stays visible.
        var majority = SeriesPositionSelection.Select([Position("p1", 1m), Position("p2", 1m), Position("p3", 2m)])!;
        Assert.AreEqual(SeriesPositionState.Conflicted, majority.State);
        Assert.AreEqual(1m, majority.Selected.Sort);
        CollectionAssert.AreEqual(OneAndTwo, majority.DistinctValues.ToArray());

        // The majority is by evidence family, so the higher number can win on support even though the lowest would win a tie.
        var support = SeriesPositionSelection.Select([Position("p1", 3m), Position("p2", 3m), Position("p3", 1m)])!;
        Assert.AreEqual(3m, support.Selected.Sort);
        Assert.IsNull(SeriesPositionSelection.Select([]));
    }

    [TestMethod]
    public void ASeriesPositionSelectionIsAnOrderIndependentFunctionOfTheClaimSet()
    {
        PositionClaim[] claims =
        [
            Position("p1", 1m, source: "x"), Position("p2", 2m, source: "y"), Position("p3", 1m, source: "z"),
            Position("p4", null, label: "Prologue", source: "w")
        ];

        var expected = SeriesPositionSelection.Select(claims)!;
        foreach (var permutation in Permutations(claims))
        {
            var actual = SeriesPositionSelection.Select(permutation)!;
            Assert.AreEqual(expected.Selected, actual.Selected);
            Assert.AreEqual(expected.State, actual.State);
            CollectionAssert.AreEqual(expected.DistinctValues.ToArray(), actual.DistinctValues.ToArray());
        }
    }

    [TestMethod]
    public void AnOrderingKindIsOnlyReadFromAnExplicitQualifier()
    {
        Assert.AreEqual(SeriesOrderingKind.Chronological, SeriesOrdering.FromLabel("The Expanse (Chronological)"));
        Assert.AreEqual(SeriesOrderingKind.Reading, SeriesOrdering.FromLabel("Cosmere (Reading Order)"));
        Assert.AreEqual(SeriesOrderingKind.Publication, SeriesOrdering.FromLabel("Dune (Publication Order)"));
        Assert.AreEqual(SeriesOrderingKind.Unspecified, SeriesOrdering.FromLabel("The Expanse"), "Unqualified is never assumed to be publication order.");
        Assert.AreEqual(SeriesOrderingKind.Unspecified, SeriesOrdering.FromLabel("Series (Complete)"));
        Assert.AreEqual(SeriesOrderingKind.Reading,
            SeriesOrdering.Of(new BookSeriesCandidate("Plain", "1", true) { OrderingKind = SeriesOrderingKind.Reading }),
            "A provider's own statement wins over the label.");
    }

    private static IEnumerable<T[]> Permutations<T>(T[] items)
    {
        if (items.Length <= 1)
        {
            yield return items;
            yield break;
        }

        for (var index = 0; index < items.Length; index++)
        {
            var rest = items.Where((_, other) => other != index).ToArray();
            foreach (var tail in Permutations(rest))
            {
                yield return [items[index], .. tail];
            }
        }
    }
}
