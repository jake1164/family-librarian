using System.Text.Json;
using FamilyLibrarian.Application.ModelGateway;

namespace FamilyLibrarian.Application.SemanticAssessment;

internal static class SemanticJson
{
    private const int MaxNoteLength = SemanticAssessmentParser.MaxNoteLength;
    internal static bool IsPlainNote(JsonElement element, out string note, out string? error)
    {
        note = string.Empty;
        error = null;
        if (!IsString(element, out var text))
        {
            error = "note must be a string";
            return false;
        }

        text = text.Trim();
        if (text.Length is < 1 or > MaxNoteLength || text.Any(char.IsControl) || text.Contains('<', StringComparison.Ordinal) ||
            text.Contains('>', StringComparison.Ordinal) || text.Contains("://", StringComparison.Ordinal))
        {
            error = $"note must be 1-{MaxNoteLength} plain characters with no markup or links";
            return false;
        }

        note = text;
        return true;
    }

    internal static bool IsString(JsonElement element, out string value)
    {
        if (element.ValueKind == JsonValueKind.String && element.GetString() is { } text)
        {
            value = text;
            return true;
        }

        value = string.Empty;
        return false;
    }

    internal static bool HasExactly(JsonElement element, string[] expected)
    {
        var names = element.EnumerateObject().Select(property => property.Name).ToArray();
        return names.Length == expected.Length && names.Distinct(StringComparer.Ordinal).Count() == names.Length &&
               expected.All(name => names.Contains(name, StringComparer.Ordinal));
    }

    internal static ModelParseResult<SemanticAssessment> Reject(string reason) => ModelParse.SchemaRejected<SemanticAssessment>(reason);

    internal static string Clip(string? value) => value is null ? "?" : value.Length <= 40 ? value : value[..40] + "…";
}
