namespace FamilyLibrarian.Domain.Delivery;

/// <summary>
/// The allowed <see cref="DeliveryAttempt"/> status transitions, as an explicit
/// matrix -- same pattern as <see cref="Acquisition.AcquisitionJobStatusTransitions"/>.
/// A retry after <see cref="DeliveryAttemptStatus.Failed"/> creates a new row
/// rather than reopening this one; see <see cref="DeliveryAttempt"/>.
/// </summary>
public static class DeliveryAttemptStatusTransitions
{
    public const DeliveryAttemptStatus InitialStatus = DeliveryAttemptStatus.Pending;

    private static readonly Dictionary<DeliveryAttemptStatus, DeliveryAttemptStatus[]> Allowed = new()
    {
        [DeliveryAttemptStatus.Pending] =
        [
            DeliveryAttemptStatus.Submitting,
            DeliveryAttemptStatus.Cancelled
        ],
        [DeliveryAttemptStatus.Submitting] =
        [
            DeliveryAttemptStatus.Submitted,
            DeliveryAttemptStatus.Failed,
            DeliveryAttemptStatus.SubmissionUnknown
        ]
        // Submitted, Failed, SubmissionUnknown and Cancelled are terminal for this row.
    };

    public static bool IsAllowed(DeliveryAttemptStatus from, DeliveryAttemptStatus to) =>
        Allowed.TryGetValue(from, out var targets) && Array.IndexOf(targets, to) >= 0;

    public static IReadOnlyList<DeliveryAttemptStatus> AllowedFrom(DeliveryAttemptStatus from) =>
        Allowed.TryGetValue(from, out var targets) ? targets : [];
}

public sealed class InvalidDeliveryAttemptTransitionException(DeliveryAttemptStatus from, DeliveryAttemptStatus to)
    : InvalidOperationException($"A delivery attempt cannot move from {from} to {to}.")
{
    public DeliveryAttemptStatus From { get; } = from;

    public DeliveryAttemptStatus To { get; } = to;
}
