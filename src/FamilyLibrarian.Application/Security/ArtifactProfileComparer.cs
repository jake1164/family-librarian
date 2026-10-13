using System.Globalization;
using FamilyLibrarian.Domain.Evidence;

namespace FamilyLibrarian.Application.Security;

public static class ArtifactProfileComparer
{
    public const decimal PeerOutlierRatio = 0.30m;

    public static IReadOnlyList<IdentityFact> Compare(IReadOnlyList<IdentityFact> facts, ExpectedArtifactProfile expected)
    {
        _ = facts;
        if (expected.PeerCount is not >= 3 || expected.PeerMedianSizeBytes is not > 0 ||
            expected.AssetSizeBytes is not >= 0 || expected.AssetSizeBytes.Value >= expected.PeerMedianSizeBytes.Value * PeerOutlierRatio)
            return [];
        return [new("ev-profile-peer-size", EvidenceDimension.Completeness, EvidenceReasonCodes.ProfileComparison.SizePeerOutlier,
            EvidencePolarity.Weak, string.Create(CultureInfo.InvariantCulture,
                $"bytes={expected.AssetSizeBytes};peer_median_bytes={expected.PeerMedianSizeBytes};peers={expected.PeerCount}"))];
    }
}
