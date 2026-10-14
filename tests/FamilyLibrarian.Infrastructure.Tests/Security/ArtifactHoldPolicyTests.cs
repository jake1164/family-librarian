using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Evidence;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Infrastructure.Tests.Security;

/// <summary>
/// M1-5b: the pure hold policy. Rows mirror the approved outcome matrix (readiness note section 4).
/// </summary>
[TestClass]
public sealed class ArtifactHoldPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 0, 0, 0, TimeSpan.Zero);
    private const string NoVerifier = ArtifactEvidenceRecorder.NoVerifierId;
    private const string EpubVerifier = "epub-package-metadata";

    private static MediaAsset Asset(AssetOrigin origin, bool preConfirmed = true, string format = ".mp3")
    {
        var asset = new MediaAsset(
            Guid.NewGuid(), null, RequestMediaType.Ebook, format, "a" + format, "s" + format, 1, new string('a', 64),
            "application/octet-stream", Guid.NewGuid(), null, Now, identityPreConfirmed: preConfirmed,
            origin: origin == AssetOrigin.Legacy ? AssetOrigin.Unknown : origin,
            originUserId: origin == AssetOrigin.ManualUpload ? Guid.NewGuid() : null);
        if (origin == AssetOrigin.Legacy)
        {
            typeof(MediaAsset).GetProperty(nameof(MediaAsset.Origin))!.SetValue(asset, AssetOrigin.Legacy);
        }

        return asset;
    }

    private static IdentityFact Cue(string code, string value) =>
        new("ev-" + code, EvidenceDimension.Completeness, code, EvidencePolarity.Condition, value);

    private static AssetIdentityVerificationResult Matched(string verifier, params IdentityFact[] facts) =>
        AssetIdentityVerificationResult.Match(verifier, facts);

    // --- attested-only formats (P1) ------------------------------------------------------------------------

    [TestMethod]
    [DataRow(AssetOrigin.ManualUpload)]
    [DataRow(AssetOrigin.BuiltInProvider)]
    [DataRow(AssetOrigin.Legacy)]
    public void AnAttestedOnlyFormatFromAFirstPartyOrLegacySourceStaysTrustedAsBefore(AssetOrigin origin)
    {
        var result = ArtifactHoldPolicy.Apply(Asset(origin), Matched(NoVerifier));

        Assert.IsTrue(result.IsMatch);
        Assert.IsNull(result.HoldCode);
    }

    [TestMethod]
    public void AnAttestedOnlyFormatFromAnExternalProviderIsHeld()
    {
        var result = ArtifactHoldPolicy.Apply(Asset(AssetOrigin.ExternalProvider), Matched(NoVerifier));

        Assert.IsFalse(result.IsMatch);
        Assert.AreEqual("identity.hold.attested_only_external", result.HoldCode);
        StringAssert.Contains(result.Reason, "external provider");
        Assert.AreEqual(NoVerifier, result.VerifierId, "The recorder still reports 'no content verifier' for it.");
    }

    [TestMethod]
    public void AnAttestedOnlyFormatWithNoTrustworthyOriginIsHeldToo()
    {
        var result = ArtifactHoldPolicy.Apply(Asset(AssetOrigin.Unknown), Matched(NoVerifier));

        Assert.IsFalse(result.IsMatch);
        Assert.AreEqual("identity.hold.attested_only_unknown_origin", result.HoldCode);
    }

    [TestMethod]
    public void AFormatWithAContentVerifierIsNeverHeldForLackOfOne()
    {
        // EPUB/M4B: the verifier read the file, so the origin does not matter.
        foreach (var origin in new[] { AssetOrigin.ExternalProvider, AssetOrigin.Unknown, AssetOrigin.ManualUpload })
        {
            var result = ArtifactHoldPolicy.Apply(Asset(origin, format: ".epub"), Matched(EpubVerifier));
            Assert.IsTrue(result.IsMatch, origin.ToString());
        }
    }

    // --- completeness cues (D-16) -----------------------------------------------------------------------------

    [TestMethod]
    [DataRow(AssetOrigin.ManualUpload)]
    [DataRow(AssetOrigin.BuiltInProvider)]
    [DataRow(AssetOrigin.ExternalProvider)]
    [DataRow(AssetOrigin.Unknown)]
    public void AnExplicitSampleMarkerHoldsAnEpubFromEveryNonLegacyOrigin(AssetOrigin origin)
    {
        var result = ArtifactHoldPolicy.Apply(
            Asset(origin, format: ".epub"),
            Matched(EpubVerifier, Cue("artifact.completeness.sample_marker_in_metadata", "subject:sample")));

        Assert.IsFalse(result.IsMatch);
        Assert.AreEqual("identity.hold.completeness_sample_marker", result.HoldCode);
        StringAssert.Contains(result.Reason, "subject:sample");
    }

    [TestMethod]
    public void MissingReadingOrderContentHolds()
    {
        var result = ArtifactHoldPolicy.Apply(
            Asset(AssetOrigin.ManualUpload, format: ".epub"),
            Matched(EpubVerifier, Cue("artifact.completeness.spine_content_missing", "2/5")));

        Assert.IsFalse(result.IsMatch);
        Assert.AreEqual("identity.hold.completeness_spine_content_missing", result.HoldCode);
        StringAssert.Contains(result.Reason, "2/5");
    }

    [TestMethod]
    public void BothCuesAreListedAndTheFirstBecomesTheCode()
    {
        var result = ArtifactHoldPolicy.Apply(
            Asset(AssetOrigin.ManualUpload, format: ".epub"),
            Matched(EpubVerifier,
                Cue("artifact.completeness.sample_marker_in_metadata", "title:preview"),
                Cue("artifact.completeness.spine_content_missing", "1/3")));

        Assert.AreEqual("identity.hold.completeness_sample_marker", result.HoldCode);
        StringAssert.Contains(result.Reason, "title:preview");
        StringAssert.Contains(result.Reason, "1/3");
    }

    [TestMethod]
    public void MissingNavigationAboveTheOwnerThresholdHoldsAndDuplicateReadingOrderRemainsEvidence()
    {
        var result = ArtifactHoldPolicy.Apply(
            Asset(AssetOrigin.ExternalProvider, format: ".epub"),
            Matched(EpubVerifier,
                Cue("artifact.completeness.toc_targets_missing", "3/10"),
                Cue("artifact.completeness.duplicate_reading_order_items", "2")));

        Assert.IsFalse(result.IsMatch);
        Assert.AreEqual("identity.hold.toc_entries_missing", result.HoldCode);
    }

    [TestMethod]
    public void NoCueAndUnknownCompletenessNeverHoldAndNeverBecomePositive()
    {
        var result = ArtifactHoldPolicy.Apply(
            Asset(AssetOrigin.ExternalProvider, format: ".epub"),
            Matched(EpubVerifier,
                new IdentityFact("ev-completeness-counts", EvidenceDimension.Completeness, "artifact.completeness.reading_order_counted", EvidencePolarity.Unknown, "spine=3"),
                new IdentityFact("ev-completeness-no-cue", EvidenceDimension.Completeness, "artifact.completeness.no_cue_detected", EvidencePolarity.Unknown, "none")));

        Assert.IsTrue(result.IsMatch);
        Assert.IsFalse(result.Facts.Any(fact => fact.Polarity == EvidencePolarity.Positive && fact.Dimension == EvidenceDimension.Completeness));
    }

    [TestMethod]
    public void ALegacyRowKeepsItsPriorBehaviorEvenWithACue()
    {
        var result = ArtifactHoldPolicy.Apply(
            Asset(AssetOrigin.Legacy, format: ".epub"),
            Matched(EpubVerifier, Cue("artifact.completeness.sample_marker_in_metadata", "title:sample")));

        Assert.IsTrue(result.IsMatch, "No retroactive holds on rows that predate origin tracking (D-19).");
    }

    // --- precedence and pass-through ------------------------------------------------------------------------

    [TestMethod]
    public void AResultThatIsAlreadyUnmatchedKeepsItsOwnCauseUntouched()
    {
        var unmatched = AssetIdentityVerificationResult.Unmatched(EpubVerifier, "title differs", []);

        var result = ArtifactHoldPolicy.Apply(Asset(AssetOrigin.ExternalProvider, format: ".epub"), unmatched);

        Assert.AreSame(unmatched, result);
        Assert.IsNull(result.HoldCode);
    }

    [TestMethod]
    public void TheProviderIdNeverEntersIntoThePolicy()
    {
        // Same outcome for every origin-equivalent asset regardless of any provider name: the policy has no such input.
        var parameters = typeof(ArtifactHoldPolicy).GetMethod(nameof(ArtifactHoldPolicy.Apply))!.GetParameters()
            .Select(parameter => parameter.ParameterType).ToArray();
        CollectionAssert.AreEqual(new[] { typeof(MediaAsset), typeof(AssetIdentityVerificationResult) }, parameters);
    }
}
