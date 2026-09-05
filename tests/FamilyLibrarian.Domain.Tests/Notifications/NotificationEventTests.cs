using FamilyLibrarian.Domain.Notifications;

namespace FamilyLibrarian.Domain.Tests.Notifications;

[TestClass]
public sealed class NotificationEventTests
{
    private static readonly DateTimeOffset OccurredAt = new(2026, 8, 19, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid RecipientUserId = Guid.NewGuid();

    [TestMethod]
    public void ASingleUserNotificationWithoutARecipientIsRejected() =>
        Assert.ThrowsExactly<ArgumentException>(() =>
            new NotificationEvent(
                NotificationAudience.SingleUser,
                recipientUserId: null,
                NotificationCategories.RequestStatusChanged,
                NotificationSeverity.Info,
                "Title",
                detail: null,
                subjectType: null,
                subjectId: null,
                OccurredAt));

    [TestMethod]
    public void AnAdminBroadcastNotificationWithARecipientIsRejected() =>
        Assert.ThrowsExactly<ArgumentException>(() =>
            new NotificationEvent(
                NotificationAudience.AdminBroadcast,
                RecipientUserId,
                NotificationCategories.RequestNeedsReview,
                NotificationSeverity.Warning,
                "Title",
                detail: null,
                subjectType: null,
                subjectId: null,
                OccurredAt));

    [TestMethod]
    public void AValidConstructionStartsWithOneOccurrence()
    {
        var notification = CreateAdminBroadcast();

        Assert.AreEqual(1, notification.RepeatCount);
        Assert.AreEqual(OccurredAt, notification.OccurredAtUtc);
        Assert.AreEqual(OccurredAt, notification.LastOccurredAtUtc);
    }

    [TestMethod]
    public void RecurIncrementsTheRepeatCountAndUpdatesTheLastOccurrence()
    {
        var notification = CreateAdminBroadcast();
        var recurredAt = OccurredAt.AddHours(1);

        notification.Recur(recurredAt, "Title", detail: "Second failure");

        Assert.AreEqual(2, notification.RepeatCount);
        Assert.AreEqual(OccurredAt, notification.OccurredAtUtc);
        Assert.AreEqual(recurredAt, notification.LastOccurredAtUtc);
        Assert.AreEqual("Second failure", notification.Detail);
    }

    [TestMethod]
    public void RecurUpdatesTheTitle()
    {
        var notification = CreateAdminBroadcast();

        notification.Recur(OccurredAt.AddHours(1), "Updated title", detail: null);

        Assert.AreEqual("Updated title", notification.Title);
    }

    [TestMethod]
    public void RecurWithNoDetailLeavesTheExistingDetailUnchanged()
    {
        var notification = new NotificationEvent(
            NotificationAudience.AdminBroadcast,
            recipientUserId: null,
            NotificationCategories.RequestNeedsReview,
            NotificationSeverity.Warning,
            "Title",
            detail: "First failure",
            subjectType: null,
            subjectId: null,
            OccurredAt);

        notification.Recur(OccurredAt.AddHours(1), "Title", detail: null);

        Assert.AreEqual("First failure", notification.Detail);
    }

    private static NotificationEvent CreateAdminBroadcast() =>
        new(
            NotificationAudience.AdminBroadcast,
            recipientUserId: null,
            NotificationCategories.RequestNeedsReview,
            NotificationSeverity.Warning,
            "Title",
            detail: null,
            subjectType: null,
            subjectId: null,
            OccurredAt);
}
