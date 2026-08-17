using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Domain.Tests.Acquisition;

[TestClass]
public sealed class MediaAssetTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    private static MediaAsset CreateAsset() => new(
        Guid.NewGuid(),
        editionId: null,
        RequestMediaType.Ebook,
        ".epub",
        "My Book.epub",
        "3f1a2b4c.epub",
        sizeBytes: 2048,
        sha256: new string('a', 64),
        detectedMimeType: "application/epub+zip",
        associatedRequestFormatId: Guid.NewGuid(),
        sourceAcquisitionCandidateId: Guid.NewGuid(),
        Now);

    [TestMethod]
    public void ANewAssetStartsInQuarantine()
    {
        var asset = CreateAsset();

        Assert.AreEqual(MediaAssetStorageState.Quarantine, asset.StorageState);
    }

    [TestMethod]
    public void QuarantineCanMoveToProcessing()
    {
        var asset = CreateAsset();

        asset.TransitionStorageState(MediaAssetStorageState.Processing, Now.AddMinutes(1));

        Assert.AreEqual(MediaAssetStorageState.Processing, asset.StorageState);
    }

    [TestMethod]
    public void QuarantineCannotMoveDirectlyToTrusted()
    {
        var asset = CreateAsset();

        Assert.ThrowsExactly<InvalidMediaAssetStorageTransitionException>(() =>
            asset.TransitionStorageState(MediaAssetStorageState.Trusted, Now.AddMinutes(1)));
    }

    [TestMethod]
    public void ARejectedAssetCanBeDestroyedButCannotReenterThePipeline()
    {
        var asset = CreateAsset();
        asset.TransitionStorageState(MediaAssetStorageState.Rejected, Now.AddMinutes(1));

        asset.TransitionStorageState(MediaAssetStorageState.Destroyed, Now.AddMinutes(2));

        Assert.ThrowsExactly<InvalidMediaAssetStorageTransitionException>(() =>
            asset.TransitionStorageState(MediaAssetStorageState.Processing, Now.AddMinutes(3)));
    }

    [TestMethod]
    public void ANonPositiveSizeIsRejected()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new MediaAsset(
            Guid.NewGuid(),
            null,
            RequestMediaType.Ebook,
            ".epub",
            "My Book.epub",
            "3f1a2b4c.epub",
            sizeBytes: 0,
            sha256: new string('a', 64),
            detectedMimeType: "application/epub+zip",
            associatedRequestFormatId: Guid.NewGuid(),
            sourceAcquisitionCandidateId: null,
            Now));
    }

    [TestMethod]
    public void AnEmptyAssociatedRequestFormatIdIsRejected()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new MediaAsset(
            Guid.NewGuid(),
            null,
            RequestMediaType.Ebook,
            ".epub",
            "My Book.epub",
            "3f1a2b4c.epub",
            sizeBytes: 2048,
            sha256: new string('a', 64),
            detectedMimeType: "application/epub+zip",
            associatedRequestFormatId: Guid.Empty,
            sourceAcquisitionCandidateId: null,
            Now));
    }
}
