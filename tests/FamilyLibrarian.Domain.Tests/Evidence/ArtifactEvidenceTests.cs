using FamilyLibrarian.Domain.Evidence;

namespace FamilyLibrarian.Domain.Tests.Evidence;

[TestClass]
public sealed class ArtifactEvidenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);

    private static ArtifactEvidence Create(string? value = "v", string? tool = "1.0", Guid? assetId = null, Guid? recordId = null) => new(
        assetId ?? Guid.NewGuid(), new string('a', 64), ArtifactEvidenceRecordKinds.SecurityEvaluation, recordId ?? Guid.NewGuid(),
        "ev-x", EvidenceDimension.Security, "artifact.security.scan_clean", EvidencePolarity.Positive, "scanner:test", value, tool, Now);

    [TestMethod]
    public void EvidenceCarriesFullProvenanceAndTheCurrentSchemaVersion()
    {
        var assetId = Guid.NewGuid();
        var recordId = Guid.NewGuid();

        var evidence = Create(assetId: assetId, recordId: recordId);

        Assert.AreEqual(assetId, evidence.MediaAssetId);
        Assert.AreEqual(recordId, evidence.RecordId);
        Assert.AreEqual(new string('a', 64), evidence.Sha256);
        Assert.AreEqual(ArtifactEvidence.SchemaVersion, evidence.EvidenceSchemaVersion);
        Assert.AreEqual("ev-x", evidence.FindingId);
        Assert.AreEqual("artifact.security.scan_clean", evidence.Code);
        Assert.AreEqual(EvidencePolarity.Positive, evidence.Polarity);
        Assert.AreEqual("scanner:test", evidence.Source);
        Assert.AreEqual("1.0", evidence.ToolVersion);
        Assert.AreEqual(Now, evidence.RecordedAtUtc);
    }

    [TestMethod]
    public void ValuesAreBoundedAndControlCharactersAreRemoved()
    {
        var stored = Create(value: "a\u0000b\u001b\n" + new string('x', 1_000)).Value!;

        Assert.AreEqual(ArtifactEvidence.MaxValueLength, stored.Length);
        Assert.IsFalse(stored.Any(char.IsControl));
        Assert.IsNull(Create(value: "   \n ").Value);
        Assert.IsNull(Create(value: null).Value);
    }

    [TestMethod]
    public void EveryRecordedEvidenceGetsADistinctId() =>
        Assert.AreNotEqual(Create().Id, Create().Id);

    [TestMethod]
    public void InvalidIdentifiersAreRejected()
    {
        Assert.Throws<ArgumentException>(() => Create(assetId: Guid.Empty) is var _ ? 0 : 0);
        Assert.Throws<ArgumentException>(() => new ArtifactEvidence(
            Guid.NewGuid(), "h", "k", Guid.NewGuid(), new string('f', ArtifactEvidence.MaxFindingIdLength + 1),
            EvidenceDimension.Security, "c", EvidencePolarity.Unknown, "s", null, null, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ArtifactEvidence(
            Guid.NewGuid(), "h", "k", Guid.NewGuid(), "f", (EvidenceDimension)99, "c", EvidencePolarity.Unknown, "s", null, null, Now));
    }

    [TestMethod]
    public void IdentityAttestationAndContentCorroborationAreSeparateDimensions()
    {
        Assert.AreEqual(7, Enum.GetValues<EvidenceDimension>().Length, "Language, structure, completeness, security and audio measurements are also distinct.");
    }
}
