using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Application.SemanticAssessment;
using FamilyLibrarian.Domain.Evidence;
using FamilyLibrarian.Infrastructure.Security;
using FamilyLibrarian.Infrastructure.Tests.Security;
using FamilyLibrarian.Infrastructure.Tests.Characterization;

namespace FamilyLibrarian.Infrastructure.Tests.SemanticAssessment;

[TestClass]
public sealed class RealEpubReleaseTests
{
    [TestMethod]
    public async Task AnExternalMp3WithoutTagsIsAttestedOnlyAndCannotReleaseUsingTechnicalFacts()
    {
        var asset = ArtifactDecisionKit.Asset(format: ".mp3");
        var lookup = new EpubCorroboratedAuthorTests.Lookup(new WorkSummary(asset.WorkId, "Cinder Gate", "Mara Quillon", []));
        await using var bytes = new MemoryStream(new byte[4096]);
        var result = ArtifactHoldPolicy.Apply(asset, await new Mp3AssetIdentityVerifier(lookup, new DeterministicBookMatcher())
            .VerifyAsync(asset, bytes, CancellationToken.None));
        Assert.IsFalse(result.IsMatch);
        Assert.AreEqual(EvidenceReasonCodes.IdentityHold.AttestedOnlyExternal, result.HoldCode);
        Assert.IsTrue(result.Facts.Any(f => f.Code == EvidenceReasonCodes.ArtifactIdentityCorroboration.TagsAbsent));
        asset.TransitionStorageState(FamilyLibrarian.Domain.Acquisition.MediaAssetStorageState.Unmatched, DateTimeOffset.UtcNow);
        asset.SetIdentityHoldCode(result.HoldCode);
        IdentityFact[] facts = [.. result.Facts,
            new("ev-codec", EvidenceDimension.AudioTechnical, EvidenceReasonCodes.ArtifactAudio.Codec, EvidencePolarity.Positive)];
        var input = ArtifactDecisionEvidence.Render(asset, facts, ArtifactDecisionKit.Expected, [], true);
        Assert.IsFalse(SemanticAssessmentParser.ParseArtifactRelease(
            ArtifactDecisionKit.Reply("release", "supports_declared_identity", "idv.ev-codec"), asset, facts, input).IsValid);
    }

    [TestMethod]
    public async Task AnAuthorBeyondTheTwoEditRuleRemainsHeldButHasTheIndependentReleaseFacts()
    {
        var asset = ModelReleaseTests.Held();
        var expected = ArtifactDecisionKit.Expected with { Series = [new("The Lantern Cycle", "2")], CatalogPageCount = null };
        var lookup = new EpubCorroboratedAuthorTests.Lookup(new WorkSummary(asset.WorkId, "Cinder Gate", "Mara Quillon", [], Series: expected.Series));
        var verifier = new EpubAssetIdentityVerifier(lookup, new DeterministicBookMatcher(), new CharacterizationFixtures.NoRequestStore());
        await using var bytes = new MemoryStream(EpubCorroboratedAuthorTests.Book("Cinder Gate", "Mara Quillington",
            "<meta name=\"calibre:series\" content=\"The Lantern Cycle\"/><meta name=\"calibre:series_index\" content=\"2\"/>"));
        var result = await verifier.VerifyAsync(asset, bytes, CancellationToken.None);
        Assert.IsFalse(result.IsMatch, "The deterministic spelling rule must still hold this byline.");
        var facts = result.Facts.Concat(ArtifactProfileComparer.Compare(result.Facts, expected)).ToArray();
        Assert.IsTrue(facts.Any(ModelReleasePolicy.IsTitleAgreement));
        Assert.IsTrue(facts.Any(ModelReleasePolicy.IsLanguageAgreement));
        Assert.IsTrue(facts.Any(ModelReleasePolicy.IsIndependentAgreement));
        Assert.AreEqual(1, facts.Count(f => f.Polarity == EvidencePolarity.Negative));
        Assert.IsTrue(ModelReleasePolicy.IsEligible(asset, facts));
    }
}
