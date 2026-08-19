using FamilyLibrarian.Domain.Notifications;

namespace FamilyLibrarian.Domain.Tests.Notifications;

[TestClass]
public sealed class NotificationReceiptTests
{
    private static readonly Guid NotificationEventId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateTimeOffset FirstReadAt = new(2026, 8, 19, 9, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void MarkReadIsIdempotent()
    {
        var receipt = new NotificationReceipt(NotificationEventId, UserId);

        receipt.MarkRead(FirstReadAt);
        receipt.MarkRead(FirstReadAt.AddHours(1));

        Assert.AreEqual(FirstReadAt, receipt.ReadAtUtc);
    }

    [TestMethod]
    public void DismissAlwaysOverwritesTheTimestamp()
    {
        var receipt = new NotificationReceipt(NotificationEventId, UserId);
        var firstDismissAt = FirstReadAt;
        var secondDismissAt = FirstReadAt.AddHours(1);

        receipt.Dismiss(firstDismissAt);
        receipt.Dismiss(secondDismissAt);

        Assert.AreEqual(secondDismissAt, receipt.DismissedAtUtc);
    }

    [TestMethod]
    public void AnEmptyUserIdIsRejected() =>
        Assert.ThrowsExactly<ArgumentException>(() =>
            new NotificationReceipt(NotificationEventId, Guid.Empty));

    [TestMethod]
    public void AnEmptyNotificationEventIdIsRejected() =>
        Assert.ThrowsExactly<ArgumentException>(() =>
            new NotificationReceipt(Guid.Empty, UserId));
}
