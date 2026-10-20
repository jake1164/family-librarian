using System.Text.Json;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.ModelGateway;
using FamilyLibrarian.Domain.ModelGateway;
using static FamilyLibrarian.Application.SemanticAssessment.SemanticJson;

namespace FamilyLibrarian.Application.SemanticAssessment;

public sealed record SearchGuidanceSuggestion(string Reason, string Value, IReadOnlyList<string> EvidenceIds, string Note);

public sealed record SearchGuidance(bool GiveUp, IReadOnlyList<SearchGuidanceSuggestion> Suggestions, Guid? UsageRecordId = null);

public static partial class SemanticAssessmentParser
{
    public const string SearchGuidanceSchema = "search-guidance/1";
    private static readonly string[] GuidanceProperties = ["schema", "give_up", "suggestions"];
    private static readonly string[] SuggestionProperties = ["reason", "value", "evidence_ids", "note"];
    private static readonly string[] GuidanceReasons =
        ["original_language_title", "alternate_published_title", "series_naming", "contained_in", "author_variant"];

    public static ModelParseResult<SearchGuidance> ParseSearchGuidance(string? content, BookIdentity requested,
        IReadOnlyCollection<string> suppliedIds, IReadOnlyCollection<string> alreadyTried)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(suppliedIds);
        ArgumentNullException.ThrowIfNull(alreadyTried);
        if (string.IsNullOrWhiteSpace(content)) return ModelParse.NotJson<SearchGuidance>("empty response");
        if (content.Length > MaxContentLength) return RejectGuidance("response is too long");
        JsonDocument document;
        try { document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = MaxDepth }); }
        catch (JsonException) { return ModelParse.NotJson<SearchGuidance>("not valid JSON"); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !HasExactly(root, GuidanceProperties))
                return RejectGuidance("the object must contain exactly schema, give_up and suggestions");
            if (!IsString(root.GetProperty("schema"), out var schema) || schema != SearchGuidanceSchema)
                return RejectGuidance("schema must be search-guidance/1");
            var stop = root.GetProperty("give_up");
            if (stop.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return RejectGuidance("give_up must be boolean");
            var giveUp = stop.GetBoolean();
            var array = root.GetProperty("suggestions");
            if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 5 || !giveUp && array.GetArrayLength() == 0)
                return RejectGuidance("suggestions must contain 1-5 items, or 0 with give_up");
            var suggestions = new List<SearchGuidanceSuggestion>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !HasExactly(item, SuggestionProperties))
                    return RejectGuidance("each suggestion must contain exactly reason, value, evidence_ids and note");
                if (!IsString(item.GetProperty("reason"), out var reason) || !GuidanceReasons.Contains(reason, StringComparer.Ordinal))
                    return RejectGuidance("unknown search reason");
                if (!IsString(item.GetProperty("value"), out var value) || !IsSearchValue(value))
                    return RejectGuidance("value must be 1-120 plain bibliographic characters without links", ModelFailureReason.InvalidSearch);
                var normalized = SearchVariantPlanner.Normalize(value);
                if (IsNotNew(normalized, requested, alreadyTried) || !seen.Add(normalized)) return RejectGuidance("suggestion is not new", ModelFailureReason.RepeatedSearch);
                var ids = item.GetProperty("evidence_ids");
                if (ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() > MaxEvidenceIdsPerClaim)
                    return RejectGuidance("evidence_ids must contain at most 6 supplied ids");
                var cited = new HashSet<string>(StringComparer.Ordinal);
                foreach (var id in ids.EnumerateArray())
                {
                    if (!IsString(id, out var text) || !suppliedIds.Contains(text, StringComparer.Ordinal) ||
                        !IsGuidanceEvidenceId(text) || !cited.Add(text)) return RejectGuidance("unknown or duplicate evidence id", ModelFailureReason.UnknownEvidence);
                }
                if (reason == "contained_in" && !cited.Any(IsResultId)) return RejectGuidance("contained_in must cite a supplied result", ModelFailureReason.NoCitedEvidence);
                if (!IsPlainNote(item.GetProperty("note"), out var note, out var error)) return RejectGuidance(error!);
                suggestions.Add(new(reason, value.Trim(), cited.ToArray(), note));
            }
            return ModelParse.Ok(new SearchGuidance(giveUp, suggestions));
        }
    }

    internal static bool IsSearchValue(string value) => value.Length is >= 1 and <= 120 &&
        !string.IsNullOrWhiteSpace(value) && !value.Contains("://", StringComparison.Ordinal) &&
        value.All(c => char.IsLetterOrDigit(c) || c is ' ' or '\'' or '-' or ':' or ',' or '.' or '&' or '!' or '?');

    private static bool IsNotNew(string normalized, BookIdentity requested, IReadOnlyCollection<string> tried) =>
        normalized == SearchVariantPlanner.Normalize(requested.Title) || normalized == SearchVariantPlanner.Normalize(requested.Author) ||
        (requested.Authors ?? []).Any(a => normalized == SearchVariantPlanner.Normalize(a.Name)) ||
        tried.Any(key => normalized == SearchVariantPlanner.Normalize(key) ||
            normalized == SearchVariantPlanner.Normalize(key[(key.IndexOf(':', StringComparison.Ordinal) + 1)..]));

    private static bool IsResultId(string id) => id.Length > 1 && id[0] == 'r' && id[1..].All(char.IsAsciiDigit);
    private static bool IsGuidanceEvidenceId(string id) => IsResultId(id) || id.StartsWith("req-", StringComparison.Ordinal);
    private static ModelParseResult<SearchGuidance> RejectGuidance(string detail, ModelFailureReason reason = ModelFailureReason.InvalidStructure) =>
        ModelParse.SchemaRejected<SearchGuidance>(detail, reason);
}
