using FamilyLibrarian.Application.Upgrades;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Evidence;

namespace FamilyLibrarian.Infrastructure.Tests.Acquisition;

/// <summary>A6-UPGRADE-1 U-2: which delivered copies are worth looking to upgrade, and which are never touched.</summary>
[TestClass]
public sealed class DeficientCopyDetectorTests
{
    private static DeliveredEvidenceFact Fact(EvidenceDimension dimension, string code, EvidencePolarity polarity) => new(dimension, code, polarity);

    private static DeficiencyAssessment Assess(AssetOrigin origin, bool overridden, params DeliveredEvidenceFact[] facts) =>
        DeficientCopyDetector.Assess(new DeliveredCopyFacts(origin, overridden, facts));

    [TestMethod]
    [DataRow(EvidenceReasonCodes.ArtifactCompleteness.TocTargetsMissing, EvidenceDimension.Completeness)]
    [DataRow(EvidenceReasonCodes.ArtifactCompleteness.DuplicateReadingOrderItems, EvidenceDimension.Completeness)]
    [DataRow(EvidenceReasonCodes.ArtifactCompleteness.SpineContentMissing, EvidenceDimension.Completeness)]
    [DataRow(EvidenceReasonCodes.ArtifactCompleteness.SampleMarkerInMetadata, EvidenceDimension.Completeness)]
    [DataRow(EvidenceReasonCodes.ArtifactAudio.TrackSetGap, EvidenceDimension.AudioTechnical)]
    [DataRow(EvidenceReasonCodes.ArtifactAudio.TrackSetDuplicate, EvidenceDimension.AudioTechnical)]
    public void AMeasuredSignOfIncompletenessMakesACopyWorthUpgrading(string code, EvidenceDimension dimension)
    {
        var result = Assess(AssetOrigin.ExternalProvider, false, Fact(dimension, code, EvidencePolarity.Condition));

        Assert.IsTrue(result.Deficient);
        Assert.IsTrue(result.EligibleForUpgrade);
        CollectionAssert.AreEqual(new[] { code }, result.Reasons.ToArray());
    }

    [TestMethod]
    public void NoCueOrInformationalFindingsAreNeverAReason()
    {
        var result = Assess(AssetOrigin.ExternalProvider, false,
            Fact(EvidenceDimension.Completeness, EvidenceReasonCodes.ArtifactCompleteness.NoCueDetected, EvidencePolarity.Unknown),
            Fact(EvidenceDimension.Completeness, EvidenceReasonCodes.ArtifactCompleteness.ReadingOrderCounted, EvidencePolarity.Unknown),
            Fact(EvidenceDimension.Completeness, EvidenceReasonCodes.ArtifactCompleteness.NotAssessed, EvidencePolarity.Unknown),
            Fact(EvidenceDimension.AudioTechnical, EvidenceReasonCodes.ArtifactAudio.DurationMeasured, EvidencePolarity.Unknown),
            Fact(EvidenceDimension.Structure, EvidenceReasonCodes.ArtifactStructure.NoDefectReported, EvidencePolarity.Positive));

        Assert.IsFalse(result.Deficient, "Absence of a cue is not a defect: only something the checks measured counts.");
        Assert.IsFalse(result.EligibleForUpgrade);
        Assert.IsEmpty(result.Reasons);
    }

    [TestMethod]
    public void AReasonCodeWithAPositivePolarityDoesNotCount()
    {
        var result = Assess(AssetOrigin.ExternalProvider, false,
            Fact(EvidenceDimension.Completeness, EvidenceReasonCodes.ArtifactCompleteness.TocTargetsMissing, EvidencePolarity.Positive));

        Assert.IsFalse(result.Deficient);
    }

    [TestMethod]
    public void ACopyAPersonChoseIsNeverEligibleEvenWhenItLooksIncomplete()
    {
        var sign = Fact(EvidenceDimension.Completeness, EvidenceReasonCodes.ArtifactCompleteness.TocTargetsMissing, EvidencePolarity.Condition);

        var released = Assess(AssetOrigin.ExternalProvider, overridden: true, sign);
        var uploaded = Assess(AssetOrigin.ManualUpload, overridden: false, sign);

        foreach (var result in new[] { released, uploaded })
        {
            Assert.IsTrue(result.Deficient, "It is still described honestly as deficient.");
            Assert.IsTrue(result.ProtectedFromReplacement);
            Assert.IsFalse(result.EligibleForUpgrade, "A human decision is never undone automatically.");
        }
    }

    [TestMethod]
    [DataRow(AssetOrigin.ExternalProvider)]
    [DataRow(AssetOrigin.BuiltInProvider)]
    [DataRow(AssetOrigin.Legacy)]
    [DataRow(AssetOrigin.Unknown)]
    public void AutomaticallyAcquiredCopiesAreEligible(AssetOrigin origin) =>
        Assert.IsTrue(Assess(origin, false,
            Fact(EvidenceDimension.Completeness, EvidenceReasonCodes.ArtifactCompleteness.TocTargetsMissing, EvidencePolarity.Condition)).EligibleForUpgrade);

    [TestMethod]
    public void ReasonsAreDistinctAndOrderedSoTheResultIsStable()
    {
        var a = Fact(EvidenceDimension.Completeness, EvidenceReasonCodes.ArtifactCompleteness.TocTargetsMissing, EvidencePolarity.Condition);
        var b = Fact(EvidenceDimension.AudioTechnical, EvidenceReasonCodes.ArtifactAudio.TrackSetGap, EvidencePolarity.Condition);

        var result = Assess(AssetOrigin.ExternalProvider, false, b, a, a);

        CollectionAssert.AreEqual(new[] { a.Code, b.Code }.OrderBy(code => code, StringComparer.Ordinal).ToArray(), result.Reasons.ToArray());
    }
}
