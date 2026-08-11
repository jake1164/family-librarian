namespace FamilyLibrarian.Domain.Audit;

/// <summary>
/// An append-only record of an administrative action.
/// </summary>
/// <remarks>
/// <see cref="Detail"/> is a small JSON object describing what changed. It must
/// never contain a credential, ciphertext, or any other secret value — the point
/// of the audit trail is that "an admin replaced the Google Books key at 14:02"
/// is visible without the key itself being recoverable from it.
/// </remarks>
public sealed class AuditEvent
{
    private AuditEvent()
    {
    }

    public AuditEvent(
        string action,
        string subjectType,
        string? subjectId,
        Guid? actorUserId,
        string? actorDisplayName,
        string? detail,
        DateTimeOffset occurredAtUtc)
    {
        Action = RequireText(action, nameof(action));
        SubjectType = RequireText(subjectType, nameof(subjectType));
        SubjectId = subjectId;
        ActorUserId = actorUserId;
        ActorDisplayName = actorDisplayName;
        Detail = detail;
        OccurredAtUtc = occurredAtUtc;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public string Action { get; private set; } = null!;

    public string SubjectType { get; private set; } = null!;

    public string? SubjectId { get; private set; }

    public Guid? ActorUserId { get; private set; }

    public string? ActorDisplayName { get; private set; }

    /// <summary>Non-secret JSON describing the change.</summary>
    public string? Detail { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    private static string RequireText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A value is required.", parameterName);
        }

        return value.Trim();
    }
}
