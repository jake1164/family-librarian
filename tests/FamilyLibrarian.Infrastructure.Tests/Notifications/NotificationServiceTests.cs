using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Notifications;
using FamilyLibrarian.Domain.Notifications;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Infrastructure.Tests.Notifications;

/// <summary>Dedup/recurrence, viewer visibility, and read/dismiss rules.</summary>
[TestClass]
public sealed class NotificationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Admin = Guid.NewGuid();
    private static readonly Guid OtherAdmin = Guid.NewGuid();
    private static readonly Guid Reader = Guid.NewGuid();
    private static readonly Guid RequestId = Guid.NewGuid();

    [TestMethod]
    public async Task ANeedsReviewNotificationIsVisibleToAdminsOnly()
    {
        var repository = new InMemoryNotificationRepository();
        var asAnyone = CreateFor(repository, Guid.NewGuid());

        await asAnyone.RecordRequestNeedsReviewAsync(RequestId, "Dune", "No matching provider.", CancellationToken.None);

        var forAdmin = await CreateFor(repository, Admin).ListForViewerAsync(isAdmin: true, CancellationToken.None);
        var forReader = await CreateFor(repository, Reader).ListForViewerAsync(isAdmin: false, CancellationToken.None);

        Assert.AreEqual(1, forAdmin.Count);
        Assert.AreEqual("\"Dune\" needs review", forAdmin[0].Title);
        Assert.AreEqual(0, forReader.Count);
    }

    [TestMethod]
    public async Task AStatusNotificationIsVisibleOnlyToItsRecipient()
    {
        var repository = new InMemoryNotificationRepository();
        var asAnyone = CreateFor(repository, Guid.NewGuid());

        await asAnyone.RecordRequestStatusForUserAsync(
            Reader, RequestId, "Dune", RequestStatus.Available, CancellationToken.None);

        var forRecipient = await CreateFor(repository, Reader).ListForViewerAsync(isAdmin: false, CancellationToken.None);
        var forAdmin = await CreateFor(repository, Admin).ListForViewerAsync(isAdmin: true, CancellationToken.None);
        var forOtherReader = await CreateFor(repository, Guid.NewGuid()).ListForViewerAsync(isAdmin: false, CancellationToken.None);

        Assert.AreEqual(1, forRecipient.Count);
        Assert.AreEqual("\"Dune\" is available", forRecipient[0].Title);
        Assert.AreEqual(0, forAdmin.Count);
        Assert.AreEqual(0, forOtherReader.Count);
    }

    [TestMethod]
    public async Task CancelledDoesNotProduceAStatusNotification()
    {
        var repository = new InMemoryNotificationRepository();
        var service = CreateFor(repository, Guid.NewGuid());

        await service.RecordRequestStatusForUserAsync(
            Reader, RequestId, "Dune", RequestStatus.Cancelled, CancellationToken.None);

        Assert.AreEqual(0, repository.Events.Count);
    }

    [TestMethod]
    public async Task ARepeatedIssueRecursTheExistingRowInsteadOfDuplicating()
    {
        var repository = new InMemoryNotificationRepository();
        var service = CreateFor(repository, Guid.NewGuid());

        await service.RecordRequestNeedsReviewAsync(RequestId, "Dune", "First failure.", CancellationToken.None);
        await service.RecordRequestNeedsReviewAsync(RequestId, "Dune", "Second failure.", CancellationToken.None);

        Assert.AreEqual(1, repository.Events.Count);
        var notification = repository.Events.Single();
        Assert.AreEqual(2, notification.RepeatCount);
        Assert.AreEqual("Second failure.", notification.Detail);
    }

    [TestMethod]
    public async Task RecurringAnEventResetsEveryReceiptSoItReappearsAsUnread()
    {
        var repository = new InMemoryNotificationRepository();
        var producer = CreateFor(repository, Guid.NewGuid());
        var asAdmin = CreateFor(repository, Admin);
        var asOtherAdmin = CreateFor(repository, OtherAdmin);

        await producer.RecordRequestNeedsReviewAsync(RequestId, "Dune", "First failure.", CancellationToken.None);
        var notificationId = repository.Events.Single().Id;
        await asAdmin.DismissAsync(notificationId, CancellationToken.None);
        await asOtherAdmin.DismissAsync(notificationId, CancellationToken.None);

        Assert.AreEqual(0, (await asAdmin.ListForViewerAsync(isAdmin: true, CancellationToken.None)).Count);

        await producer.RecordRequestNeedsReviewAsync(RequestId, "Dune", "Second failure.", CancellationToken.None);

        Assert.AreEqual(1, (await asAdmin.ListForViewerAsync(isAdmin: true, CancellationToken.None)).Count);
        Assert.AreEqual(1, (await asOtherAdmin.ListForViewerAsync(isAdmin: true, CancellationToken.None)).Count);
    }

    [TestMethod]
    public async Task DismissingRemovesItFromThatViewersListOnly()
    {
        var repository = new InMemoryNotificationRepository();
        var producer = CreateFor(repository, Guid.NewGuid());
        var asAdmin = CreateFor(repository, Admin);
        var asOtherAdmin = CreateFor(repository, OtherAdmin);
        await producer.RecordRequestNeedsReviewAsync(RequestId, "Dune", null, CancellationToken.None);
        var notificationId = repository.Events.Single().Id;

        await asAdmin.DismissAsync(notificationId, CancellationToken.None);

        Assert.AreEqual(0, (await asAdmin.ListForViewerAsync(isAdmin: true, CancellationToken.None)).Count);
        Assert.AreEqual(1, (await asOtherAdmin.ListForViewerAsync(isAdmin: true, CancellationToken.None)).Count);
    }

    [TestMethod]
    public async Task DismissAllClearsEveryNotificationVisibleToThatViewer()
    {
        var repository = new InMemoryNotificationRepository();
        var producer = CreateFor(repository, Guid.NewGuid());
        var asAdmin = CreateFor(repository, Admin);
        var asOtherAdmin = CreateFor(repository, OtherAdmin);
        await producer.RecordRequestNeedsReviewAsync(RequestId, "Dune", null, CancellationToken.None);
        await producer.RecordRequestNeedsReviewAsync(Guid.NewGuid(), "Foundation", null, CancellationToken.None);

        await asAdmin.DismissAllAsync(isAdmin: true, CancellationToken.None);

        Assert.AreEqual(0, (await asAdmin.ListForViewerAsync(isAdmin: true, CancellationToken.None)).Count);
        Assert.AreEqual(2, (await asOtherAdmin.ListForViewerAsync(isAdmin: true, CancellationToken.None)).Count);
    }

    [TestMethod]
    public async Task MarkingReadDoesNotDismissIt()
    {
        var repository = new InMemoryNotificationRepository();
        var producer = CreateFor(repository, Guid.NewGuid());
        var asAdmin = CreateFor(repository, Admin);
        await producer.RecordRequestNeedsReviewAsync(RequestId, "Dune", null, CancellationToken.None);
        var notificationId = repository.Events.Single().Id;

        await asAdmin.MarkReadAsync(notificationId, CancellationToken.None);

        var visible = await asAdmin.ListForViewerAsync(isAdmin: true, CancellationToken.None);
        Assert.AreEqual(1, visible.Count);
        Assert.IsTrue(visible[0].IsRead);
    }

    private static NotificationService CreateFor(INotificationRepository repository, Guid userId) =>
        new(repository, new StubCurrentUser(userId), new FixedClock());

    private sealed class StubCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid? UserId => userId;

        public string? DisplayName => "Someone";
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class InMemoryNotificationRepository : INotificationRepository
    {
        public List<NotificationEvent> Events { get; } = [];

        public List<NotificationReceipt> Receipts { get; } = [];

        public Task<NotificationEvent?> FindLatestAsync(
            NotificationAudience audience,
            Guid? recipientUserId,
            string category,
            string? subjectType,
            string? subjectId,
            CancellationToken cancellationToken) =>
            Task.FromResult(Events
                .Where(notification =>
                    notification.Audience == audience &&
                    notification.RecipientUserId == recipientUserId &&
                    notification.Category == category &&
                    notification.SubjectType == subjectType &&
                    notification.SubjectId == subjectId)
                .OrderByDescending(notification => notification.LastOccurredAtUtc)
                .FirstOrDefault());

        public Task AddAsync(NotificationEvent notification, CancellationToken cancellationToken)
        {
            Events.Add(notification);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<NotificationReceipt>> ListReceiptsAsync(
            Guid notificationEventId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<NotificationReceipt>>(
                Receipts.Where(receipt => receipt.NotificationEventId == notificationEventId).ToArray());

        public Task RemoveReceiptsAsync(IReadOnlyList<NotificationReceipt> receipts, CancellationToken cancellationToken)
        {
            foreach (var receipt in receipts)
            {
                Receipts.Remove(receipt);
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<(NotificationEvent Event, NotificationReceipt? Receipt)>> ListForViewerAsync(
            Guid userId, bool isAdmin, CancellationToken cancellationToken)
        {
            var events = Events
                .Where(notification =>
                    (notification.Audience == NotificationAudience.SingleUser && notification.RecipientUserId == userId) ||
                    (isAdmin && notification.Audience == NotificationAudience.AdminBroadcast))
                .OrderByDescending(notification => notification.LastOccurredAtUtc)
                .Select(notification => (
                    notification,
                    Receipts.SingleOrDefault(receipt =>
                        receipt.NotificationEventId == notification.Id && receipt.UserId == userId)))
                .ToArray();

            return Task.FromResult<IReadOnlyList<(NotificationEvent Event, NotificationReceipt? Receipt)>>(events);
        }

        public Task<NotificationReceipt?> FindReceiptAsync(
            Guid notificationEventId, Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult(Receipts.SingleOrDefault(receipt =>
                receipt.NotificationEventId == notificationEventId && receipt.UserId == userId));

        public Task AddReceiptAsync(NotificationReceipt receipt, CancellationToken cancellationToken)
        {
            Receipts.Add(receipt);
            return Task.CompletedTask;
        }

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
