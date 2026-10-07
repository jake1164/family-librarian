using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Infrastructure.Tests.Providers;

[TestClass]
public sealed class CandidateIdentityAssessmentTests
{
    private static readonly BookIdentity Threshing = new("Threshing Day", "Rebecca Yarros", []);
    private static readonly BookIdentity Onyx = new("Onyx Storm", "Rebecca Yarros", [], Series: [new BookSeries("The Empyrean", "3")]);
    private static ExternalProviderCandidate Release(string name, string reference = "candidate") => new(reference,
        ExternalProviderWorkEvidence.Empty, Release: new(name, "m4b", 550_100_000, false, 1, false, null, null, [], null));
    private static CandidateIdentityAssessment Assess(string name) => DeterministicCandidateIdentityResolver.Assess(Threshing, Release(name));

    [TestMethod]
    [DataRow("Rebecca.Yarros-The.Empyrean.3.5-Threshing.Day")]
    [DataRow("Rebecca Yarros - Threshing Day - Fantasy Romance - M4B")]
    [DataRow("Threshing Day by Rebecca Yarros")]
    [DataRow("Threshing.Day.m4b")]
    [DataRow("Threshing Day - Fantasy Romance")]
    [DataRow("Threshing.Day.Publisher.Ultimate.Cut.web.128kbps.m4b")]
    public void ExactTitlePhraseSurvivesMetadataAndArbitraryDescriptors(string name)
    {
        var assessment = Assess(name);
        Assert.AreEqual(WorkIdentityDecision.Match, assessment.Decision);
        Assert.AreEqual(IdentityEvidenceState.Exact, assessment.TitleEvidence.State);
        Assert.HasCount(0, assessment.Contradictions);
        Assert.AreEqual(name, assessment.ReleaseEvidence.RawReleaseTitle);
    }

    [TestMethod]
    [DataRow("The Threshing Floor by Steph Nelson")]
    [DataRow("The Threshing Circle by Neil Grimmett")]
    [DataRow("The Threshing by Tim Grahl")]
    [DataRow("Threshing Day by David Yarros")]
    [DataRow("Threshing Day by Rebecca Ross")]
    public void ExplicitContradictionsAreMismatch(string name)
    {
        var assessment = Assess(name);
        Assert.AreEqual(WorkIdentityDecision.Mismatch, assessment.Decision);
        Assert.AreEqual(AuthorAffinityKind.Conflict, assessment.AuthorEvidence.Kind);
        Assert.IsTrue(assessment.Contradictions.Count > 0);
    }

    [TestMethod]
    public void ResidualDescriptorsAreNotMisreadAsAnAuthor()
    {
        var assessment = Assess("Threshing.Day.fantasy.romance.m4b");
        Assert.AreEqual(AuthorAffinityKind.Unknown, assessment.AuthorEvidence.Kind);
        CollectionAssert.AreEqual(new[] { "FANTASY", "ROMANCE" }, assessment.ReleaseEvidence.UnexplainedTokens.ToArray());
        Assert.AreEqual(WorkIdentityDecision.Match, assessment.Decision);
    }

    [TestMethod]
    [DataRow("Rebecca Yarros", AuthorAffinityKind.Exact)]
    [DataRow("Yarros, Rebecca", AuthorAffinityKind.Exact)]
    [DataRow("R Yarros", AuthorAffinityKind.Compatible)]
    [DataRow("R. Yarros", AuthorAffinityKind.Compatible)]
    [DataRow("Yarros", AuthorAffinityKind.LastOnly)]
    [DataRow("Rebecca", AuthorAffinityKind.FirstOnly)]
    [DataRow("Rececca Yarros", AuthorAffinityKind.LastExactFirstFuzzy)]
    [DataRow("Rebecca Yaros", AuthorAffinityKind.FirstExactLastFuzzy)]
    public void AuthorVariantsRemainSupportingEvidence(string author, AuthorAffinityKind kind)
    {
        var assessment = Assess($"Threshing Day by {author}");
        Assert.AreEqual(WorkIdentityDecision.Match, assessment.Decision);
        Assert.AreEqual(kind, assessment.AuthorEvidence.Kind);
    }

    [TestMethod]
    public void RequestedUnknownSeriesPositionDoesNotConflictWithReleaseDecimal()
    {
        var requested = Threshing with { Series = [new("The Empyrean", null)] };
        var assessment = DeterministicCandidateIdentityResolver.Assess(requested, Release("Rebecca.Yarros-The.Empyrean.3.5-Threshing.Day"));
        Assert.AreEqual(WorkIdentityDecision.Match, assessment.Decision);
        Assert.AreEqual(IdentityEvidenceState.Unknown, assessment.SeriesEvidence.Single().State);
        Assert.AreEqual("3.5", assessment.SeriesEvidence.Single().ObservedPosition);
        var noSeries = Assess("Rebecca.Yarros-The.Empyrean.3.5-Threshing.Day");
        Assert.AreEqual("3.5", noSeries.SeriesEvidence.Single().ObservedPosition);
    }

    [TestMethod]
    [DataRow("3.5", WorkIdentityDecision.Match)]
    [DataRow("4", WorkIdentityDecision.Mismatch)]
    public void ComparableSeriesPositionsProduceExplicitEvidence(string expected, WorkIdentityDecision decision)
    {
        var requested = Threshing with { Series = [new("The Empyrean", expected)] };
        var assessment = DeterministicCandidateIdentityResolver.Assess(requested, Release("Rebecca.Yarros-The.Empyrean.3.5-Threshing.Day"));
        Assert.AreEqual(decision, assessment.Decision);
    }

    [TestMethod]
    public void StructuredSeriesComparisonRequiresTheSameSeriesAndBothPositions()
    {
        var requested = Threshing with { Series = [new("The Empyrean", "4")] };
        var source = Release("Threshing Day") with { Work = ExternalProviderWorkEvidence.Empty with { Series = [new("Other series", "3.5")] } };
        Assert.AreEqual(WorkIdentityDecision.Match, DeterministicCandidateIdentityResolver.Assess(requested, source).Decision);
        source = source with { Work = source.Work with { Series = [new("Empyrean", "3.5")] } };
        Assert.AreEqual(WorkIdentityDecision.Mismatch, DeterministicCandidateIdentityResolver.Assess(requested, source).Decision);
    }

    [TestMethod]
    [DataRow("Fourth.Wng.by.Rebecca.Yarros", WorkIdentityDecision.Match)]
    [DataRow("Fourth.Wng.Fantasy.Romance.M4B", WorkIdentityDecision.Ambiguous)]
    [DataRow("Fourth.Wrong.by.Rebecca.Yarros", WorkIdentityDecision.Ambiguous)]
    public void FuzzyTitleUsesOneLocalEditAndRequiresSupportingAuthor(string name, WorkIdentityDecision decision)
    {
        var assessment = DeterministicCandidateIdentityResolver.Assess(new("Fourth Wing", "Rebecca Yarros", []), Release(name));
        Assert.AreEqual(decision, assessment.Decision);
        if (decision == WorkIdentityDecision.Match)
        {
            Assert.AreEqual("FOURTH WNG", assessment.TitleEvidence.Observed);
            Assert.AreEqual(IdentityEvidenceState.Fuzzy, assessment.TitleEvidence.State);
        }
    }

    [TestMethod]
    [DataRow("It", "The Institute")]
    [DataRow("It", "Little Women")]
    [DataRow("It", "Bit")]
    [DataRow("Dune", "Dune Messiah")]
    [DataRow("Day", "May")]
    public void ShortTitlesNeverUseSubstringOrFuzzyAccidents(string expected, string candidate)
    {
        var assessment = DeterministicCandidateIdentityResolver.Assess(new(expected, "Rebecca Yarros", []),
            ExternalProviderCandidate.FromSimple("wrong", candidate, "Rebecca Yarros", "m4b", 1000));
        Assert.IsTrue(assessment.Decision is WorkIdentityDecision.Mismatch or WorkIdentityDecision.Ambiguous);
    }

    [TestMethod]
    public void VeryShortExactTitlesRequireIndependentAuthorEvidence()
    {
        var identity = new BookIdentity("It", "Stephen King", []);
        Assert.AreEqual(WorkIdentityDecision.Ambiguous, DeterministicCandidateIdentityResolver.Assess(identity, Release("It.m4b")).Decision);
        Assert.AreEqual(WorkIdentityDecision.Match, DeterministicCandidateIdentityResolver.Assess(identity, Release("It.by.Stephen.King.m4b")).Decision);
    }

    [TestMethod]
    public void SignificantTitleTokensKeepOrderingAndOnlyVaryGrammar()
    {
        Assert.AreEqual(IdentityEvidenceState.Compatible, ReleaseTitleMatcher.Evaluate("Debt of Honor", ["DEBT", "HONOR"]).State);
        Assert.AreEqual(IdentityEvidenceState.Unknown, ReleaseTitleMatcher.Evaluate("Debt of Honor", ["HONOR", "DEBT"]).State);
        Assert.AreEqual(IdentityEvidenceState.Unknown, ReleaseTitleMatcher.Evaluate("Debt of Honor", ["DEBT", "WRONG", "HONOR"]).State);
    }

    [TestMethod]
    public void NormalizationKeepsBoundariesUnicodeAndRawEvidence()
    {
        var raw = "Ｒｅｂｅｃｃａ.Yarros—The.Empyrean.3.5-Threshing.Day";
        var assessment = Assess(raw);
        Assert.AreEqual(WorkIdentityDecision.Match, assessment.Decision);
        Assert.AreEqual(raw, assessment.ReleaseEvidence.RawReleaseTitle);
        Assert.Contains("EMPYREAN", assessment.ReleaseEvidence.Tokens!);
        Assert.Contains("THRESHING", assessment.ReleaseEvidence.Tokens!);
        Assert.IsFalse(assessment.ReleaseEvidence.NormalizedRelease!.Contains("EMPYREANTHRESHING", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("GraphicAudio Part 2")]
    [DataRow("Box Set")]
    [DataRow("Omnibus")]
    [DataRow("Abridged")]
    [DataRow("Dramatized Full Cast")]
    [DataRow("Books 1-5")]
    public void StructuralQualifiersKeepIdentityButBlockAutomaticIndividualAcquisition(string qualifier)
    {
        var requested = new BookIdentity("Fourth Wing", "Rebecca Yarros", []);
        var source = Release($"Fourth Wing by Rebecca Yarros [{qualifier}]");
        var assessment = DeterministicCandidateIdentityResolver.Assess(requested, source);
        Assert.AreEqual(WorkIdentityDecision.MatchWithConditions, assessment.Decision);
        Assert.HasCount(0, assessment.Contradictions);
        Assert.IsTrue(ExternalReleasePolicy.Evaluate(source.Release, RequestMediaType.Audiobook, assessment.ReleaseEvidence.Part, assessment).RequiresConfirmation);
    }

    [TestMethod]
    public void UnabridgedIsPositivePackagingAndNeverMatchesAbridged()
    {
        var assessment = Assess("Threshing.Day.by.Rebecca.Yarros.Unabridged.m4b");
        Assert.AreEqual(WorkIdentityDecision.Match, assessment.Decision);
        Assert.HasCount(0, assessment.Conditions);
    }

    [TestMethod]
    public void StructuredWrongTitleCannotBeOverriddenByRawExactTitleOrIdentifier()
    {
        var source = Release("Threshing Day by Rebecca Yarros") with
        { Work = new("The Threshing Floor", null, [new("Rebecca Yarros", "author")], [], [new("isbn13", "9781649374042")]) };
        Assert.AreEqual(WorkIdentityDecision.Mismatch, DeterministicCandidateIdentityResolver.Assess(Threshing, source).Decision);
    }

    [TestMethod]
    public void NarratorCannotBecomePrimaryAuthorEvidence()
    {
        var source = Release("Threshing Day") with
        { Work = ExternalProviderWorkEvidence.Empty with { Authors = [new("Stephen King", "narrator")] } };
        var assessment = DeterministicCandidateIdentityResolver.Assess(Threshing, source);
        Assert.AreEqual(AuthorAffinityKind.Unknown, assessment.AuthorEvidence.Kind);
        Assert.AreEqual(WorkIdentityDecision.Match, assessment.Decision);
    }

    [TestMethod]
    public void MissingTitleAndUnknownMetadataRemainAmbiguous()
    {
        var assessment = Assess("Unidentified.Record.m4b");
        Assert.AreEqual(WorkIdentityDecision.Ambiguous, assessment.Decision);
        Assert.AreEqual(IdentityEvidenceState.Unknown, assessment.TitleEvidence.State);
        Assert.AreEqual(AuthorAffinityKind.Unknown, assessment.AuthorEvidence.Kind);
        Assert.HasCount(0, assessment.Contradictions);
    }

    [TestMethod]
    public void ExplicitForeignLanguageInBracketsCannotDisappear()
    {
        var assessment = Assess("Threshing.Day.by.Rebecca.Yarros.[Spanish].m4b");
        Assert.AreEqual(WorkIdentityDecision.Mismatch, assessment.Decision);
        Assert.AreEqual(IdentityEvidenceState.Conflicting, assessment.LanguageEvidence);
    }

    [TestMethod]
    public void MultipartMembersCanOmitAuthorFormatAndGenreMetadata()
    {
        var one = Option("The.Empyrean.[03].Onyx.Storm.1.of.2.by.Rebecca.Yaros.fantasy.romance.m4b", "one");
        var two = Option("The.Empyrean.[03].Onyx.Storm.2.of.2", "two");
        var set = AudiobookPartSetSelector.TrySelect([two, one]);
        Assert.IsNotNull(set);
        Assert.AreEqual(2, set.Total);
        Assert.AreEqual("one,two", string.Join(',', set.MemberResultIds));
        Assert.AreEqual(AuthorAffinityKind.Unknown, two.AuthorAffinity!.Kind);
        Assert.IsNull(AudiobookPartSetSelector.TrySelect([two]));
        var assessment = ExternalAudiobookPartSetAssessment.For(two, [two]);
        CollectionAssert.AreEqual(new[] { 1 }, assessment.MissingParts.ToArray());
        Assert.AreEqual(WorkIdentityDecision.MatchWithConditions, two.IdentityAssessment!.Decision);
    }

    [TestMethod]
    [DataRow("Different.Series.[03].Onyx.Storm.2.of.2")]
    [DataRow("The.Empyrean.[04].Onyx.Storm.2.of.2")]
    [DataRow("The.Empyrean.[03].Onyx.Storm.2.of.3")]
    [DataRow("The.Empyrean.[03].Onyx.Storm.2.of.2.GraphicAudio")]
    public void DifferentReleaseBasesPositionsTotalsAndEditionsCannotFormASet(string second)
    {
        var one = Option("The.Empyrean.[03].Onyx.Storm.1.of.2.by.Rebecca.Yaros.m4b", "one");
        var two = Option(second, "two");
        Assert.IsNull(AudiobookPartSetSelector.TrySelect([one, two]));
    }

    [TestMethod]
    public void SetRejectsDuplicateNumbersAndMixedProviders()
    {
        var one = Option("The.Empyrean.[03].Onyx.Storm.1.of.2", "one");
        var two = Option("The.Empyrean.[03].Onyx.Storm.2.of.2", "two");
        Assert.IsNull(AudiobookPartSetSelector.TrySelect([one, two, two with { ProviderResultId = "duplicate" }]));
        Assert.IsNull(AudiobookPartSetSelector.TrySelect([one, two with { ProviderId = "other-source" }]));
    }

    private static FulfillmentOption Option(string name, string reference)
    {
        var assessment = DeterministicCandidateIdentityResolver.Assess(Onyx, Release(name, reference));
        var part = assessment.ReleaseEvidence.Part;
        return new("example-source", reference, Guid.Empty, null, RequestMediaType.Audiobook,
            OptionKind.DirectAcquisition, AcquisitionMethod.DirectDownload, assessment.ReleaseEvidence.AssertedFormat, null, null, null,
            0m, null, null, "none", null, null,
            MatchBasis: assessment.Decision == WorkIdentityDecision.Mismatch ? null : BookMatchBasis.StrictTitle,
            AuthorAffinity: assessment.AuthorEvidence, HasPlausibleTitle: assessment.TitleEvidence.IsPositive,
            ReleaseName: name, AudiobookPart: part, RequiresReleaseConfirmation: true,
            FragmentOnlyConcern: part?.Total > 1 && assessment.Conditions.All(condition => condition.Kind == ReleaseConditionKind.CompanionParts),
            IdentityAssessment: assessment);
    }
}
