namespace FamilyLibrarian.Domain.Acquisition;

/// <summary>
/// The allowed <see cref="AcquisitionJob"/> status transitions, as an explicit
/// matrix. M9's manual-import path only drives Created -> CandidateAcquired and
/// Created -> Failed; the wider matrix is declared now so M10/M11 (security
/// waiting, private egress, retries) do not need another migration to widen it.
/// </summary>
public static class AcquisitionJobStatusTransitions
{
    public const AcquisitionJobStatus InitialStatus = AcquisitionJobStatus.Created;

    private static readonly Dictionary<AcquisitionJobStatus, AcquisitionJobStatus[]> Allowed = new()
    {
        [AcquisitionJobStatus.Created] =
        [
            AcquisitionJobStatus.InProgress,
            AcquisitionJobStatus.CandidateAcquired,
            AcquisitionJobStatus.WaitingForSecurityScanner,
            AcquisitionJobStatus.WaitingForPrivateEgress,
            AcquisitionJobStatus.Failed,
            AcquisitionJobStatus.Cancelled
        ],
        [AcquisitionJobStatus.InProgress] =
        [
            AcquisitionJobStatus.CandidateAcquired,
            AcquisitionJobStatus.WaitingForSecurityScanner,
            AcquisitionJobStatus.WaitingForPrivateEgress,
            AcquisitionJobStatus.Failed,
            AcquisitionJobStatus.Cancelled
        ],
        [AcquisitionJobStatus.WaitingForSecurityScanner] =
        [
            AcquisitionJobStatus.InProgress,
            AcquisitionJobStatus.Cancelled
        ],
        [AcquisitionJobStatus.WaitingForPrivateEgress] =
        [
            AcquisitionJobStatus.InProgress,
            AcquisitionJobStatus.Cancelled
        ]
        // CandidateAcquired, Failed, and Cancelled are terminal.
    };

    public static bool IsAllowed(AcquisitionJobStatus from, AcquisitionJobStatus to) =>
        Allowed.TryGetValue(from, out var targets) && Array.IndexOf(targets, to) >= 0;

    public static IReadOnlyList<AcquisitionJobStatus> AllowedFrom(AcquisitionJobStatus from) =>
        Allowed.TryGetValue(from, out var targets) ? targets : [];
}

public sealed class InvalidAcquisitionJobTransitionException(AcquisitionJobStatus from, AcquisitionJobStatus to)
    : InvalidOperationException($"An acquisition job cannot move from {from} to {to}.")
{
    public AcquisitionJobStatus From { get; } = from;

    public AcquisitionJobStatus To { get; } = to;
}
