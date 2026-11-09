using FamilyLibrarian.Domain.ModelGateway;

namespace FamilyLibrarian.Application.ModelTuning;

public enum TuningGoal { GoodQuickly, Best }
public enum TuningTier { Quick, Standard, Thorough }
public enum CapabilitySupport { Unknown, Supported, Unsupported }
public enum OptionEvaluation { NotTested, Partial, Completed, Disqualified }
public enum OptionOmission { DefaultSufficient, EarlyStop, BudgetOrTime, Cancelled, NotSelectedForEvaluation }
public sealed record TuningOptionKey(ModelRole Role, TuningModelKey Model, ModelReasoningMode Reasoning);
public sealed record OptionCoverage(TuningOptionKey Option, CapabilitySupport Support, OptionEvaluation Evaluation,
    OptionOmission? Omission, decimal? TaskScore, bool ObservedTerminalFailure, bool ComparableIdentity, int Repeats);
public sealed record ComparisonCoverage(bool CanClaimBest, IReadOnlyList<TuningOptionKey> Untested,
    IReadOnlyList<TuningOptionKey> Partial, IReadOnlyList<TuningOptionKey> UnknownCapability,
    IReadOnlyList<TuningOptionKey> Unsupported);

public static class TuningComparison
{
    public static int Repeats(TuningTier tier) => tier switch
    { TuningTier.Quick => 1, TuningTier.Standard => 2, TuningTier.Thorough => 3, _ => throw new ArgumentOutOfRangeException(nameof(tier)) };
    public static TuningTier Tier(TuningGoal goal) => goal switch
    { TuningGoal.GoodQuickly => TuningTier.Standard, TuningGoal.Best => TuningTier.Thorough, _ => throw new ArgumentOutOfRangeException(nameof(goal)) };

    /// <summary>Reports observed coverage only. Never decides the next call or schedules work.</summary>
    public static ComparisonCoverage Assess(TuningGoal goal, IReadOnlyList<ModelRole> roles, IReadOnlyList<TuningModelKey> models,
        IReadOnlyList<OptionCoverage> observed)
    {
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(observed);
        if (models.Count is < 1 or > TuningPlanRules.MaxModels || roles.Count is < 1 or > TuningPlanRules.MaxRoles ||
            models.Distinct().Count() != models.Count || roles.Distinct().Count() != roles.Count || roles.Any(r => !TuningRoles.IsScored(r)))
            throw new ArgumentException("Invalid comparison scope.");
        var rows = new Dictionary<TuningOptionKey, OptionCoverage>();
        foreach (var row in observed)
        {
            if (!roles.Contains(row.Option.Role) || !models.Contains(row.Option.Model) || !Enum.IsDefined(row.Option.Reasoning) ||
                !Enum.IsDefined(row.Support) || !Enum.IsDefined(row.Evaluation) || !rows.TryAdd(row.Option, row))
                throw new ArgumentException("Invalid or duplicate option coverage.", nameof(observed));
            if (row.Evaluation == OptionEvaluation.NotTested && (row.TaskScore is not null || row.ObservedTerminalFailure) ||
                row.Evaluation == OptionEvaluation.Disqualified && !row.ObservedTerminalFailure ||
                row.Support == CapabilitySupport.Unsupported && row.TaskScore is not null)
                throw new ArgumentException("Coverage cannot fabricate an option score or failure.", nameof(observed));
        }
        var untested = new List<TuningOptionKey>(); var partial = new List<TuningOptionKey>();
        var unknown = new List<TuningOptionKey>(); var unsupported = new List<TuningOptionKey>();
        foreach (var role in roles.Order())
        foreach (var model in models.OrderBy(m => m.ConnectionId, StringComparer.Ordinal).ThenBy(m => m.Alias, StringComparer.Ordinal))
        foreach (var reasoning in Enum.GetValues<ModelReasoningMode>())
        {
            var key = new TuningOptionKey(role, model, reasoning);
            if (!rows.TryGetValue(key, out var row) || row.Support == CapabilitySupport.Unknown) { unknown.Add(key); continue; }
            if (row.Support == CapabilitySupport.Unsupported) { unsupported.Add(key); continue; }
            if (row.Evaluation == OptionEvaluation.NotTested) untested.Add(key);
            else if (row.Evaluation != OptionEvaluation.Disqualified &&
                (row.Evaluation != OptionEvaluation.Completed || !row.ComparableIdentity || row.Repeats != Repeats(Tier(goal)) || row.TaskScore is null)) partial.Add(key);
        }
        return new(goal == TuningGoal.Best && unknown.Count == 0 && untested.Count == 0 && partial.Count == 0,
            untested, partial, unknown, unsupported);
    }
}

public static class TuningPlanRules
{
    public const int MaxModels = 16;
    public const int MaxRoles = 6;
}
public enum TuningPlanKind { RecommendedFewestModels, BestForEachTask }
public enum TuningPlanStatus { Complete, NoCompletePlan, NoRolesSelected }
public sealed record AdministratorModelOrder(TuningModelKey Model, int Rank);
public sealed record CloudPreferenceOverride(ModelRole Role, TuningModelKey Model, string AdministratorOrderIdentity);
public sealed record RolePlanOption(TuningOptionKey Option, TuningPlacement Placement, decimal Score,
    decimal BestAdmissibleScore, decimal FrozenNoiseBand, TuningAdequacy Adequacy)
{
    public decimal ScoreGap => BestAdmissibleScore - Score;
}
public sealed record ReportedHouseholdEvidence(ModelRole Role, TuningModelKey Model, decimal? Score,
    int KnownOutcomes, int UnknownOutcomes, string DatasetIdentity);
public sealed record TuningPlan(TuningPlanKind Kind, TuningPlanStatus Status, IReadOnlyList<RolePlanOption> Assignments,
    IReadOnlyList<ModelRole> UncoveredRoles, ComparisonCoverage Coverage, IReadOnlyList<ReportedHouseholdEvidence> HouseholdEvidence,
    IReadOnlyList<ModelRole> NotAssessableRoles, IReadOnlyList<ModelRole> UnscoredRoles);
