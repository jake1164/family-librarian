using FamilyLibrarian.Domain.Publishing;

namespace FamilyLibrarian.Domain.Tests.Publishing;

[TestClass]
public sealed class AudiobookshelfDeliveryTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void ANewDeliveryStartsUploading()
    {
        var delivery = new AudiobookshelfDelivery(Guid.NewGuid(), Now);

        Assert.AreEqual(AudiobookshelfDeliveryStatus.Uploading, delivery.Status);
        Assert.IsNull(delivery.CompletedAtUtc);
    }

    [TestMethod]
    public void AnEmptyAssetIdIsRejected()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new AudiobookshelfDelivery(Guid.Empty, Now));
    }

    [TestMethod]
    public void MarkDeliveredRequiresAnExternalItemId()
    {
        var delivery = new AudiobookshelfDelivery(Guid.NewGuid(), Now);

        Assert.ThrowsExactly<ArgumentException>(() => delivery.MarkDelivered(" ", Now));
    }

    [TestMethod]
    public void MarkDeliveredRecordsTheItemIdAndCompletion()
    {
        var delivery = new AudiobookshelfDelivery(Guid.NewGuid(), Now);
        delivery.MarkVerifying();

        delivery.MarkDelivered("li_abc123", Now.AddMinutes(1));

        Assert.AreEqual(AudiobookshelfDeliveryStatus.Delivered, delivery.Status);
        Assert.AreEqual("li_abc123", delivery.ExternalItemId);
        Assert.AreEqual(Now.AddMinutes(1), delivery.CompletedAtUtc);
        Assert.IsNull(delivery.FailureReason);
    }

    [TestMethod]
    public void MarkFailedRequiresAReason()
    {
        var delivery = new AudiobookshelfDelivery(Guid.NewGuid(), Now);

        Assert.ThrowsExactly<ArgumentException>(() => delivery.MarkFailed(string.Empty, Now));
    }

    [TestMethod]
    public void ResetForRetryClearsFailureAndCompletion()
    {
        var delivery = new AudiobookshelfDelivery(Guid.NewGuid(), Now);
        delivery.MarkFailed("upload error", Now.AddMinutes(1));

        delivery.ResetForRetry();

        Assert.AreEqual(AudiobookshelfDeliveryStatus.Uploading, delivery.Status);
        Assert.IsNull(delivery.FailureReason);
        Assert.IsNull(delivery.CompletedAtUtc);
    }
}
