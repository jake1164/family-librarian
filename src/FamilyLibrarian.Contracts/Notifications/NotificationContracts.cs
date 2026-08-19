namespace FamilyLibrarian.Contracts.Notifications;

public sealed record NotificationResponse(
    Guid Id,
    string Category,
    string Severity,
    string Title,
    string? Detail,
    string? SubjectType,
    string? SubjectId,
    int RepeatCount,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset LastOccurredAtUtc,
    bool IsRead);

public sealed record NotificationListResponse(IReadOnlyList<NotificationResponse> Notifications);
