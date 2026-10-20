using System.Reflection;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Evidence;
using FamilyLibrarian.Application.SemanticAssessment;
using static FamilyLibrarian.Domain.Evidence.EvidenceReasonCodes;

namespace FamilyLibrarian.Infrastructure.Tests.SemanticAssessment;

[TestClass]
public sealed class ModelReleaseTests
{
    // Test every catalogued code with Negative polarity, even codes normally emitted with other polarities.
    // This includes future constants automatically and proves fail-closed behavior for every dimension.
    public static IEnumerable<object[]> EveryCode() => Codes(typeof(EvidenceReasonCodes)).Select(c => new object[] { c });
    private static IEnumerable<string> Codes(Type type) => type.GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!)
        .Concat(type.GetNestedTypes().SelectMany(Codes));

    internal static IdentityFact[] AuthorFacts =>
    [
        new("ev-title", EvidenceDimension.IdentityCorroboration, ArtifactIdentityCorroboration.TitleAgrees, EvidencePolarity.Positive),
        new("ev-author", EvidenceDimension.IdentityCorroboration, ArtifactIdentityCorroboration.AuthorDiffers, EvidencePolarity.Negative),
        new("ev-language", EvidenceDimension.Language, ArtifactLanguage.DeclaredMatchesRequest, EvidencePolarity.Positive),
        new("ev-series", EvidenceDimension.Profile, ProfileComparison.SeriesPositionAgrees, EvidencePolarity.Positive)
    ];

    internal static MediaAsset Held(string code = IdentityHold.ContentContradicted, string format = ".epub")
    {
        var asset = ArtifactDecisionKit.Asset(format: format);
        asset.TransitionStorageState(MediaAssetStorageState.Unmatched, DateTimeOffset.UtcNow);
        asset.SetIdentityHoldCode(code);
        asset.SetIdentityMismatchReason("Held for review.");
        return asset;
    }

    [TestMethod]
    public void EvidenceCatalogChangesRequireReleaseClassificationReview()
    {
        Assert.HasCount(146, EveryCode().ToArray(),
            "A reason code was added or removed. Review its polarity and release classification, then update this reviewed inventory count.");
    }

    [TestMethod]
    public void ConditionsThatWouldIndependentlyHoldNeverRideAlongWithTheAuthorException()
    {
        Assert.IsFalse(ModelReleasePolicy.IsEligible(Held(), [.. AuthorFacts,
            new("sample", EvidenceDimension.Completeness, ArtifactCompleteness.SampleMarkerInMetadata, EvidencePolarity.Condition)]));
        Assert.IsFalse(ModelReleasePolicy.IsEligible(Held(), [.. AuthorFacts,
            new("abridged", EvidenceDimension.Profile, ProfileComparison.AbridgedMarker, EvidencePolarity.Condition)]));
    }

    [TestMethod]
    [DynamicData(nameof(EveryCode))]
    public void EveryNegativeCodeBlocksExceptTheExactAuthorException(string code)
    {
        var facts = AuthorFacts.Where(f => f.Polarity != EvidencePolarity.Negative)
            .Append(new IdentityFact("negative", EvidenceDimension.IdentityCorroboration, code, EvidencePolarity.Negative)).ToArray();
        Assert.AreEqual(code == ArtifactIdentityCorroboration.AuthorDiffers,
            ModelReleasePolicy.IsEligible(Held(), facts), code);
        Assert.IsFalse(ModelReleasePolicy.IsEligible(Held(IdentityHold.AttestedOnlyExternal), facts), code);
    }

    [TestMethod]
    [DynamicData(nameof(EveryCode))]
    public void CollectionExceptionAllowsOnlyTheTitleDifferenceAcrossTheEntireCatalog(string code)
    {
        IdentityFact[] facts = [
            new("ev-negative", EvidenceDimension.IdentityCorroboration, code, EvidencePolarity.Negative),
            new("ev-consent", EvidenceDimension.IdentityAttestation, ArtifactIdentityAttestation.CollectionConsentRecorded, EvidencePolarity.Condition),
            new("ev-heading", EvidenceDimension.Profile, ArtifactProfile.Heading, EvidencePolarity.Unknown, "Cinder Gate: a story")];
        Assert.AreEqual(code == ArtifactIdentityCorroboration.TitleDiffers,
            ModelReleasePolicy.IsEligible(Held(IdentityHold.CollectionMissingRequestedWork), facts), code);
    }

    [TestMethod]
    public void AuthorExceptionNeedsAllThreeIndependentAgreements()
    {
        Assert.IsTrue(ModelReleasePolicy.IsEligible(Held(), AuthorFacts));
        foreach (var id in new[] { "ev-title", "ev-language", "ev-series" })
            Assert.IsFalse(ModelReleasePolicy.IsEligible(Held(), AuthorFacts.Where(f => f.FindingId != id).ToArray()), id);
    }

    [TestMethod]
    public void NoNegativeAttestedAndInconclusiveHoldsAreEligible()
    {
        Assert.IsTrue(ModelReleasePolicy.IsEligible(Held(IdentityHold.AttestedOnlyExternal), []));
        Assert.IsTrue(ModelReleasePolicy.IsEligible(Held(IdentityHold.ContentInconclusive), []));
    }

    [TestMethod]
    public void ModelConcernCanNeverBeReleasedEvenWithExceptionFacts()
    {
        Assert.IsFalse(ModelReleasePolicy.IsEligible(Held(IdentityHold.ModelConcern), AuthorFacts));
        Assert.IsFalse(ModelReleasePolicy.IsEligible(Held(IdentityHold.ModelConcern), []));
    }

    [TestMethod]
    [DataRow("idv.ev-title,idv.ev-language,idv.ev-series", true)]
    [DataRow("idv.ev-title", false)]
    [DataRow("idv.ev-language,idv.ev-series", false)]
    [DataRow("forged", false)]
    public void AuthorReleaseNeedsCitedTitleLanguageAndIndependentAgreement(string ids, bool valid)
    {
        var asset = Held();
        var input = ArtifactDecisionEvidence.Render(asset, AuthorFacts, ArtifactDecisionKit.Expected, [], true);
        var reply = System.Text.Json.JsonSerializer.Serialize(new { schema = "artifact-decision/1", verdict = "release",
            claims = new[] { new { kind = "supports_declared_identity", evidence_ids = ids.Split(','), note = "The byline is a spelling variant." } } });
        Assert.AreEqual(valid, SemanticAssessmentParser.ParseArtifactRelease(reply, asset, AuthorFacts, input).IsValid);
        Assert.IsFalse(SemanticAssessmentParser.ParseArtifactDecision(reply, input.Findings).IsValid);
    }

    [TestMethod]
    public void AttestedMp3CannotBeReleasedUsingOnlyPositiveTechnicalFacts()
    {
        var asset = Held(IdentityHold.AttestedOnlyExternal, ".mp3");
        IdentityFact[] facts = [new("ev-codec", EvidenceDimension.AudioTechnical, ArtifactAudio.Codec, EvidencePolarity.Positive)];
        var input = ArtifactDecisionEvidence.Render(asset, facts, ArtifactDecisionKit.Expected, [], true);
        Assert.IsFalse(SemanticAssessmentParser.ParseArtifactRelease(ArtifactDecisionKit.Reply("release", "supports_declared_identity", "idv.ev-codec"), asset, facts, input).IsValid);
    }

    [TestMethod]
    public void AcceptedCollectionIsTheOnlyTitleNegativeExceptionAndNeedsRealTierBContents()
    {
        var asset = Held(IdentityHold.CollectionMissingRequestedWork);
        IdentityFact[] facts = [
            new("ev-title", EvidenceDimension.IdentityCorroboration, ArtifactIdentityCorroboration.TitleDiffers, EvidencePolarity.Negative),
            new("ev-accepted-collection", EvidenceDimension.IdentityAttestation, ArtifactIdentityAttestation.CollectionConsentRecorded, EvidencePolarity.Condition),
            new("ev-heading", EvidenceDimension.Profile, ArtifactProfile.Heading, EvidencePolarity.Unknown, "Cinder Gate: a story")];
        Assert.IsTrue(ModelReleasePolicy.IsEligible(asset, facts));
        var input = ArtifactDecisionEvidence.Render(asset, facts, ArtifactDecisionKit.Expected, [], true);
        var reply = ArtifactDecisionKit.Reply("contains_requested", "supports_declared_identity", "idv.ev-heading");
        Assert.IsTrue(SemanticAssessmentParser.ParseArtifactRelease(reply, asset, facts, input).IsValid);
        Assert.IsFalse(SemanticAssessmentParser.ParseArtifactDecision(reply, input.Findings).IsValid);
        Assert.IsFalse(ModelReleasePolicy.IsEligible(asset, facts.Where(f => f.Code != ArtifactIdentityAttestation.CollectionConsentRecorded).ToArray()));
        Assert.IsFalse(ModelReleasePolicy.IsEligible(asset, [.. facts, new("lang", EvidenceDimension.Language, ArtifactLanguage.DeclaredConflicts, EvidencePolarity.Negative)]));
    }
}
