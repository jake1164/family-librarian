using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FamilyLibrarian.Application.ModelTuning;

public sealed record TuningCalibrationKey(string CorpusVersion, string LabelVersion, int SchemaVersion,
    string PromptParserRulesScorerIdentity)
{
    public static TuningCalibrationKey ForCases(string corpusVersion, string labelVersion, int schemaVersion, IReadOnlyList<TuningCase> cases)
    {
        ArgumentNullException.ThrowIfNull(cases);
        if (string.IsNullOrWhiteSpace(corpusVersion) || string.IsNullOrWhiteSpace(labelVersion) || schemaVersion != 1 ||
            cases.Count == 0 || cases.Any(c => c.Source != TuningSource.BuiltIn) || cases.Select(c => c.Role).Distinct().Count() != 1)
            throw new ArgumentException("Calibration identity requires one built-in role and versioned cases.", nameof(cases));
        var role = cases[0].Role;
        var prompts = cases.OrderBy(c => c.Id, StringComparer.Ordinal)
            .Select(c => new { c.Id, PromptIdentity = TuningCaseRenderer.Render(c).PromptIdentity }).ToArray();
        var identity = JsonSerializer.Serialize(new { contract = TuningContractIdentity.ForRole(role), role, prompts }, TuningInputJson.Options);
        return new(corpusVersion, labelVersion, schemaVersion, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity))));
    }
}
public sealed record CaseObservation(TuningCaseIdentity Case, int Repeat, CaseScore Result);
public sealed record RoleScorecard(TuningSource Source, TuningCalibrationKey Identity, int CaseCount, int Repeats,
    int Correct, int Incorrect, int Unknown, decimal Validity, decimal? Stability, decimal Baseline,
    bool IsComplete, bool LabelsApproved, bool HasDisqualifier, decimal? ActionRecall,
    decimal? HoldPrecision, decimal? HoldRecall, IReadOnlyDictionary<string, int> Confusion)
{
    public decimal? TaskScore => Correct + Incorrect == 0 ? null : Correct / (decimal)(Correct + Incorrect);
}

public static class TuningMetrics
{
    public static RoleScorecard Summarize(IReadOnlyList<TuningCase> cases, IReadOnlyList<CaseObservation> observations,
        int repeats, TuningCalibrationKey identity, bool observedRefusalOrCutoff = false)
    {
        ArgumentNullException.ThrowIfNull(cases);
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(identity);
        if (repeats is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(repeats));
        if (cases.Count == 0 || cases.Any(c => c.Source != TuningSource.BuiltIn || c.Coverage != TuningCoverage.ProductionReachable) ||
            cases.Select(c => c.Role).Distinct().Count() != 1)
            throw new ArgumentException("S1 summarizes one built-in role set only.", nameof(cases));
        var byCase = cases.ToDictionary(TuningCaseIdentity.From);
        var cells = new Dictionary<(TuningCaseIdentity, int), CaseScore>();
        foreach (var observation in observations)
            if (!byCase.ContainsKey(observation.Case) || observation.Repeat < 0 || observation.Repeat >= repeats ||
                !cells.TryAdd((observation.Case, observation.Repeat), observation.Result))
                throw new ArgumentException("Unknown, duplicate or out-of-range observation.", nameof(observations));
        var planned = cases.Count * repeats;
        var correct = cells.Values.Count(r => r.Score == TuningScore.Correct);
        var unknown = cells.Values.Count(r => r.Score == TuningScore.Unknown);
        var agree = 0; var pairs = 0; var actionTotal = 0; var actionCorrect = 0;
        var holds = 0; var trueHolds = 0; var expectedHolds = 0;
        var confusion = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (key, item) in byCase)
        {
            for (var repeat = 0; repeat < repeats; repeat++)
            {
                cells.TryGetValue((key, repeat), out var result);
                var action = TuningScoring.RequiresNonAbstention(item);
                if (action) { actionTotal++; if (result?.Score == TuningScore.Correct) actionCorrect++; }
                var expectedHold = item.Expected.Acceptable.All(a => a.Verdict == "hold");
                if (expectedHold) expectedHolds++;
                if (result?.Answer?.Verdict == "hold") { holds++; if (expectedHold) trueHolds++; }
                var label = string.Join('/', item.Expected.Acceptable.Select(a => a.Verdict ?? a.Move ?? a.Intent ?? (a.GiveUp == true ? "give_up" : "search")).Order(StringComparer.Ordinal));
                var actual = result?.Answer is { } answer ? answer.Verdict ?? answer.Move ?? answer.Intent ?? (answer.GiveUp == true ? "give_up" : "search") : "failed_or_missing";
                var cell = label + " -> " + actual;
                confusion[cell] = confusion.GetValueOrDefault(cell) + 1;
                for (var other = repeat + 1; other < repeats; other++)
                {
                    pairs++;
                    if (result is { IsValid: true, Answer: not null } && cells.TryGetValue((key, other), out var otherResult) &&
                        otherResult is { IsValid: true, Answer: not null } && TuningScoring.SemanticKey(result.Answer) == TuningScoring.SemanticKey(otherResult.Answer)) agree++;
                }
            }
        }
        return new(TuningSource.BuiltIn, identity, cases.Count, repeats, correct, planned - correct - unknown, unknown,
            cells.Values.Count(r => r.IsValid) / (decimal)planned, pairs == 0 ? null : agree / (decimal)pairs,
            TuningScoring.AbstainBaseline(cases), cells.Count == planned, cases.All(c => c.Review.State == LabelReviewState.Approved),
            observedRefusalOrCutoff || cells.Values.Any(r => r.SafetyViolation), actionTotal == 0 ? null : actionCorrect / (decimal)actionTotal,
            holds == 0 ? null : trueHolds / (decimal)holds, expectedHolds == 0 ? null : trueHolds / (decimal)expectedHolds, confusion);
    }
}

public enum TuningAdequacy { NotApplicable, Disqualified, Incomplete, Provisional, BelowThreshold, Adequate }
public sealed record RoleThreshold(decimal AbsoluteFloor, decimal BaselineMargin, decimal NoiseBand);
public enum TuningNoiseBasis { Unspecified, Prior }
public sealed record FrozenRoleCalibration(TuningCalibrationKey Identity, RoleThreshold Threshold, bool IsFrozen, int MinimumCases = 40,
    decimal MinimumValidity = .95m, decimal MinimumAgreement = .90m,
    TuningNoiseBasis NoiseBasis = TuningNoiseBasis.Unspecified, decimal? MeasuredRepeatRange = null);

public static class TuningAdequacyRules
{
    public static TuningAdequacy Assess(RoleScorecard score, FrozenRoleCalibration? calibration)
    {
        ArgumentNullException.ThrowIfNull(score);
        if (score.Source != TuningSource.BuiltIn) return TuningAdequacy.NotApplicable;
        if (score.HasDisqualifier || score.Unknown != 0) return TuningAdequacy.Disqualified;
        if (!score.IsComplete || !score.LabelsApproved) return TuningAdequacy.Incomplete;
        if (calibration is null || !calibration.IsFrozen || score.Repeats < 2 || score.Stability is null) return TuningAdequacy.Provisional;
        ValidateCalibration(calibration, score.Identity, score.Baseline);
        if (score.CaseCount < calibration.MinimumCases) return TuningAdequacy.Incomplete;
        var t = calibration.Threshold;
        return score.Validity >= calibration.MinimumValidity && score.Stability >= calibration.MinimumAgreement && score.TaskScore >= t.AbsoluteFloor &&
            score.TaskScore >= score.Baseline + t.BaselineMargin ? TuningAdequacy.Adequate : TuningAdequacy.BelowThreshold;
    }

    internal static void ValidateCalibration(FrozenRoleCalibration calibration, TuningCalibrationKey identity, decimal baseline)
    {
        if (calibration.MinimumCases < 40) throw new ArgumentException("Calibration sample size is too small.", nameof(calibration));
        if (calibration.Identity != identity) throw new ArgumentException("Built-in calibration identity differs.", nameof(calibration));
        var t = calibration.Threshold;
        if (t.AbsoluteFloor is < 0 or > 1 || t.BaselineMargin is <= 0 or > 1 || t.NoiseBand is < 0 or > 1 ||
            baseline + t.BaselineMargin > 1 || calibration.MinimumValidity is < .95m or > 1 ||
            calibration.MinimumAgreement is < .90m or > 1 || !Enum.IsDefined(calibration.NoiseBasis) ||
            calibration.MeasuredRepeatRange is < 0 or > 1) throw new ArgumentException("Invalid calibrated thresholds.", nameof(calibration));
    }
}
