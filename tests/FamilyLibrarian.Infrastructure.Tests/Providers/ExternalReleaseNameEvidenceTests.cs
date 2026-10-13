using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Providers;

namespace FamilyLibrarian.Infrastructure.Tests.Providers;

/// <summary>
/// Every case here is a real release name captured from a live an indexer
/// <c>/search</c> response for "Fahrenheit 451" by Ray Bradbury on
/// <c>toontown-int-srv2</c> (PROVIDER-7). The acceptances and the refusals are
/// equally load-bearing: four of these names contain both the expected title
/// and the expected author and are still not the requested work.
/// </summary>
[TestClass]
public sealed class ExternalReleaseNameEvidenceTests
{
    [TestMethod]
    public void JoinedBookNumberAndFormatInTheLiveNameAreExplained()
    {
        var verdict = ExternalReleaseNameEvidence.Evaluate(
            "req.Fourth.Wing.Fourth.Wing.Book.1m4b", ["Fourth Wing"], "Rebecca Yarros");
        Assert.IsTrue(verdict.IsStrictWorkAssertion);
        Assert.IsTrue(verdict.AssertsExpectedTitle);
        Assert.IsFalse(verdict.AssertsExpectedAuthor);
        Assert.HasCount(0, verdict.UnexplainedTokens);
        Assert.AreEqual("m4b", verdict.AssertedFormat);
    }

    [TestMethod]
    [DataRow("Book.0m4b")]
    [DataRow("Book.1m4bx")]
    [DataRow("Book.1unknown")]
    [DataRow("Book.m4b")]
    [DataRow("Book.999999999999999m4b")]
    public void InvalidJoinedBookPackagingIsNotSwallowed(string packaging) =>
        Assert.IsFalse(ExternalReleaseNameEvidence.Evaluate(
            $"Fourth.Wing.{packaging}", ["Fourth Wing"], "Rebecca Yarros").IsStrictWorkAssertion);

    [TestMethod]
    public void TerminalFormatIsRecognizedIndependentlyOfBookNumberPackaging()
    {
        var verdict = ExternalReleaseNameEvidence.Evaluate("Fourth.Wingm4b", ["Fourth Wing"], "Rebecca Yarros");
        Assert.AreEqual("m4b", verdict.AssertedFormat);
        Assert.IsTrue(verdict.IsStrictWorkAssertion);
        var unrelated = ExternalReleaseNameEvidence.Evaluate("Other.Bookm4b", ["Fourth Wing"], "Rebecca Yarros");
        Assert.AreEqual("m4b", unrelated.AssertedFormat);
        Assert.IsFalse(unrelated.IsStrictWorkAssertion);
    }

    [TestMethod]
    public void JoinedFormatDoesNotEraseContradictorySeriesOrExtraTitleEvidence()
    {
        Assert.IsFalse(ExternalReleaseNameEvidence.Evaluate("Fourth.Wing.Book.2m4b", ["Fourth Wing"],
            "Rebecca Yarros", [new BookSeries("Empyrean", "1")]).IsStrictWorkAssertion);
        Assert.IsFalse(ExternalReleaseNameEvidence.Evaluate("Fourth.Wing.Other.Stories.Book.1m4b",
            ["Fourth Wing"], "Rebecca Yarros").IsStrictWorkAssertion);
        Assert.IsTrue(ExternalReleaseNameEvidence.Evaluate("Fourth.Wing.Book.1m4b", ["Fourth Wing"],
            "Rebecca Yarros", [new BookSeries("Empyrean", "1")]).IsStrictWorkAssertion);
    }

    private const string Title = "Fahrenheit 451";
    private const string Author = "Ray Bradbury";

    private static ReleaseNameVerdict Evaluate(string releaseName) =>
        ExternalReleaseNameEvidence.Evaluate(releaseName, [Title], Author);

    [TestMethod]
    [DataRow("Fahrenheit 451 by Ray Bradbury EPUB")]
    [DataRow("Fahrenheit 451 - Ray Bradbury - eBook [EPUB, MOBI, RTF]")]
    [DataRow("Ray Bradbury - Fahrenheit 451 - epub [TKRG]")]
    [DataRow("Fahrenheit 451 - Ray Bradbury")]
    [DataRow("Fahrenheit 451 (60th Anniversary Edition) by Ray Bradbury EPUB")]
    [DataRow("Ray Bradbury - Fahrenheit 451 (60th Anniversary) (epub)")]
    [DataRow("Ray Bradbury - Fahrenheit 451 (retail) (epub)")]
    [DataRow("Ray Bradbury - Fahrenheit 451 (60th Anniversary)")]
    [DataRow("Ray Bradbury - Fahrenheit 451 60th Anniversary edition (retail) (mobi)")]
    [DataRow("Ray Bradbury - Fahrenheit 451 (60th Anniversary) (retail)(epub)")]
    [DataRow("Ray.Bradbury-Fahrenheit.451")]
    public void ACapturedNameThatFullyExplainsItselfAssertsTheRequestedWork(string releaseName)
    {
        var verdict = Evaluate(releaseName);

        Assert.IsTrue(
            verdict.IsStrictWorkAssertion,
            $"'{releaseName}' should assert the work; unexplained: [{string.Join(", ", verdict.UnexplainedTokens)}]");
    }

    [TestMethod]
    public void AStoryCollectionSharingTheTitleAndAuthorIsRefused()
    {
        // The critical safety case: title present, author present, different book.
        var verdict = Evaluate("Ray Bradbury - A Pleasure to Burn-Fahrenheit 451 Stories (retail) (epub)");

        Assert.IsFalse(verdict.IsStrictWorkAssertion);
        Assert.IsGreaterThan(0, verdict.UnexplainedTokens.Count);
    }

    [TestMethod]
    public void AGraphicNovelAdaptationIsRefused()
    {
        var verdict = Evaluate("Ray Bradbury's Fahrenheit 451 Graphic Novel");

        Assert.IsFalse(verdict.IsStrictWorkAssertion);
    }

    [TestMethod]
    public void ASlashSeparatedRadioDramatizationIsRefused()
    {
        var verdict = Evaluate("Fahrenheit 451 / Ray Bradbury / BBC Radio Audio Drama");

        Assert.IsFalse(verdict.IsStrictWorkAssertion);
        Assert.IsTrue(verdict.Conditions!.Count > 0, "A dramatization is an acquisition condition, not a title contradiction.");
    }

    [TestMethod]
    public void AnUnclassifiedBroadcasterTagIsRetainedWithoutInventingAnEdition()
    {
        // A broadcaster label alone does not establish a dramatization.
        var verdict = Evaluate("Ray Bradbury - Fahrenheit 451 (BBC)");

        Assert.IsTrue(verdict.IsStrictWorkAssertion);
        Assert.Contains("BBC", verdict.UnexplainedTokens);
    }

    [TestMethod]
    public void AForeignLanguageReleaseReportsItsLanguageInsteadOfSwallowingIt()
    {
        // The release name is the only place this candidate's language appears.
        // Stripping "Spanish" as a release tag would hand a Spanish EPUB to an
        // English request as a confident automatic acquisition.
        var verdict = Evaluate("Ray.Bradbury-Fahrenheit.451.2012.Spanish.Retail.EPUB.eBook-BitBook");

        Assert.AreEqual("es", verdict.AssertedLanguage);
        Assert.IsTrue(verdict.AssertsExpectedTitle);
        Assert.IsTrue(verdict.AssertsExpectedAuthor);
    }

    [TestMethod]
    public void AudiobookNarrationCreditIsExtractedFromTheReleaseName()
    {
        var verdict = Evaluate("Fahrenheit 451 by Ray Bradbury (read by Christopher Hurt)");

        Assert.IsTrue(verdict.IsStrictWorkAssertion);
        Assert.AreEqual("Christopher Hurt", verdict.AssertedNarrator);
    }

    [TestMethod]
    public void AFormatTokenInTheNameIsReportedWhenTheProviderReportedNoFormat()
    {
        var verdict = Evaluate("Ray Bradbury - Fahrenheit 451 - epub [TKRG]");

        Assert.AreEqual("epub", verdict.AssertedFormat);
    }

    [TestMethod]
    public void AMissingAuthorDoesNotPreventAnExactTitleAssertion()
    {
        // External acquisition uses author for rank, independently of the
        // destination matcher which retains its own title/author policy.
        var verdict = ExternalReleaseNameEvidence.Evaluate("Fahrenheit 451 EPUB", [Title], Author);

        Assert.IsTrue(verdict.AssertsExpectedTitle);
        Assert.IsFalse(verdict.AssertsExpectedAuthor);
        Assert.IsTrue(verdict.IsStrictWorkAssertion);
    }

    [TestMethod]
    public void ADifferentBookByTheSameAuthorIsNotAnAssertion()
    {
        var verdict = Evaluate("Ray Bradbury - The Martian Chronicles (retail) (epub)");

        Assert.IsFalse(verdict.AssertsExpectedTitle);
        Assert.IsFalse(verdict.IsStrictWorkAssertion);
    }

    [TestMethod]
    public void ADifferentAuthorSharingTheTitleIsNotAnAssertion()
    {
        var verdict = Evaluate("Fahrenheit 451 by Someone Else EPUB");

        Assert.IsFalse(verdict.IsStrictWorkAssertion);
    }

    [TestMethod]
    public void AnEditionTitleAliasCanSupplyTheAssertion()
    {
        // PROVIDER-6's persisted edition-title aliases, carried through here.
        var verdict = ExternalReleaseNameEvidence.Evaluate(
            "Ray Bradbury - Fahrenheit Four Five One (epub)",
            [Title, "Fahrenheit Four Five One"],
            Author);

        Assert.IsTrue(verdict.IsStrictWorkAssertion);
    }

    [TestMethod]
    public void ALeadingArticleDroppedByTheReleaseStillMatches()
    {
        var verdict = ExternalReleaseNameEvidence.Evaluate(
            "Andy Weir - Martian (retail) (epub)", ["The Martian"], "Andy Weir");

        Assert.IsTrue(verdict.IsStrictWorkAssertion);
    }

    [TestMethod]
    public void AnEmptyReleaseNameAssertsNothing()
    {
        var verdict = ExternalReleaseNameEvidence.Evaluate(null, [Title], Author);

        Assert.IsFalse(verdict.IsStrictWorkAssertion);
        Assert.IsFalse(verdict.AssertsExpectedTitle);
    }

    [TestMethod]
    public void NonContiguousTitleWordsAreNotAnAssertion()
    {
        // Contiguity is what keeps "explained tokens" honest: the expected
        // words must appear together, not merely somewhere in the name.
        var verdict = Evaluate("Fahrenheit Rising 451 by Ray Bradbury EPUB");

        Assert.IsFalse(verdict.AssertsExpectedTitle);
    }

    [TestMethod]
    public void ExpectedSeriesNameAndNumberExplainTheirTokensInTheReleaseName()
    {
        var series = new[] { new BookSeries("The Empyrean", "3.5") };

        var verdict = ExternalReleaseNameEvidence.Evaluate(
            "Rebecca.Yarros-The.Empyrean.3.5-Threshing.Day",
            ["Threshing Day"], "Rebecca Yarros", series);
        var withoutSeries = ExternalReleaseNameEvidence.Evaluate(
            "Rebecca.Yarros-The.Empyrean.3.5-Threshing.Day",
            ["Threshing Day"], "Rebecca Yarros");

        Assert.IsTrue(verdict.IsStrictWorkAssertion);
        Assert.IsTrue(withoutSeries.IsStrictWorkAssertion, "A missing catalog series position is not a contradiction.");
    }

    [TestMethod]
    public void ACatalogSeriesNameMissingItsArticleStillExplainsTheReleasesLeadingArticle()
    {
        // The catalog's series record is routinely just the bare series name
        // ("Empyrean"), while a release still carries the branding article
        // ("The.Empyrean"). The leading "The" must not be left unexplained.
        var series = new[] { new BookSeries("Empyrean", "3.5") };

        var verdict = ExternalReleaseNameEvidence.Evaluate(
            "Rebecca.Yarros-The.Empyrean.3.5-Threshing.Day",
            ["Threshing Day"], "Rebecca Yarros", series);

        Assert.IsTrue(
            verdict.IsStrictWorkAssertion,
            $"unexplained: [{string.Join(", ", verdict.UnexplainedTokens)}]");
    }

    /// <summary>AUTOFIND 0d: a release that states its language only through a cue word.</summary>
    [TestMethod]
    [DataRow("Jack Carr The Terminal List Die Abschussliste (ungekürzt)", "de")]
    [DataRow("Jack Carr The Terminal List Die Abschussliste (ungekuerzt)", "de")]
    [DataRow("Jack Carr The Terminal List Hörbuch", "de")]
    [DataRow("Jack Carr The Terminal List Hoerbuch", "de")]
    [DataRow("Jack Carr The Terminal List Hörspiel", "de")]
    [DataRow("Jack Carr The Terminal List livre audio", "fr")]
    [DataRow("Jack Carr The Terminal List lu par Paul Dupont", "fr")]
    [DataRow("Jack Carr The Terminal List intégrale", "fr")]
    public void ALanguageCueWordResolvesTheReleaseLanguage(string release, string language) =>
        Assert.AreEqual(language, ExternalReleaseNameEvidence.Evaluate(
            release, ["The Terminal List"], "Jack Carr").AssertedLanguage);

    [TestMethod]
    [DataRow("Jack Carr The Terminal List Unabridged")]
    [DataRow("Jack Carr The Terminal List read by Ray Porter")]
    [DataRow("Jack Carr The Terminal List Audiobook")]
    public void AReleaseWithoutALanguageCueAssertsNoLanguage(string release) =>
        Assert.IsNull(ExternalReleaseNameEvidence.Evaluate(
            release, ["The Terminal List"], "Jack Carr").AssertedLanguage);

    [TestMethod]
    public void ALanguageNameWinsOverACueWord() =>
        Assert.AreEqual("fr", ExternalReleaseNameEvidence.Evaluate(
            "Jack Carr The Terminal List French Hörbuch", ["The Terminal List"], "Jack Carr").AssertedLanguage);

    [TestMethod]
    public void ACueWordLanguageConflictsWithTheRequestedLanguageAndBlocksTheCandidate()
    {
        var requested = new BookIdentity("The Terminal List", "Jack Carr", []);
        var candidate = new ExternalProviderCandidate(
            "custom-scheme:german-1",
            ExternalProviderWorkEvidence.Empty,
            Release: Release("Jack Carr The Terminal List Die Abschussliste (ungekürzt)"));

        var assessment = DeterministicCandidateIdentityResolver.Assess(requested, candidate);

        Assert.AreEqual("de", assessment.ReleaseEvidence.AssertedLanguage);
        Assert.AreEqual(IdentityEvidenceState.Conflicting, assessment.LanguageEvidence);
        Assert.AreEqual(WorkIdentityDecision.Mismatch, assessment.Decision);
    }

    [TestMethod]
    public void AStructuredEditionLanguageStillWinsOverACueWord()
    {
        var requested = new BookIdentity("The Terminal List", "Jack Carr", []);
        var candidate = new ExternalProviderCandidate(
            "custom-scheme:english-1",
            ExternalProviderWorkEvidence.Empty,
            new ExternalProviderEditionEvidence("en", null, null, []),
            Release("Jack Carr The Terminal List Hörbuch"));

        Assert.AreNotEqual(IdentityEvidenceState.Conflicting,
            DeterministicCandidateIdentityResolver.Assess(requested, candidate).LanguageEvidence);
    }

    private static ExternalProviderReleaseEvidence Release(string name) =>
        new(name, null, null, null, null, null, null, null, [], null);

    [TestMethod]
    public void AReleaseOfADifferentSeriesVolumeStaysUnexplained()
    {
        var series = new[] { new BookSeries("The Empyrean", "3.5") };

        var verdict = ExternalReleaseNameEvidence.Evaluate(
            "Rebecca.Yarros-The.Empyrean.3-Threshing.Day",
            ["Threshing Day"], "Rebecca Yarros", series);

        Assert.IsFalse(verdict.IsStrictWorkAssertion);
    }
}
