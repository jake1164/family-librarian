using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FamilyLibrarian.Domain.ModelGateway;

namespace FamilyLibrarian.Application.ModelTuning;

public sealed record TuningRolePlanScope(ModelRole Role, IReadOnlyList<TuningCase> Cases,
    TuningCalibrationKey Identity, FrozenRoleCalibration? Calibration);
public sealed record TuningOptionMeasurements(decimal? MedianLatencyMilliseconds = null, decimal? TokensPerCompletedCase = null,
    decimal? CostPerCompletedCase = null, string? CostUnit = null);
public sealed record TestedTuningPlanOption(TuningOptionKey Option, TuningPlacement Placement,
    RoleScorecard BuiltInScore, TuningOptionMeasurements Measurements);
public sealed record TuningPlanInput(TuningGoal Goal, IReadOnlyList<ModelRole> EnabledRoles,
    IReadOnlyList<TuningRolePlanScope> Roles, IReadOnlyList<TuningModelKey> Models, IReadOnlyList<TestedTuningPlanOption> TestedOptions,
    IReadOnlyList<AdministratorModelOrder> AdministratorOrder, IReadOnlyList<CloudPreferenceOverride> CloudPreferences,
    IReadOnlyList<OptionCoverage> Coverage, IReadOnlyList<ReportedHouseholdEvidence> HouseholdEvidence);

/// <summary>Pure selection over tested built-in evidence. Does not schedule calls or apply settings.</summary>
public static class TuningPlanSelection
{
    public static string AdministratorOrderIdentity(IReadOnlyList<AdministratorModelOrder> order)
    {
        ArgumentNullException.ThrowIfNull(order);
        var json = JsonSerializer.Serialize(order.OrderBy(r => r.Rank).ToArray(), TuningInputJson.Options);
        return "administrator-order/1:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    public static TuningPlan SelectBestForEachTaskPlan(TuningPlanInput input) => Select(input, TuningPlanKind.BestForEachTask);
    public static TuningPlan SelectFewestModelsPlan(TuningPlanInput input) => Select(input, TuningPlanKind.RecommendedFewestModels);

    public static int CountDistinctLocalModels(TuningPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.Assignments.Where(a => a.Placement == TuningPlacement.Local && a.Adequacy == TuningAdequacy.Adequate)
            .Select(a => a.Option.Model).Distinct().Count();
    }

    private static TuningPlan Select(TuningPlanInput input, TuningPlanKind kind)
    {
        ArgumentNullException.ThrowIfNull(input);
        var prepared = Prepare(input);
        var uncovered = prepared.Roles.Where(r => prepared.Allowed[r].Count == 0).ToArray();
        var status = prepared.Roles.Length == 0 ? TuningPlanStatus.NoRolesSelected :
            uncovered.Length > 0 ? TuningPlanStatus.NoCompletePlan : TuningPlanStatus.Complete;
        var selectedModels = kind == TuningPlanKind.RecommendedFewestModels && status == TuningPlanStatus.Complete
            ? MinimumCover(prepared) : prepared.Models.ToHashSet();
        var assignments = new List<RolePlanOption>();
        foreach (var role in prepared.Roles)
        {
            var allowed = prepared.Allowed[role].Where(o => selectedModels.Contains(o.Option.Model)).ToArray();
            if (allowed.Length == 0) continue;
            var model = allowed.Select(o => o.Option.Model).OrderBy(m => prepared.Ranks[m]).First();
            var chosen = ChooseMode(allowed.Where(o => o.Option.Model == model).ToArray());
            assignments.Add(new(chosen.Option, chosen.Placement, chosen.BuiltInScore.TaskScore!.Value,
                prepared.BestScores[role], prepared.Bands[role], TuningAdequacy.Adequate));
        }
        return new(kind, status, assignments.AsReadOnly(), uncovered,
            status == TuningPlanStatus.Complete ? prepared.Coverage : prepared.Coverage with { CanClaimBest = false },
            input.HouseholdEvidence.ToArray(), prepared.NotAssessable, prepared.Unscored);
    }

    private static Prepared Prepare(TuningPlanInput input)
    {
        if (!Enum.IsDefined(input.Goal) || input.EnabledRoles.Any(r => !Enum.IsDefined(r)) ||
            input.EnabledRoles.Distinct().Count() != input.EnabledRoles.Count || input.Models.Count > TuningPlanRules.MaxModels ||
            input.Models.Distinct().Count() != input.Models.Count || input.Models.Any(m => string.IsNullOrWhiteSpace(m.ConnectionId) || string.IsNullOrWhiteSpace(m.Alias)))
            throw new ArgumentException("Invalid role/model selection scope.", nameof(input));
        var roles = TuningRoles.Scored.Where(input.EnabledRoles.Contains).ToArray();
        if (roles.Length > TuningPlanRules.MaxRoles || roles.Length > 0 && input.Models.Count == 0 ||
            !input.Roles.Select(r => r.Role).Order().SequenceEqual(roles.Order()))
            throw new ArgumentException("Explicit cases must cover each enabled scored role exactly once.", nameof(input));
        if (input.AdministratorOrder.Count != input.Models.Count ||
            input.AdministratorOrder.Select(r => r.Model).Distinct().Count() != input.Models.Count ||
            input.AdministratorOrder.Select(r => r.Rank).Distinct().Count() != input.Models.Count ||
            input.AdministratorOrder.Any(r => r.Rank < 0 || !input.Models.Contains(r.Model)))
            throw new ArgumentException("Administrator order must rank every selected model exactly once.", nameof(input));
        var ranks = input.AdministratorOrder.ToDictionary(r => r.Model, r => r.Rank);
        var models = input.Models.OrderBy(m => ranks[m]).ToArray();
        var scopes = input.Roles.ToDictionary(r => r.Role);
        foreach (var scope in input.Roles)
        {
            if (scope.Cases.Count == 0 || scope.Cases.Any(c => c.Role != scope.Role || c.Source != TuningSource.BuiltIn ||
                c.Coverage != TuningCoverage.ProductionReachable) || scope.Cases.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != scope.Cases.Count ||
                TuningCalibrationKey.ForCases(scope.Identity.CorpusVersion, scope.Identity.LabelVersion, scope.Identity.SchemaVersion, scope.Cases) != scope.Identity)
                throw new ArgumentException("Case/calibration identity mismatch.", nameof(input));
            if (scope.Calibration is { } calibration)
                TuningAdequacyRules.ValidateCalibration(calibration, scope.Identity, TuningScoring.AbstainBaseline(scope.Cases));
        }
        var coverage = roles.Length == 0 ? EmptyCoverage(input) : TuningComparison.Assess(input.Goal, roles, models, input.Coverage);
        var coverageByOption = input.Coverage.ToDictionary(c => c.Option);
        var seen = new HashSet<TuningOptionKey>();
        var placements = new Dictionary<TuningModelKey, TuningPlacement>();
        var adequate = roles.ToDictionary(r => r, _ => new List<TestedTuningPlanOption>());
        foreach (var candidate in input.TestedOptions)
        {
            var key = candidate.Option;
            if (!seen.Add(key) || !scopes.TryGetValue(key.Role, out var scope) || !models.Contains(key.Model) ||
                !Enum.IsDefined(key.Reasoning) || !Enum.IsDefined(candidate.Placement) || !coverageByOption.TryGetValue(key, out var observed) ||
                observed.Evaluation == OptionEvaluation.NotTested || observed.Support != CapabilitySupport.Supported)
                throw new ArgumentException("Invalid, untested or unsupported candidate.", nameof(input));
            if (placements.TryGetValue(key.Model, out var placement) && placement != candidate.Placement)
                throw new ArgumentException("One model identity has inconsistent placements.", nameof(input));
            placements[key.Model] = candidate.Placement;
            ValidateScore(candidate, scope, observed);
            if (scope.Calibration is { IsFrozen: true } && observed.Evaluation == OptionEvaluation.Completed &&
                observed.ComparableIdentity && !observed.ObservedTerminalFailure &&
                TuningAdequacyRules.Assess(candidate.BuiltInScore, scope.Calibration) == TuningAdequacy.Adequate)
                adequate[key.Role].Add(candidate);
        }
        var orderIdentity = AdministratorOrderIdentity(input.AdministratorOrder);
        var preferences = new HashSet<(ModelRole, TuningModelKey)>();
        foreach (var preference in input.CloudPreferences)
        {
            if (!roles.Contains(preference.Role) || !models.Contains(preference.Model) || preference.AdministratorOrderIdentity != orderIdentity ||
                !preferences.Add((preference.Role, preference.Model)) || !placements.TryGetValue(preference.Model, out var placement) || placement == TuningPlacement.Local)
                throw new ArgumentException("Cloud preference must identify a tested cloud model in this administrator order.", nameof(input));
            var locals = adequate[preference.Role].Where(o => o.Placement == TuningPlacement.Local).ToArray();
            if (locals.Any(l => ranks[l.Option.Model] < ranks[preference.Model]))
                throw new ArgumentException("Cloud preference must precede the adequate local alternatives.", nameof(input));
        }
        var allowed = new Dictionary<ModelRole, List<TestedTuningPlanOption>>();
        var bestScores = new Dictionary<ModelRole, decimal>();
        var bands = new Dictionary<ModelRole, decimal>();
        foreach (var role in roles)
        {
            var hasLocal = adequate[role].Any(c => c.Placement == TuningPlacement.Local);
            var admissible = adequate[role].Where(c => !hasLocal || c.Placement == TuningPlacement.Local || preferences.Contains((role, c.Option.Model))).ToArray();
            var best = admissible.Length == 0 ? 0 : admissible.Max(c => c.BuiltInScore.TaskScore!.Value);
            var band = scopes[role].Calibration?.Threshold.NoiseBand ?? 0;
            bestScores[role] = best; bands[role] = band;
            allowed[role] = admissible.Where(c => best - c.BuiltInScore.TaskScore!.Value <= band).ToList();
        }
        foreach (var evidence in input.HouseholdEvidence)
            if (!roles.Contains(evidence.Role) || !models.Contains(evidence.Model) || string.IsNullOrWhiteSpace(evidence.DatasetIdentity) ||
                evidence.KnownOutcomes < 0 || evidence.UnknownOutcomes < 0 || evidence.Score is < 0 or > 1 || evidence.KnownOutcomes == 0 && evidence.Score is not null)
                throw new ArgumentException("Invalid separately reported household evidence.", nameof(input));
        return new(roles, models, ranks, allowed, bestScores, bands, coverage,
            roles.Where(r => scopes[r].Calibration is not { IsFrozen: true }).ToArray(), input.EnabledRoles.Where(r => !TuningRoles.IsScored(r)).Order().ToArray());
    }

    private static void ValidateScore(TestedTuningPlanOption candidate, TuningRolePlanScope scope, OptionCoverage coverage)
    {
        var score = candidate.BuiltInScore;
        if (score.Source != TuningSource.BuiltIn || score.Identity != scope.Identity || score.CaseCount != scope.Cases.Count ||
            score.Repeats is < 1 or > 3 || coverage.Repeats != score.Repeats || score.Correct < 0 || score.Incorrect < 0 || score.Unknown < 0 ||
            score.Correct + score.Incorrect + score.Unknown != score.CaseCount * score.Repeats || score.Validity is < 0 or > 1 || score.Stability is < 0 or > 1 ||
            score.Baseline != TuningScoring.AbstainBaseline(scope.Cases) || score.LabelsApproved != scope.Cases.All(c => c.Review.State == LabelReviewState.Approved) ||
            coverage.TaskScore != score.TaskScore && !(coverage.Evaluation == OptionEvaluation.Disqualified && coverage.TaskScore is null) ||
            coverage.Evaluation == OptionEvaluation.Completed && !score.IsComplete)
            throw new ArgumentException("Candidate built-in evidence or coverage identity differs.", nameof(candidate));
        var m = candidate.Measurements;
        if (m.MedianLatencyMilliseconds < 0 || m.TokensPerCompletedCase < 0 || m.CostPerCompletedCase < 0 ||
            m.CostPerCompletedCase is not null && string.IsNullOrWhiteSpace(m.CostUnit))
            throw new ArgumentException("Invalid option measurements.", nameof(candidate));
    }

    private static ComparisonCoverage EmptyCoverage(TuningPlanInput input)
    {
        if (input.TestedOptions.Count != 0 || input.Coverage.Count != 0 || input.CloudPreferences.Count != 0 || input.HouseholdEvidence.Count != 0)
            throw new ArgumentException("An empty scored scope cannot contain scored evidence.", nameof(input));
        return new(false, [], [], [], []);
    }

    private static TestedTuningPlanOption ChooseMode(TestedTuningPlanOption[] candidates)
    {
        candidates = Minimize(candidates, c => c.Measurements.MedianLatencyMilliseconds);
        candidates = Minimize(candidates, c => c.Measurements.TokensPerCompletedCase);
        if (candidates.All(c => c.Measurements.CostPerCompletedCase is not null) &&
            candidates.Select(c => c.Measurements.CostUnit).Distinct(StringComparer.Ordinal).Count() == 1)
            candidates = Minimize(candidates, c => c.Measurements.CostPerCompletedCase);
        return candidates.OrderBy(c => c.Option.Reasoning).First();
    }

    private static TestedTuningPlanOption[] Minimize(TestedTuningPlanOption[] candidates, Func<TestedTuningPlanOption, decimal?> metric)
    {
        var known = candidates.Where(c => metric(c) is not null).ToArray();
        if (known.Length == 0) return candidates;
        var minimum = known.Min(metric);
        return known.Where(c => metric(c) == minimum).ToArray();
    }

    private static HashSet<TuningModelKey> MinimumCover(Prepared prepared)
    {
        var masks = prepared.Models.Select(model => prepared.Roles.Select((role, bit) =>
            prepared.Allowed[role].Any(c => c.Option.Model == model) ? 1 << bit : 0).Aggregate(0, (a, b) => a | b)).ToArray();
        var target = (1 << prepared.Roles.Length) - 1;
        var best = 0; var bestCount = int.MaxValue;
        for (var subset = 1; subset < 1 << prepared.Models.Length; subset++)
        {
            var count = BitOperations.PopCount((uint)subset);
            if (count > bestCount) continue;
            var covered = 0;
            for (var model = 0; model < masks.Length; model++) if ((subset & 1 << model) != 0) covered |= masks[model];
            if (covered != target || count == bestCount && !EarlierRankVector(subset, best, masks.Length)) continue;
            best = subset; bestCount = count;
        }
        return prepared.Models.Where((_, index) => (best & 1 << index) != 0).ToHashSet();
    }

    private static bool EarlierRankVector(int candidate, int previous, int models)
    {
        for (var index = 0; index < models; index++)
            if ((candidate & 1 << index) != (previous & 1 << index)) return (candidate & 1 << index) != 0;
        return false;
    }

    private sealed record Prepared(ModelRole[] Roles, TuningModelKey[] Models, Dictionary<TuningModelKey, int> Ranks,
        Dictionary<ModelRole, List<TestedTuningPlanOption>> Allowed, Dictionary<ModelRole, decimal> BestScores,
        Dictionary<ModelRole, decimal> Bands, ComparisonCoverage Coverage, ModelRole[] NotAssessable, ModelRole[] Unscored);
}
