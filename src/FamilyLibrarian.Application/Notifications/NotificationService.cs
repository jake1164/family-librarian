using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Domain.Notifications;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Application.Notifications;

/// <summary>
/// Owns the rules for recording, collapsing, and clearing notifications.
/// Callers only describe *what* happened; this decides whether that becomes
/// a new row or recurs an existing one.
/// </summary>
public sealed class NotificationService(
    INotificationRepository repository,
    ICurrentUser currentUser,
    IClock clock)
{
    public Task RecordRequestNeedsReviewAsync(
        Guid requestId, string workTitle, string? reason, CancellationToken cancellationToken) =>
        UpsertAsync(
            NotificationAudience.AdminBroadcast,
            recipientUserId: null,
            NotificationCategories.RequestNeedsReview,
            NotificationSeverity.Warning,
            title: $"\"{workTitle}\" needs review",
            detail: reason,
            subjectType: NotificationSubjectTypes.BookRequest,
            subjectId: requestId.ToString(),
            cancellationToken);

    public Task RecordRequestStatusForUserAsync(
        Guid userId, Guid requestId, string workTitle, RequestStatus to, CancellationToken cancellationToken)
    {
        var (severity, title) = to switch
        {
            RequestStatus.Available => (NotificationSeverity.Info, $"\"{workTitle}\" is available"),
            RequestStatus.NotAvailable => (NotificationSeverity.Warning, $"\"{workTitle}\" could not be found"),
            _ => ((NotificationSeverity?)null, (string?)null)
        };

        if (severity is null)
        {
            return Task.CompletedTask;
        }

        return UpsertAsync(
            NotificationAudience.SingleUser,
            recipientUserId: userId,
            NotificationCategories.RequestStatusChanged,
            severity.Value,
            title!,
            detail: null,
            subjectType: NotificationSubjectTypes.BookRequest,
            subjectId: requestId.ToString(),
            cancellationToken);
    }

    public async Task<IReadOnlyList<NotificationView>> ListForViewerAsync(
        bool isAdmin, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return [];
        }

        var rows = await repository.ListForViewerAsync(userId, isAdmin, cancellationToken);
        return rows
            .Where(row => row.Receipt?.DismissedAtUtc is null)
            .Select(row => new NotificationView(
                row.Event.Id,
                row.Event.Category,
                row.Event.Severity,
                row.Event.Title,
                row.Event.Detail,
                row.Event.SubjectType,
                row.Event.SubjectId,
                row.Event.RepeatCount,
                row.Event.OccurredAtUtc,
                row.Event.LastOccurredAtUtc,
                IsRead: row.Receipt?.ReadAtUtc is not null))
            .ToArray();
    }

    public async Task MarkReadAsync(Guid notificationEventId, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return;
        }

        var receipt = await GetOrCreateReceiptAsync(notificationEventId, userId, cancellationToken);
        receipt.MarkRead(clock.UtcNow);
        await repository.SaveChangesAsync(cancellationToken);
    }

    public async Task DismissAsync(Guid notificationEventId, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return;
        }

        var receipt = await GetOrCreateReceiptAsync(notificationEventId, userId, cancellationToken);
        receipt.Dismiss(clock.UtcNow);
        await repository.SaveChangesAsync(cancellationToken);
    }

    public async Task DismissAllAsync(bool isAdmin, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return;
        }

        var rows = await repository.ListForViewerAsync(userId, isAdmin, cancellationToken);
        var now = clock.UtcNow;

        foreach (var (evt, receipt) in rows)
        {
            if (receipt is null)
            {
                var created = new NotificationReceipt(evt.Id, userId);
                created.Dismiss(now);
                await repository.AddReceiptAsync(created, cancellationToken);
            }
            else if (receipt.DismissedAtUtc is null)
            {
                receipt.Dismiss(now);
            }
        }

        await repository.SaveChangesAsync(cancellationToken);
    }

    private async Task UpsertAsync(
        NotificationAudience audience,
        Guid? recipientUserId,
        string category,
        NotificationSeverity severity,
        string title,
        string? detail,
        string? subjectType,
        string? subjectId,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var existing = await repository.FindLatestAsync(
            audience, recipientUserId, category, subjectType, subjectId, cancellationToken);

        if (existing is null)
        {
            await repository.AddAsync(
                new NotificationEvent(
                    audience, recipientUserId, category, severity, title, detail, subjectType, subjectId, now),
                cancellationToken);
        }
        else
        {
            existing.Recur(now, title, detail);
            var receipts = await repository.ListReceiptsAsync(existing.Id, cancellationToken);
            if (receipts.Count > 0)
            {
                await repository.RemoveReceiptsAsync(receipts, cancellationToken);
            }
        }

        await repository.SaveChangesAsync(cancellationToken);
    }

    private async Task<NotificationReceipt> GetOrCreateReceiptAsync(
        Guid notificationEventId, Guid userId, CancellationToken cancellationToken)
    {
        var receipt = await repository.FindReceiptAsync(notificationEventId, userId, cancellationToken);
        if (receipt is not null)
        {
            return receipt;
        }

        receipt = new NotificationReceipt(notificationEventId, userId);
        await repository.AddReceiptAsync(receipt, cancellationToken);
        return receipt;
    }
}
