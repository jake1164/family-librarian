using FamilyLibrarian.Application.Notifications;
using FamilyLibrarian.Domain.Notifications;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.Persistence;

public sealed class NotificationRepository(AppDbContext database) : INotificationRepository
{
    public Task<NotificationEvent?> FindLatestAsync(
        NotificationAudience audience,
        Guid? recipientUserId,
        string category,
        string? subjectType,
        string? subjectId,
        CancellationToken cancellationToken) =>
        database.NotificationEvents
            .Where(notification =>
                notification.Audience == audience &&
                notification.RecipientUserId == recipientUserId &&
                notification.Category == category &&
                notification.SubjectType == subjectType &&
                notification.SubjectId == subjectId)
            .OrderByDescending(notification => notification.LastOccurredAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

    public Task AddAsync(NotificationEvent notification, CancellationToken cancellationToken)
    {
        database.NotificationEvents.Add(notification);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<NotificationReceipt>> ListReceiptsAsync(
        Guid notificationEventId, CancellationToken cancellationToken) =>
        await database.NotificationReceipts
            .Where(receipt => receipt.NotificationEventId == notificationEventId)
            .ToArrayAsync(cancellationToken);

    public Task RemoveReceiptsAsync(IReadOnlyList<NotificationReceipt> receipts, CancellationToken cancellationToken)
    {
        database.NotificationReceipts.RemoveRange(receipts);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<(NotificationEvent Event, NotificationReceipt? Receipt)>> ListForViewerAsync(
        Guid userId, bool isAdmin, CancellationToken cancellationToken)
    {
        var events = await database.NotificationEvents
            .Where(notification =>
                (notification.Audience == NotificationAudience.SingleUser && notification.RecipientUserId == userId) ||
                (isAdmin && notification.Audience == NotificationAudience.AdminBroadcast))
            .OrderByDescending(notification => notification.LastOccurredAtUtc)
            .ToArrayAsync(cancellationToken);

        if (events.Length == 0)
        {
            return [];
        }

        var eventIds = events.Select(notification => notification.Id).ToArray();
        var receiptsByEventId = await database.NotificationReceipts
            .Where(receipt => eventIds.Contains(receipt.NotificationEventId) && receipt.UserId == userId)
            .ToDictionaryAsync(receipt => receipt.NotificationEventId, cancellationToken);

        return events
            .Select(notification => (
                notification,
                receiptsByEventId.GetValueOrDefault(notification.Id)))
            .ToArray();
    }

    public Task<NotificationReceipt?> FindReceiptAsync(
        Guid notificationEventId, Guid userId, CancellationToken cancellationToken) =>
        database.NotificationReceipts
            .SingleOrDefaultAsync(
                receipt => receipt.NotificationEventId == notificationEventId && receipt.UserId == userId,
                cancellationToken);

    public Task AddReceiptAsync(NotificationReceipt receipt, CancellationToken cancellationToken)
    {
        database.NotificationReceipts.Add(receipt);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        database.SaveChangesAsync(cancellationToken);
}
