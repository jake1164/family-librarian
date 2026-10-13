using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Evidence;
using FamilyLibrarian.Domain.Requests;
using static FamilyLibrarian.Infrastructure.Tests.Characterization.CharacterizationFixtures;

namespace FamilyLibrarian.Infrastructure.Tests.Security;

[TestClass]
public sealed class ArtifactProfileComparerTests
{
    internal static ExpectedArtifactProfile Expected(int? pages = null, int? seconds = null) =>
        new("Iron Flame", [], "Rebecca Yarros", [new BookSeries("The Empyrean", "2")], ["9780000000011"], "en", pages, seconds, false);

    [TestMethod]
    [DataRow(6000, 320, "profile.compare.length_far_below_expected", true)]
    [DataRow(22000, 90, null, false)]
    [DataRow(9000, 60, "profile.compare.length_below_expected", false)]
    [DataRow(19999, 320, "profile.compare.length_far_below_expected", true)]
    [DataRow(20000, 320, "profile.compare.length_below_expected", false)]
    [DataRow(48000, 320, null, false)]
    [DataRow(0, 79, "profile.compare.length_below_expected", false)]
    [DataRow(0, 80, "profile.compare.length_far_below_expected", true)]
    public void TextLengthUsesCatalogPagesAndTheNovellaGuard(int words, int pages, string? code, bool held)
    {
        var raw = new IdentityFact("ev-words", EvidenceDimension.Profile, EvidenceReasonCodes.ArtifactProfile.TextWordCount,
            EvidencePolarity.Unknown, $"words={words};items=3;basis=xhtml_text");
        var comparisons = ArtifactProfileComparer.Compare([raw], Expected(pages));
        Assert.AreEqual(code, comparisons.SingleOrDefault()?.Code);
        var result = ArtifactHoldPolicy.Apply(Asset(RequestMediaType.Ebook, ".epub"), AssetIdentityVerificationResult.Match("epub", comparisons));
        Assert.AreEqual(held, !result.IsMatch);
        if (code is not null && !held) Assert.AreEqual(EvidencePolarity.Weak, comparisons.Single().Polarity);
    }

    [TestMethod]
    [DataRow(14000, "profile.compare.duration_far_below_expected", true)]
    [DataRow(18000, "profile.compare.duration_below_expected", false)]
    [DataRow(50399, "profile.compare.duration_below_expected", false)]
    [DataRow(50400, null, false)]
    public void DurationUsesTheSameStrictQuarterThreshold(int seconds, string? code, bool held)
    {
        var facts = ArtifactProfileComparer.Compare([AudioDurations.TotalDuration(seconds, 1)], Expected(seconds: 72000));
        Assert.AreEqual(code, facts.SingleOrDefault()?.Code);
        Assert.AreEqual(held, !ArtifactHoldPolicy.Apply(Asset(RequestMediaType.Audiobook, ".m4b"),
            AssetIdentityVerificationResult.Match("m4b", facts)).IsMatch);
    }

    [TestMethod]
    public void MissingCatalogLengthAndUnavailableMeasurementsNeverCreateLengthFindings()
    {
        Assert.IsEmpty(ArtifactProfileComparer.Compare([AudioDurations.TotalDuration(14000, 1)], Expected()));
        Assert.IsEmpty(ArtifactProfileComparer.Compare([new IdentityFact("unknown", EvidenceDimension.Profile,
            EvidenceReasonCodes.ArtifactProfile.TextWordCountUnavailable, EvidencePolarity.Unknown, "CJK")], Expected(320)));
    }

    [TestMethod]
    [DataRow("The Empyrean#3", "profile.compare.series_position_differs", EvidencePolarity.Negative)]
    [DataRow("THE EMPYREAN#02", "profile.compare.series_position_agrees", EvidencePolarity.Positive)]
    [DataRow("Other Series#3", null, EvidencePolarity.Unknown)]
    [DataRow("The Empyrean", null, EvidencePolarity.Unknown)]
    public void SeriesNeedsAComparableNameAndPosition(string series, string? code, EvidencePolarity polarity)
    {
        var facts = ArtifactProfileComparer.Compare([new IdentityFact("series", EvidenceDimension.Profile,
            EvidenceReasonCodes.ArtifactProfile.PackageSeries, EvidencePolarity.Unknown, series)], Expected());
        Assert.AreEqual(code, facts.SingleOrDefault()?.Code);
        if (code is not null) Assert.AreEqual(polarity, facts.Single().Polarity);
    }

    [TestMethod]
    [DataRow("abridged", true)]
    [DataRow("gekürzt", true)]
    [DataRow("abrégé", true)]
    [DataRow("unabridged", false)]
    [DataRow("ungekürzt", false)]
    [DataRow("abridgedness", false)]
    public void AbridgementRequiresAnExactClosedListWord(string text, bool marked) =>
        Assert.AreEqual(marked, ArtifactProfileComparer.FindAbridgedMarker(text) is not null);

    [TestMethod]
    [DataRow(3, 5, true)]
    [DataRow(2, 5, false)]
    [DataRow(4, 4, false)]
    public void HeadingLanguageIsWeakAndNeedsBothThresholds(int german, int total, bool finding)
    {
        var raw = Enumerable.Range(0, total).Select(i => new IdentityFact($"h{i}", EvidenceDimension.Profile,
            EvidenceReasonCodes.ArtifactProfile.Heading, EvidencePolarity.Unknown, i < german ? "Kapitel ungekürzt" : "Chapter one")).ToArray();
        var facts = ArtifactProfileComparer.Compare(raw, Expected());
        Assert.AreEqual(finding, facts.Any(f => f.Code == EvidenceReasonCodes.ProfileComparison.HeadingsOtherLanguage));
        Assert.IsTrue(ArtifactHoldPolicy.Apply(Asset(RequestMediaType.Ebook, ".epub"), AssetIdentityVerificationResult.Match("epub", facts)).IsMatch);
    }

    [TestMethod]
    [DataRow("Rebecca Yarros", "Rebeca Yaross", true)]
    [DataRow("Yarros, Rebecca (Author)", "Rebeca Yaross", true)]
    [DataRow("Rebecca Yarros", "Xebecca Yarros", false)]
    [DataRow("Rebecca Yarros", "Rebecca", false)]
    [DataRow("Rebecca Yarros", "Robert Young", false)]
    public void AuthorSpellingIsBoundedPerComponent(string expected, string observed, bool variant) =>
        Assert.AreEqual(variant, AuthorSpelling.IsVariant(expected, observed));

    [TestMethod]
    [DataRow("Iron Flame (Unabridged)", "Iron Flame", false, false)]
    [DataRow("Iron Flame [ABRIDGED]", "Iron Flame", true, false)]
    [DataRow("Iron Flame - Ungekürzt", "Iron Flame", false, true)]
    [DataRow("Iron Flame (GEKÜRZT)", "Iron Flame", true, true)]
    [DataRow("Iron Flame (Unabridged) [Abridged]", "Iron Flame (Unabridged)", true, false)]
    [DataRow("Iron Flame - Unabridged extra", "Iron Flame - Unabridged extra", false, false)]
    [DataRow("Unabridged Iron Flame", "Unabridged Iron Flame", false, false)]
    public void AudioStripsOneTerminalMarkerOnly(string title, string core, bool abridged, bool german)
    {
        var marker = AudioEditionMarker.Strip(title);
        Assert.AreEqual(core, marker.CoreTitle);
        Assert.AreEqual(abridged, marker.IsAbridged);
        Assert.AreEqual(german, marker.IsGerman);
    }
}
