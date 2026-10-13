using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Evidence;

namespace FamilyLibrarian.Infrastructure.Tests.Security;

[TestClass]
public sealed class PeerSizeProfileTests
{
    [TestMethod]
    [DataRow(600_000L, 4_000_000L, 3, true)]
    [DataRow(1_199_999L, 4_000_000L, 3, true)]
    [DataRow(1_200_000L, 4_000_000L, 3, false)]
    [DataRow(1_200_001L, 4_000_000L, 3, false)]
    [DataRow(600_000L, 4_000_000L, 2, false)]
    [DataRow(600_000L, 0L, 3, false)]
    [DataRow(-1L, 4_000_000L, 3, false)]
    [DataRow(0L, long.MaxValue, 3, true)]
    public void ThirtyPercentIsStrictAndAlwaysWeak(long bytes, long median, int count, bool finding)
    {
        var expected = new ExpectedArtifactProfile("Book", [], null, [], [], "en", null, null, false, median, count, bytes);
        var facts = ArtifactProfileComparer.Compare([], expected);
        Assert.AreEqual(finding ? 1 : 0, facts.Count);
        if (finding)
        {
            Assert.AreEqual(EvidencePolarity.Weak, facts[0].Polarity);
            Assert.AreEqual(EvidenceReasonCodes.ProfileComparison.SizePeerOutlier, facts[0].Code);
        }
    }

    [TestMethod]
    public void MissingMeasurementsNeverProducePeerEvidence()
    {
        var basis = new ExpectedArtifactProfile("Book", [], null, [], [], null, null, null, false, 4_000_000, 3, 600_000);
        Assert.IsEmpty(ArtifactProfileComparer.Compare([], basis with { PeerMedianSizeBytes = null }));
        Assert.IsEmpty(ArtifactProfileComparer.Compare([], basis with { PeerCount = null }));
        Assert.IsEmpty(ArtifactProfileComparer.Compare([], basis with { AssetSizeBytes = null }));
    }
}
