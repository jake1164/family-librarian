using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Evidence;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Infrastructure.Tests.Security;

[TestClass]
public sealed class ArtifactProfileThresholdTests
{
    [TestMethod]
    [DataRow("1/40", true)]
    [DataRow("3/40", true)]
    [DataRow("2/20", false)]
    [DataRow("1/5", true)]
    public void MissingNavigationHoldsOnlyAtBothOwnerApprovedThresholds(string missing, bool expectedMatch)
    {
        var asset = new MediaAsset(Guid.NewGuid(), null, RequestMediaType.Ebook, ".epub",
            "book.epub", "stored.epub", 1, new string('a', 64), "application/epub+zip",
            Guid.NewGuid(), null, DateTimeOffset.UtcNow, origin: AssetOrigin.ExternalProvider);
        var result = ArtifactHoldPolicy.Apply(asset, AssetIdentityVerificationResult.Match("epub-package-metadata",
            [new IdentityFact("ev-completeness-toc-missing", EvidenceDimension.Completeness,
                EvidenceReasonCodes.ArtifactCompleteness.TocTargetsMissing, EvidencePolarity.Condition, missing)]));

        Assert.AreEqual(expectedMatch, result.IsMatch);
        Assert.AreEqual(expectedMatch ? null : "identity.hold.toc_entries_missing", result.HoldCode);
    }
}
