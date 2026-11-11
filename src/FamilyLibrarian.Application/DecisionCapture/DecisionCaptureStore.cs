using FamilyLibrarian.Domain.ModelGateway;

namespace FamilyLibrarian.Application.DecisionCapture;

/// <summary>The six roles whose decisions can be captured. Cover identification is vision and the release call is not a tuner role.</summary>
public static class DecisionCaptureRoles
{
    public static IReadOnlyList<ModelRole> Scored { get; } =
    [
        ModelRole.ArtifactValidationAssessment,
        ModelRole.ArtifactValidationDecision,
        ModelRole.CandidateAmbiguityAssessment,
        ModelRole.CandidateTriage,
        ModelRole.SearchGuidance,
        ModelRole.IntentInterpretation
    ];
}

/// <summary>Counts and approximate sizes for one captured role. Numbers only; never titles, messages or snapshot content.</summary>
public sealed record DecisionCaptureRoleInsight(
    ModelRole Role, int Snapshots, int WithOutcomes, int WithoutOutcomes, DateTimeOffset? Oldest, DateTimeOffset? Newest, long ApproximateBytes);

public sealed record DecisionCaptureTableInsight(string Table, long Rows, long ApproximateBytes, DateTimeOffset? Oldest);

/// <summary>What is stored and what takes the space. Sizes are database estimates (including indexes), so they are approximate.</summary>
public sealed record DecisionCaptureInsight(
    DateTimeOffset AsOfUtc,
    IReadOnlyList<DecisionCaptureRoleInsight> Roles,
    IReadOnlyList<DecisionCaptureTableInsight> Tables,
    long TotalApproximateBytes,
    bool SizesAvailable = true);

/// <summary>How many records the next retention pass would remove under some limits. Counts only.</summary>
public sealed record DecisionCaptureRetentionCounts(int Snapshots, int Calls, int Findings)
{
    public int Total => Snapshots + Calls + Findings;
}

/// <summary>What a purge removed. Counts only, so it is safe to audit.</summary>
public sealed record DecisionCapturePurgeResult(int Snapshots, int Calls, int Outcomes, int CandidateLinks, int UsageRollups = 0);

/// <summary>
/// When a model's call changed something, what was observed afterwards, per role. Decision counts, not a score: facts are partly
/// influenced by the model's own note, and most decisions never get one.
/// </summary>
public sealed record DecisionCaptureEffectivenessRow(
    ModelRole Role, int ActedCalls, int WithFacts, int PositiveOnly, int NegativeOnly, int Mixed, int InfluencedByNote);

/// <summary>
/// Storage for decision capture. C1-a needs only the administration side: insight, retention and purge. Writing snapshots arrives
/// with C1-b. Implementations must never return snapshot content.
/// </summary>
public interface IDecisionCaptureStore
{
    Task<DecisionCaptureInsight> GetInsightAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken);

    /// <summary>What <see cref="RunRetentionAsync"/> would remove under these limits right now, without removing anything.</summary>
    Task<DecisionCaptureRetentionCounts> CountRetentionAsync(DecisionCaptureLimits limits, DateTimeOffset nowUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Removes expired and over-cap records. Order: snapshots without an outcome that have expired, labeled snapshots past an optional
    /// age, then each role down to its cap (no outcome first, then weak outcomes only, then strong; oldest first within a group), then
    /// calls beyond the per-snapshot cap, then old findings. Cascades take each snapshot's calls, outcomes and candidate links.
    /// </summary>
    Task<DecisionCaptureRetentionCounts> RunRetentionAsync(DecisionCaptureLimits limits, DateTimeOffset nowUtc, CancellationToken cancellationToken);

    /// <summary>Per role, for calls since <paramref name="sinceUtc"/> that changed something: how many decisions later had facts, and of what kind.</summary>
    Task<IReadOnlyList<DecisionCaptureEffectivenessRow>> GetEffectivenessAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken);

    /// <summary>Deletes snapshots (and, by cascade, their calls, outcomes and links) for one role, or for every role when null. Purging everything also clears the weekly usage counts.</summary>
    Task<DecisionCapturePurgeResult> PurgeAsync(ModelRole? role, CancellationToken cancellationToken);
}
