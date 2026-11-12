using System.Text.Json;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Domain.ModelGateway;

namespace FamilyLibrarian.Application.ModelTuning;

public sealed record TuningCompletion(ModelCallOutcome Outcome, string? Content, bool IsOutputLimited = false);
public sealed record CaseScore(TuningScore Score, bool IsValid, bool SafetyViolation, TuningAnswer? Answer);

public sealed record BaselineScore(decimal? Score, int Correct, int Incorrect, int Unknown);

public static class TuningScoring
{
    /// <param name="outcomeKnown">Trusted outcome availability supplied by a future replay caller; never model self-report.</param>
    public static CaseScore Score(TuningCase item, RenderedTuningCase rendered, TuningCompletion completion, bool outcomeKnown = true)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(rendered);
        ArgumentNullException.ThrowIfNull(completion);
        if (completion.Outcome != ModelCallOutcome.Succeeded || completion.IsOutputLimited)
            return new(TuningScore.Incorrect, false, false, null);
        var parsed = rendered.Parse(completion.Content);
        if (!parsed.IsValid || parsed.Answer is null) return new(TuningScore.Incorrect, false, false, null);
        if (parsed.AcceptedSafetyViolation) return new(TuningScore.Incorrect, true, true, parsed.Answer);
        if (!outcomeKnown)
        {
            if (item.Source != TuningSource.Household) throw new ArgumentException("Built-in outcomes must be known.", nameof(outcomeKnown));
            return new(TuningScore.Unknown, true, false, parsed.Answer);
        }
        var evidence = item.Expected.RequiredEvidenceAlternatives;
        var grounded = evidence.Count == 0 || evidence.Any(ids => ids.All(parsed.EvidenceIds.Contains));
        var correct = grounded && item.Expected.Acceptable.Any(expected => Matches(item.Role, expected, parsed.Answer));
        return new(correct ? TuningScore.Correct : TuningScore.Incorrect, true, false, parsed.Answer);
    }

    private static bool Matches(ModelRole role, TuningAnswer expected, TuningAnswer actual) => role switch
    {
        ModelRole.ArtifactValidationAssessment or ModelRole.ArtifactValidationDecision or ModelRole.CandidateAmbiguityAssessment =>
            expected.Verdict == actual.Verdict,
        ModelRole.CandidateTriage => expected.Move == actual.Move && expected.TargetKey == actual.TargetKey,
        ModelRole.IntentInterpretation => expected.Intent == actual.Intent && SamePlain(expected.Title, actual.Title) && SamePlain(expected.Author, actual.Author),
        ModelRole.SearchGuidance => MatchesSearch(expected, actual),
        _ => throw new ArgumentException("Role is not scored.", nameof(role))
    };

    private static bool SamePlain(string? left, string? right) => left is null || right is null ? left == right :
        string.Equals(Plain(left), Plain(right), StringComparison.OrdinalIgnoreCase);
    private static string Plain(string value) => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static bool SameName(TuningSearchName left, TuningSearchName right) => left.Reason == right.Reason &&
        SearchVariantPlanner.Normalize(left.Value) == SearchVariantPlanner.Normalize(right.Value);
    private static bool MatchesSearch(TuningAnswer expected, TuningAnswer actual)
    {
        var suggestions = actual.Suggestions ?? [];
        if (expected.GiveUp != actual.GiveUp) return false;
        if (actual.GiveUp == true) return suggestions.Count == 0;
        var allowed = expected.AllowedSuggestions ?? [];
        return suggestions.Count > 0 && suggestions.All(s => allowed.Any(a => SameName(s, a))) &&
            (expected.RequiredSuggestions ?? []).All(r => suggestions.Any(s => SameName(s, r)));
    }

    public static string SemanticKey(TuningAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        return JsonSerializer.Serialize(answer with
        {
            Title = answer.Title is null ? null : Plain(answer.Title).ToUpperInvariant(),
            Author = answer.Author is null ? null : Plain(answer.Author).ToUpperInvariant(),
            Suggestions = answer.Suggestions?.Select(s => s with { Value = SearchVariantPlanner.Normalize(s.Value) })
                .OrderBy(s => s.Reason, StringComparer.Ordinal).ThenBy(s => s.Value, StringComparer.Ordinal).ToArray()
        }, TuningInputJson.Options);
    }

    public static bool RequiresNonAbstention(TuningCase item) => item.Expected.Acceptable.All(answer => item.Role switch
    {
        ModelRole.ArtifactValidationAssessment => answer.Verdict == "inconsistent",
        ModelRole.CandidateAmbiguityAssessment => answer.Verdict is "consistent" or "inconsistent",
        ModelRole.ArtifactValidationDecision => answer.Verdict == "hold",
        ModelRole.CandidateTriage => answer.Move is "prefer" or "veto" or "author_variant",
        ModelRole.SearchGuidance => answer.GiveUp == false,
        ModelRole.IntentInterpretation => answer.Intent == "find_book",
        _ => false
    });

    /// <summary>Best constant abstention strategy, never a per-case oracle. Uses the real parser and case scorer.</summary>
    public static decimal AbstainBaseline(IReadOnlyList<TuningCase> cases) =>
        EvaluateAbstainBaseline(cases).Score ?? throw new InvalidOperationException("Baseline correctness is not measured.");

    public static BaselineScore EvaluateAbstainBaseline(IReadOnlyList<TuningCase> cases,
        Func<TuningCase, TuningAnswer, bool>? outcomeKnown = null)
    {
        ArgumentNullException.ThrowIfNull(cases);
        if (cases.Count == 0 || cases.Select(c => c.Role).Distinct().Count() != 1 || cases.Select(c => c.Source).Distinct().Count() != 1)
            throw new ArgumentException("A baseline requires one nonempty role/source set.", nameof(cases));
        var role = cases[0].Role;
        var strategies = role switch
        {
            ModelRole.ArtifactValidationAssessment or ModelRole.CandidateAmbiguityAssessment => new[] { new TuningAnswer(Verdict: "uncertain") },
            ModelRole.ArtifactValidationDecision => [new(Verdict: "uncertain"), new(Verdict: "no_objection")],
            ModelRole.CandidateTriage => [new(Move: "keep"), new(Move: "uncertain")],
            ModelRole.SearchGuidance => [new(GiveUp: true)],
            ModelRole.IntentInterpretation => [new(Intent: "unclear")],
            _ => throw new ArgumentException("Role is not scored.", nameof(cases))
        };
        return strategies.Select(answer =>
        {
            var results = cases.Select(item =>
            {
                var rendered = TuningCaseRenderer.Render(item);
                var reply = TuningReplies.Render(item.Role, answer, rendered);
                return Score(item, rendered, new(ModelCallOutcome.Succeeded, reply), outcomeKnown?.Invoke(item, answer) ?? true).Score;
            }).ToArray();
            var correct = results.Count(r => r == TuningScore.Correct);
            var incorrect = results.Count(r => r == TuningScore.Incorrect);
            return new BaselineScore(correct + incorrect == 0 ? null : correct / (decimal)(correct + incorrect),
                correct, incorrect, results.Count(r => r == TuningScore.Unknown));
        }).OrderByDescending(r => r.Score ?? -1m).First();
    }
}
