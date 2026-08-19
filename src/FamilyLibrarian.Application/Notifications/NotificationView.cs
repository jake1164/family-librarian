using FamilyLibrarian.Domain.Notifications;

namespace FamilyLibrarian.Application.Notifications;

/// <summary>
/// A notification as seen by one viewer. Kept separate from
/// <see cref="NotificationEvent"/> so the domain entity doesn't leak past
/// this layer.
/// </summary>
public sealed record NotificationView(
    Guid Id,
    string Category,
    NotificationSeverity Severity,
    string Title,
    string? Detail,
    string? SubjectType,
    string? SubjectId,
    int RepeatCount,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset LastOccurredAtUtc,
    bool IsRead);
