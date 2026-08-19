namespace FamilyLibrarian.Domain.Notifications;

/// <summary>
/// One viewer's read/dismissed state for one <see cref="NotificationEvent"/>.
/// An admin-broadcast event is shared, but each admin reads and dismisses it
/// independently, so this is keyed per (event, user) rather than living on
/// the event itself.
/// </summary>
public sealed class NotificationReceipt
{
    private NotificationReceipt()
    {
    }

    public NotificationReceipt(Guid notificationEventId, Guid userId)
    {
        if (notificationEventId == Guid.Empty)
        {
            throw new ArgumentException("A notification event ID is required.", nameof(notificationEventId));
        }

        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A user ID is required.", nameof(userId));
        }

        NotificationEventId = notificationEventId;
        UserId = userId;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public Guid NotificationEventId { get; private set; }

    public Guid UserId { get; private set; }

    public DateTimeOffset? ReadAtUtc { get; private set; }

    public DateTimeOffset? DismissedAtUtc { get; private set; }

    public void MarkRead(DateTimeOffset atUtc) => ReadAtUtc ??= atUtc;

    public void Dismiss(DateTimeOffset atUtc) => DismissedAtUtc = atUtc;
}
