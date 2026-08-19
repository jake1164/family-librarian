using FamilyLibrarian.Domain.Notifications;

namespace FamilyLibrarian.Application.Notifications;

/// <summary>The persistence boundary for notifications.</summary>
public interface INotificationRepository
{
    /// <summary>
    /// The most recent notification matching this audience/recipient/category/subject,
    /// regardless of age or read/dismissed state on any receipt. Used to decide
    /// whether a new occurrence should create a row or recur an existing one.
    /// </summary>
    Task<NotificationEvent?> FindLatestAsync(
        NotificationAudience audience,
        Guid? recipientUserId,
        string category,
        string? subjectType,
        string? subjectId,
        CancellationToken cancellationToken);

    Task AddAsync(NotificationEvent notification, CancellationToken cancellationToken);

    /// <summary>All receipts for a given notification event (used to reset them on recur).</summary>
    Task<IReadOnlyList<NotificationReceipt>> ListReceiptsAsync(
        Guid notificationEventId, CancellationToken cancellationToken);

    Task RemoveReceiptsAsync(IReadOnlyList<NotificationReceipt> receipts, CancellationToken cancellationToken);

    /// <summary>
    /// Notifications visible to this viewer: every SingleUser event addressed to
    /// them, plus every AdminBroadcast event when isAdmin is true. Each is paired
    /// with that viewer's own receipt (null if they've never read/dismissed it).
    /// Ordered by LastOccurredAtUtc descending.
    /// </summary>
    Task<IReadOnlyList<(NotificationEvent Event, NotificationReceipt? Receipt)>> ListForViewerAsync(
        Guid userId, bool isAdmin, CancellationToken cancellationToken);

    Task<NotificationReceipt?> FindReceiptAsync(
        Guid notificationEventId, Guid userId, CancellationToken cancellationToken);

    Task AddReceiptAsync(NotificationReceipt receipt, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
