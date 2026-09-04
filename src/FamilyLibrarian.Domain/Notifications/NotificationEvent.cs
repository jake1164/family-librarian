namespace FamilyLibrarian.Domain.Notifications;

/// <summary>
/// A notification shown in the tray: either broadcast to every admin, or
/// addressed to one specific user.
/// </summary>
/// <remarks>
/// Repeated occurrences of the same kind of thing (e.g. the same provider
/// failing twice) collapse onto one row via <see cref="Recur"/> rather than
/// creating a new one, so the tray doesn't fill up with duplicates.
/// </remarks>
public sealed class NotificationEvent
{
    public const int MaxTitleLength = 256;
    public const int MaxDetailLength = 2_000;

    private NotificationEvent()
    {
    }

    public NotificationEvent(
        NotificationAudience audience,
        Guid? recipientUserId,
        string category,
        NotificationSeverity severity,
        string title,
        string? detail,
        string? subjectType,
        string? subjectId,
        DateTimeOffset occurredAtUtc)
    {
        if (audience == NotificationAudience.SingleUser && recipientUserId is null)
        {
            throw new ArgumentException(
                "A recipient is required for a single-user notification.", nameof(recipientUserId));
        }

        if (audience == NotificationAudience.AdminBroadcast && recipientUserId is not null)
        {
            throw new ArgumentException(
                "An admin broadcast notification has no single recipient.", nameof(recipientUserId));
        }

        Audience = audience;
        RecipientUserId = recipientUserId;
        Category = RequireText(category, nameof(category), maxLength: 128);
        Severity = severity;
        Title = RequireText(title, nameof(title), MaxTitleLength);
        Detail = CleanOptionalText(detail, MaxDetailLength, nameof(detail));
        SubjectType = subjectType;
        SubjectId = subjectId;
        RepeatCount = 1;
        OccurredAtUtc = occurredAtUtc;
        LastOccurredAtUtc = occurredAtUtc;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public NotificationAudience Audience { get; private set; }

    public Guid? RecipientUserId { get; private set; }

    public string Category { get; private set; } = null!;

    public NotificationSeverity Severity { get; private set; }

    public string Title { get; private set; } = null!;

    public string? Detail { get; private set; }

    public string? SubjectType { get; private set; }

    public string? SubjectId { get; private set; }

    public int RepeatCount { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    public DateTimeOffset LastOccurredAtUtc { get; private set; }

    public void Recur(DateTimeOffset occurredAtUtc, string title, string? detail)
    {
        RepeatCount++;
        LastOccurredAtUtc = occurredAtUtc;
        Title = RequireText(title, nameof(title), MaxTitleLength);
        if (detail is not null)
        {
            Detail = CleanOptionalText(detail, MaxDetailLength, nameof(detail));
        }
    }

    private static string RequireText(string value, string parameterName, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A value is required.", parameterName);
        }

        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
        {
            throw new ArgumentException($"Must be {maxLength} characters or fewer.", parameterName);
        }

        return trimmed;
    }

    private static string? CleanOptionalText(string? value, int maxLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
        {
            throw new ArgumentException($"Must be {maxLength} characters or fewer.", parameterName);
        }

        return trimmed;
    }
}
