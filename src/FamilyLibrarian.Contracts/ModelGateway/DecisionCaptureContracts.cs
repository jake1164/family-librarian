namespace FamilyLibrarian.Contracts.ModelGateway;

/// <summary>How much decision history is kept. <paramref name="LabeledSnapshotDays"/> null means kept until the cap or a purge.</summary>
public sealed record DecisionCaptureLimitsDto(
    int SnapshotsPerRole, int UnlabeledSnapshotDays, int? LabeledSnapshotDays, int CallsPerSnapshot, int FindingsDays);

/// <summary>The fixed ceilings the server enforces, so the page can show them. The size guard is not adjustable.</summary>
public sealed record DecisionCaptureRangesDto(
    int MinSnapshotsPerRole, int MaxSnapshotsPerRole,
    int MinUnlabeledSnapshotDays, int MaxUnlabeledSnapshotDays,
    int MinLabeledSnapshotDays, int MaxLabeledSnapshotDays,
    int MinCallsPerSnapshot, int MaxCallsPerSnapshot,
    int MinFindingsDays, int MaxFindingsDays,
    int SnapshotSizeGuardBytes);

public sealed record DecisionCaptureSettingsResponse(
    bool DecisionCaptureEnabled, bool ChatCaptureEnabled, DecisionCaptureLimitsDto Limits,
    DecisionCaptureRangesDto Ranges, DecisionCaptureLimitsDto Defaults);

public sealed record SaveDecisionCaptureSettingsRequest(bool DecisionCaptureEnabled, bool ChatCaptureEnabled, DecisionCaptureLimitsDto? Limits);

/// <summary>How many records the next retention pass would remove under the limits asked about. Counts only.</summary>
public sealed record DecisionCaptureRetentionPreviewResponse(int Snapshots, int Calls, int Findings, int Total);

/// <param name="Role">One captured role name, or null / <c>All</c> for every role.</param>
public sealed record PurgeDecisionCaptureRequest(string? Role);

public sealed record DecisionCapturePurgeResponse(int Snapshots, int Calls, int Outcomes, int CandidateLinks);

/// <summary>Numbers only: there is no title, author, message or snapshot content anywhere in this response.</summary>
public sealed record DecisionCaptureRoleInsightDto(
    string Role, int Snapshots, int WithOutcomes, int WithoutOutcomes, DateTimeOffset? Oldest, DateTimeOffset? Newest, long ApproximateBytes);

public sealed record DecisionCaptureTableInsightDto(string Table, long Rows, long ApproximateBytes, DateTimeOffset? Oldest);

/// <param name="SizesAvailable">False when the database would not report sizes; the counts are still correct. Sizes are always approximate.</param>
public sealed record DecisionCaptureInsightResponse(
    DateTimeOffset AsOfUtc, IReadOnlyList<DecisionCaptureRoleInsightDto> Roles, IReadOnlyList<DecisionCaptureTableInsightDto> Tables,
    long TotalApproximateBytes, bool SizesAvailable);
