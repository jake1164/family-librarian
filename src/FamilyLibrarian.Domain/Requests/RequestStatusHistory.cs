namespace FamilyLibrarian.Domain.Requests;

/// <summary>
/// One recorded status change on a <see cref="BookRequest"/>.
/// </summary>
/// <remarks>
/// A small, purposeful audit trail: it makes "why is my request in this state"
/// answerable, and it is written in the same transaction as the change it
/// describes. A null <see cref="FromStatus"/> marks the creation row.
/// </remarks>
public sealed class RequestStatusHistory
{
    private RequestStatusHistory()
    {
    }

    internal RequestStatusHistory(
        Guid requestId,
        RequestStatus? fromStatus,
        RequestStatus toStatus,
        Guid? actorUserId,
        string? reason,
        DateTimeOffset occurredAtUtc)
    {
        RequestId = requestId;
        FromStatus = fromStatus;
        ToStatus = toStatus;
        ActorUserId = actorUserId;
        Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        OccurredAtUtc = occurredAtUtc;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public Guid RequestId { get; private set; }

    public BookRequest Request { get; private set; } = null!;

    public RequestStatus? FromStatus { get; private set; }

    public RequestStatus ToStatus { get; private set; }

    /// <summary>The acting user, or <see langword="null"/> for a system action.</summary>
    public Guid? ActorUserId { get; private set; }

    public string? Reason { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }
}
