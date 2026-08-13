namespace FamilyLibrarian.Domain.Requests;

/// <summary>
/// The allowed request status transitions, as an explicit matrix.
/// </summary>
/// <remarks>
/// Status is changed by application commands, never by an arbitrary UI edit, so
/// the rule lives here rather than in an endpoint or a component. Who may make
/// an allowed transition is a separate question answered by the application
/// layer: this type only says which moves exist.
/// </remarks>
public static class RequestStatusTransitions
{
    public const RequestStatus InitialStatus = RequestStatus.PendingAcquisition;

    private static readonly Dictionary<RequestStatus, RequestStatus[]> Allowed = new()
    {
        [RequestStatus.PendingAcquisition] =
        [
            RequestStatus.NeedsReview,
            RequestStatus.NotAvailable,
            RequestStatus.Cancelled
        ],
        [RequestStatus.NeedsReview] =
        [
            RequestStatus.PendingAcquisition,
            RequestStatus.NotAvailable,
            RequestStatus.Cancelled
        ],
        // Reopening returns a request to the queue rather than resurrecting the
        // status it held before it was closed.
        [RequestStatus.NotAvailable] = [RequestStatus.PendingAcquisition],
        [RequestStatus.Cancelled] = [RequestStatus.PendingAcquisition]
    };

    /// <summary>
    /// Statuses that still represent an outstanding request. Duplicate detection
    /// and the admin queue both key off this set.
    /// </summary>
    public static bool IsActive(RequestStatus status) =>
        status is RequestStatus.PendingAcquisition or RequestStatus.NeedsReview;

    public static bool IsAllowed(RequestStatus from, RequestStatus to) =>
        Allowed.TryGetValue(from, out var targets) &&
        Array.IndexOf(targets, to) >= 0;

    public static IReadOnlyList<RequestStatus> AllowedFrom(RequestStatus from) =>
        Allowed.TryGetValue(from, out var targets) ? targets : [];
}

public sealed class InvalidRequestTransitionException(RequestStatus from, RequestStatus to)
    : InvalidOperationException($"A request cannot move from {from} to {to}.")
{
    public RequestStatus From { get; } = from;

    public RequestStatus To { get; } = to;
}
