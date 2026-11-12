using System.Text.Json;
using System.Text.Json.Serialization;
using System.Reflection;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Evidence;
using FamilyLibrarian.Domain.ModelGateway;

namespace FamilyLibrarian.Application.ModelTuning;

public enum TuningSource { BuiltIn, Household }
public enum TuningScore { Correct, Incorrect, Unknown }
public enum TuningCoverage { ProductionReachable, ContractOnly }
public enum TuningLabelKind { Rule, Judgment, Injection }
public enum LabelReviewState { Proposed, Approved }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LabelReview(LabelReviewState State, int LabelRevision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TuningSearchName(string Reason, string Value);
/// <summary>Closed normalized answer: no notes, provider error body or raw reply.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TuningAnswer(string? Verdict = null, string? Move = null, string? TargetKey = null,
    bool? GiveUp = null, IReadOnlyList<TuningSearchName>? Suggestions = null, string? Intent = null,
    string? Title = null, string? Author = null,
    IReadOnlyList<TuningSearchName>? AllowedSuggestions = null, IReadOnlyList<TuningSearchName>? RequiredSuggestions = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TuningExpected(IReadOnlyList<TuningAnswer> Acceptable,
    IReadOnlyList<IReadOnlyList<string>> RequiredEvidenceAlternatives, IReadOnlyList<string> Forbidden);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SyntheticSupplement(int SchemaVersion, IReadOnlyList<ArtifactExcerpt> Excerpts);
public sealed record TuningCase(int SchemaVersion, TuningSource Source, string Id, ModelRole Role,
    TuningLabelKind Category, TuningCoverage Coverage, string Rationale, LabelReview Review,
    ITuningInput Input, TuningExpected Expected, SyntheticSupplement? SyntheticSupplement);

public static class TuningRoles
{
    public static IReadOnlyList<ModelRole> Scored { get; } = Array.AsReadOnly(new[] {
        ModelRole.ArtifactValidationAssessment, ModelRole.ArtifactValidationDecision, ModelRole.CandidateAmbiguityAssessment,
        ModelRole.CandidateTriage, ModelRole.SearchGuidance, ModelRole.IntentInterpretation });

    public static bool IsScored(ModelRole role) => Scored.Contains(role);
}

/// <summary>Independent snapshot JSON contract, usable without a corpus manifest or loader.</summary>
public static class TuningInputJson
{
    private static readonly string[] ExcludedExtensions = [".epub", ".mp3", ".m4b", ".pdf", ".ogg", ".kepub"];
    private static readonly HashSet<string> EvidenceCodes = Codes(typeof(EvidenceReasonCodes)).ToHashSet(StringComparer.Ordinal);
    internal static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        MaxDepth = 24,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public static ITuningInput Read(ModelRole role, string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 24 });
        CheckTree(document.RootElement);
        ITuningInput input = role switch
        {
            ModelRole.ArtifactValidationAssessment => Read<ArtifactAssessmentInput>(),
            ModelRole.ArtifactValidationDecision => Read<ArtifactHoldInput>(),
            ModelRole.CandidateAmbiguityAssessment => Read<CandidateAssessmentInput>(),
            ModelRole.CandidateTriage => Read<CandidateTriageCaseInput>(),
            ModelRole.SearchGuidance => Read<SearchCaseInput>(),
            ModelRole.IntentInterpretation => Read<IntentCaseInput>(),
            _ => throw new ArgumentException("Role is not scored.", nameof(role))
        };
        if (input.SchemaVersion != 1) throw new JsonException("Unsupported role-input schema.");
        CheckContent(document.RootElement, input is IntentCaseInput);
        if (input is CandidateTriageCaseInput triage && triage.Candidates.Count > 8 ||
            input is SearchCaseInput search && search.Results.Count > 15)
            throw new JsonException("Role-input candidate bound exceeded.");
        return input;

        T Read<T>() => document.RootElement.Deserialize<T>(Options) ?? throw new JsonException("Missing role input.");
    }

    private static IEnumerable<string> Codes(Type type) => type.GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!)
        .Concat(type.GetNestedTypes(BindingFlags.Public).SelectMany(Codes));

    private static void CheckContent(JsonElement element, bool intent, string field = "")
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("code", out var code))
            {
                var value = code.GetString();
                if (value is null || !EvidenceCodes.Contains(value) || value == EvidenceReasonCodes.ArtifactExcerpt.Text)
                    throw new JsonException("Evidence code is not allow-listed.");
                if (element.TryGetProperty("value", out var detail) && detail.ValueKind == JsonValueKind.String &&
                    detail.GetString()!.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length > 24)
                    throw new JsonException("Evidence value is not a bounded observation.");
            }
            if (element.TryGetProperty("recordKind", out var kind) && kind.GetString() is not
                (ArtifactEvidenceRecordKinds.IdentityVerification or ArtifactEvidenceRecordKinds.TechnicalInspection or
                ArtifactEvidenceRecordKinds.SecurityEvaluation or ArtifactEvidenceRecordKinds.ArtifactProfile or ArtifactEvidenceRecordKinds.IdentityOverride))
                throw new JsonException("Evidence kind is not allow-listed.");
            if (element.TryGetProperty("scheme", out var scheme) && scheme.GetString() != "isbn13")
                throw new JsonException("Identifier scheme is not allow-listed.");
            foreach (var property in element.EnumerateObject()) CheckContent(property.Value, intent, property.Name);
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var value in element.EnumerateArray()) CheckContent(value, intent, field);
        else if (element.ValueKind == JsonValueKind.String && !(intent && field is "message" or "previousSearch"))
        {
            var value = element.GetString()!;
            if (value.Length > 256 || value.Any(char.IsControl) || value.Contains("://", StringComparison.Ordinal) ||
                value.StartsWith('/') || value.Contains('\\', StringComparison.Ordinal) || value.Contains("../", StringComparison.Ordinal) ||
                value.Length == 64 && value.All(char.IsAsciiHexDigit) ||
                field != "format" && ExcludedExtensions
                    .Any(ext => value.Length > ext.Length && value.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                throw new JsonException("Input field contains excluded content or exceeds its bound.");
        }
    }

    public static string Write(ModelRole role, ITuningInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var json = JsonSerializer.Serialize(input, input.GetType(), Options);
        _ = Read(role, json);
        return json;
    }

    internal static void CheckTree(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate JSON property.");
                CheckTree(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            if (element.GetArrayLength() > 1024) throw new JsonException("Input collection too large.");
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Null) throw new JsonException("Null collection item.");
                CheckTree(item);
            }
        }
    }
}
