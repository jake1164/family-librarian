namespace FamilyLibrarian.Domain.ModelGateway;

/// <summary>
/// How much decision history the installation keeps for later replay (A6-TUNER-C1). Defaults are the owner's recommendations;
/// the ranges are fixed ceilings that no request may exceed. Storage is always capped: there is no "keep everything" value.
/// </summary>
/// <param name="SnapshotsPerRole">Snapshots kept per scored role.</param>
/// <param name="UnlabeledSnapshotDays">Snapshots with no observed outcome expire this many days after they were last seen.</param>
/// <param name="LabeledSnapshotDays">Snapshots with an observed outcome expire after this many days; null keeps them until the cap or a purge.</param>
/// <param name="CallsPerSnapshot">The most recent calls kept for one snapshot.</param>
/// <param name="FindingsDays">How long a semantic finding is kept.</param>
public sealed record DecisionCaptureLimits(
    int SnapshotsPerRole, int UnlabeledSnapshotDays, int? LabeledSnapshotDays, int CallsPerSnapshot, int FindingsDays)
{
    public const int DefaultSnapshotsPerRole = 200;
    public const int MinSnapshotsPerRole = 50;
    public const int MaxSnapshotsPerRole = 1_000;

    public const int DefaultUnlabeledSnapshotDays = 180;
    public const int MinUnlabeledSnapshotDays = 30;
    public const int MaxUnlabeledSnapshotDays = 730;

    public const int MinLabeledSnapshotDays = 90;
    public const int MaxLabeledSnapshotDays = 1_825;

    public const int DefaultCallsPerSnapshot = 10;
    public const int MinCallsPerSnapshot = 1;
    public const int MaxCallsPerSnapshot = 20;

    public const int DefaultFindingsDays = 180;
    /// <summary>Keeps the 7-day approval reuse window and the admin displays working.</summary>
    public const int MinFindingsDays = 30;
    public const int MaxFindingsDays = 730;

    /// <summary>A safety bound, not a setting: a larger snapshot is not stored and is counted instead.</summary>
    public const int MaxSnapshotBytes = 32 * 1024;

    public static DecisionCaptureLimits Default { get; } = new(
        DefaultSnapshotsPerRole, DefaultUnlabeledSnapshotDays, null, DefaultCallsPerSnapshot, DefaultFindingsDays);

    /// <summary>The first problem with these values, or null when every one is inside its allowed range.</summary>
    public string? Validate()
    {
        if (SnapshotsPerRole is < MinSnapshotsPerRole or > MaxSnapshotsPerRole)
        {
            return $"Snapshots kept per feature must be between {MinSnapshotsPerRole} and {MaxSnapshotsPerRole:N0}.";
        }

        if (UnlabeledSnapshotDays is < MinUnlabeledSnapshotDays or > MaxUnlabeledSnapshotDays)
        {
            return $"Snapshots without a known outcome must expire after {MinUnlabeledSnapshotDays} to {MaxUnlabeledSnapshotDays} days.";
        }

        if (LabeledSnapshotDays is { } labeled && labeled is < MinLabeledSnapshotDays or > MaxLabeledSnapshotDays)
        {
            return $"Snapshots with a known outcome must be kept {MinLabeledSnapshotDays} to {MaxLabeledSnapshotDays:N0} days, or until the cap.";
        }

        if (CallsPerSnapshot is < MinCallsPerSnapshot or > MaxCallsPerSnapshot)
        {
            return $"Calls kept per snapshot must be between {MinCallsPerSnapshot} and {MaxCallsPerSnapshot}.";
        }

        if (FindingsDays is < MinFindingsDays or > MaxFindingsDays)
        {
            return $"Findings must be kept {MinFindingsDays} to {MaxFindingsDays} days.";
        }

        return null;
    }
}
