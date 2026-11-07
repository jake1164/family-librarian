using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FamilyLibrarian.Domain.ModelGateway;

namespace FamilyLibrarian.Application.ModelTuning;

public sealed record TuningCaseIdentity(int SchemaVersion, TuningSource Source, ModelRole Role, string Id, int InputSchemaVersion)
{
    public static TuningCaseIdentity From(TuningCase item) => new(item.SchemaVersion, item.Source, item.Role, item.Id, item.Input.SchemaVersion);
}
public sealed record TuningModelKey(string? ConnectionId, string Alias);
public enum TuningPlacement { Local, Cloud, Unknown }
public sealed record TuningResultIdentity(TuningCaseIdentity Case, string DatasetVersion, string PromptIdentity,
    string ParserIdentity, TuningModelKey Model, string? ReportedModelId, ModelReasoningMode Reasoning, int Repeat);

public interface ITuningCaseResult
{
    TuningSource Source { get; }
    TuningResultIdentity Identity { get; }
    TuningAnswer? NormalizedAnswer { get; }
    TuningScore Score { get; }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BuiltInCaseResult(TuningResultIdentity Identity, TuningAnswer? NormalizedAnswer,
    TuningScore Score, string? RawReplyPreview) : ITuningCaseResult
{
    public TuningSource Source => TuningSource.BuiltIn;
}
/// <summary>There is deliberately no raw-reply member, including on its shared interface.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HouseholdCaseResult(TuningResultIdentity Identity, TuningAnswer? NormalizedAnswer, TuningScore Score) : ITuningCaseResult
{
    public TuningSource Source => TuningSource.Household;
}

public static class TuningResultJson
{
    public static ITuningCaseResult Create(TuningCase item, TuningResultIdentity identity, CaseScore score, string? transientReply)
    {
        ITuningCaseResult result = item.Source == TuningSource.Household
            ? new HouseholdCaseResult(identity, score.Answer, score.Score)
            : new BuiltInCaseResult(identity, score.Answer, score.Score,
                score.Score == TuningScore.Incorrect ? Preview(transientReply) : null);
        Validate(item, result);
        return result;
    }

    public static string Write(TuningCase item, ITuningCaseResult result)
    {
        Validate(item, result);
        return JsonSerializer.Serialize(result, result.GetType(), TuningInputJson.Options);
    }

    public static ITuningCaseResult Read(TuningCase item, string json)
    {
        using var document = JsonDocument.Parse(json);
        TuningInputJson.CheckTree(document.RootElement);
        var source = document.RootElement.GetProperty("source").Deserialize<TuningSource>(TuningInputJson.Options);
        if (source != item.Source) throw new JsonException("Result source differs from case.");
        ITuningCaseResult result = source == TuningSource.Household
            ? document.RootElement.Deserialize<HouseholdCaseResult>(TuningInputJson.Options)!
            : document.RootElement.Deserialize<BuiltInCaseResult>(TuningInputJson.Options)!;
        Validate(item, result);
        return result;
    }

    private static void Validate(TuningCase item, ITuningCaseResult result)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(result);
        if (result.Source != item.Source || result.Identity.Case != TuningCaseIdentity.From(item) ||
            result is not (BuiltInCaseResult or HouseholdCaseResult) || !Enum.IsDefined(result.Score))
            throw new ArgumentException("Result identity/source mismatch.", nameof(result));
        if (result is BuiltInCaseResult built && (built.Score == TuningScore.Unknown ||
            built.RawReplyPreview is not null && (built.Score != TuningScore.Incorrect || Encoding.UTF8.GetByteCount(built.RawReplyPreview) > 1024)))
            throw new ArgumentException("Invalid built-in reply preview/score.", nameof(result));
        if (result.NormalizedAnswer is { } a)
        {
            if (a.AllowedSuggestions is not null || a.RequiredSuggestions is not null)
                throw new ArgumentException("Result must be a normalized answer, not a label.", nameof(result));
            var valid = item.Role switch
            {
                ModelRole.ArtifactValidationAssessment or ModelRole.CandidateAmbiguityAssessment =>
                    a.Verdict is "consistent" or "inconsistent" or "uncertain" && OnlyVerdict(a),
                ModelRole.ArtifactValidationDecision => a.Verdict is "hold" or "no_objection" or "uncertain" && OnlyVerdict(a),
                ModelRole.CandidateTriage => a.Verdict is null && a.GiveUp is null && a.Suggestions is null && a.Intent is null && a.Title is null && a.Author is null &&
                    (a.Move is "keep" or "uncertain" ? a.TargetKey is null : a.Move is "veto" or "prefer" or "author_variant" &&
                        a.TargetKey is { Length: > 0 and <= 80 } && a.TargetKey.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')),
                ModelRole.IntentInterpretation => a.Verdict is null && a.Move is null && a.TargetKey is null && a.GiveUp is null && a.Suggestions is null &&
                    (a.Intent is "unclear" or "check_status" or "out_of_domain" ? a.Title is null && a.Author is null :
                     a.Intent == "find_book" && Plain(a.Title, 120) && (a.Author is null || Plain(a.Author, 120))),
                ModelRole.SearchGuidance => a.Verdict is null && a.Move is null && a.TargetKey is null && a.Intent is null && a.Title is null && a.Author is null &&
                    a.GiveUp is not null && a.Suggestions is { Count: <= 5 } && a.Suggestions.All(s => Plain(s.Value, 120) &&
                    s.Reason is "original_language_title" or "alternate_published_title" or "series_naming" or "contained_in" or "author_variant"),
                _ => false
            };
            if (!valid) throw new ArgumentException("Invalid normalized answer.", nameof(result));
        }
        else if (result.Score != TuningScore.Incorrect) throw new ArgumentException("A scored answer is required.", nameof(result));
    }
    private static bool Plain(string? text, int limit) => text is { Length: > 0 } && text.Length <= limit &&
        !text.Any(char.IsControl) && !text.Contains("://", StringComparison.Ordinal) && !text.Contains('{', StringComparison.Ordinal) && !text.Contains('<', StringComparison.Ordinal);
    private static bool OnlyVerdict(TuningAnswer a) => a.Move is null && a.TargetKey is null && a.GiveUp is null &&
        a.Suggestions is null && a.Intent is null && a.Title is null && a.Author is null;
    private static string? Preview(string? value)
    {
        if (value is null) return null;
        var result = new StringBuilder(); var bytes = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > 1024) break;
            result.Append(rune.ToString()); bytes += rune.Utf8SequenceLength;
        }
        return result.ToString();
    }
}
