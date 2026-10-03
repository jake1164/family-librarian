using FamilyLibrarian.Application.Providers;

namespace FamilyLibrarian.Infrastructure.Tests.Providers;

/// <summary>
/// Every case here is a real release name captured from a live Prowlarr
/// <c>/search</c> response for "Fahrenheit 451" by Ray Bradbury on
/// <c>toontown-int-srv2</c> (PROVIDER-7). The acceptances and the refusals are
/// equally load-bearing: four of these names contain both the expected title
/// and the expected author and are still not the requested work.
/// </summary>
[TestClass]
public sealed class ExternalReleaseNameEvidenceTests
{
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
        Assert.IsNotNull(verdict.RejectionReason);
    }

    [TestMethod]
    public void AnUnexplainedBroadcasterTagIsRefusedRatherThanTreatedAsNoise()
    {
        // "(BBC)" is a parenthesised annotation, but parentheses earn no pass:
        // a BBC production of Fahrenheit 451 is a dramatization, not the book.
        var verdict = Evaluate("Ray Bradbury - Fahrenheit 451 (BBC)");

        Assert.IsFalse(verdict.IsStrictWorkAssertion);
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
    public void AMissingAuthorKeepsTheAssertionShortOfStrict()
    {
        // Same-title/missing-author is ambiguous, not a match -- the same rule
        // the deterministic matcher applies to structured metadata.
        var verdict = ExternalReleaseNameEvidence.Evaluate("Fahrenheit 451 EPUB", [Title], Author);

        Assert.IsTrue(verdict.AssertsExpectedTitle);
        Assert.IsFalse(verdict.AssertsExpectedAuthor);
        Assert.IsFalse(verdict.IsStrictWorkAssertion);
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
        Assert.IsFalse(withoutSeries.IsStrictWorkAssertion);
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
