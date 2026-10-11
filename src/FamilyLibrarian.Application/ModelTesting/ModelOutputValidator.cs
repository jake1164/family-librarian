using System.Text.Json;

namespace FamilyLibrarian.Application.ModelTesting;

/// <summary>
/// Strict validation of model output. A model's reply is untrusted text: it is accepted only if it is exactly the JSON shape
/// asked for, with nothing extra (a model-reported "confidence", an unknown field, a wrapper) and every value inside its
/// allowed set. Anything else is rejected with a short reason and never interpreted further.
/// </summary>
public static class ModelOutputValidator
{
    public const int MaxContentLength = 4_000;
    private const int MaxDepth = 6;

    public static readonly IReadOnlyList<string> Intents = ["FindBook", "RequestBook", "CheckRequestStatus", "OutOfDomain"];
    public static readonly IReadOnlyList<string> Assessments = ["likely_match", "likely_different", "uncertain"];

    public sealed record Validation<T>(bool IsValid, T? Value, string? Error);

    private static class Validation
    {
        public static Validation<T> Ok<T>(T value) => new(true, value, null);

        public static Validation<T> Fail<T>(string error) => new(false, default, error);
    }

    public sealed record BookFacts(string Title, string Author, int Year);

    public sealed record MatchReason(string Text, string EvidenceId);

    public sealed record MatchAdvisory(string Assessment, IReadOnlyList<MatchReason> Reasons);

    public static Validation<string> ValidateIntent(string? content)
    {
        var root = ParseObject(content, ["intent"], out var error);
        if (root is null)
        {
            return Validation.Fail<string>(error!);
        }

        using (root)
        {
            var intent = RequiredString(root.RootElement, "intent", 1, 40, out error);
            return intent is null
                ? Validation.Fail<string>(error!)
                : Intents.Contains(intent, StringComparer.Ordinal) ? Validation.Ok(intent) : Validation.Fail<string>($"unknown intent '{Clip(intent)}'");
        }
    }

    public static Validation<BookFacts> ValidateBookFacts(string? content)
    {
        var root = ParseObject(content, ["title", "author", "year"], out var error);
        if (root is null)
        {
            return Validation.Fail<BookFacts>(error!);
        }

        using (root)
        {
            var title = RequiredString(root.RootElement, "title", 1, 200, out error);
            var author = title is null ? null : RequiredString(root.RootElement, "author", 1, 200, out error);
            if (title is null || author is null)
            {
                return Validation.Fail<BookFacts>(error!);
            }

            var yearElement = root.RootElement.GetProperty("year");
            if (yearElement.ValueKind != JsonValueKind.Number || !yearElement.TryGetInt32(out var year) || year is < 1000 or > 2100)
            {
                return Validation.Fail<BookFacts>("year must be an integer between 1000 and 2100");
            }

            return Validation.Ok(new BookFacts(title, author, year));
        }
    }

    public static Validation<MatchAdvisory> ValidateMatchAdvisory(string? content, IReadOnlyCollection<string> allowedEvidenceIds)
    {
        var root = ParseObject(content, ["assessment", "reasons"], out var error);
        if (root is null)
        {
            return Validation.Fail<MatchAdvisory>(error!);
        }

        using (root)
        {
            var assessment = RequiredString(root.RootElement, "assessment", 1, 40, out error);
            if (assessment is null)
            {
                return Validation.Fail<MatchAdvisory>(error!);
            }

            if (!Assessments.Contains(assessment, StringComparer.Ordinal))
            {
                return Validation.Fail<MatchAdvisory>($"unknown assessment '{Clip(assessment)}'");
            }

            var reasonsElement = root.RootElement.GetProperty("reasons");
            if (reasonsElement.ValueKind != JsonValueKind.Array || reasonsElement.GetArrayLength() is < 1 or > 3)
            {
                return Validation.Fail<MatchAdvisory>("reasons must be an array of one to three items");
            }

            var reasons = new List<MatchReason>();
            foreach (var item in reasonsElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !HasExactlyProperties(item, ["text", "evidence_id"]))
                {
                    return Validation.Fail<MatchAdvisory>("each reason must be an object with exactly text and evidence_id");
                }

                var text = RequiredString(item, "text", 1, 200, out error);
                var evidenceId = text is null ? null : RequiredString(item, "evidence_id", 1, 40, out error);
                if (text is null || evidenceId is null)
                {
                    return Validation.Fail<MatchAdvisory>(error!);
                }

                if (!allowedEvidenceIds.Contains(evidenceId, StringComparer.Ordinal))
                {
                    return Validation.Fail<MatchAdvisory>($"reason cites an evidence id that was not supplied ('{Clip(evidenceId)}')");
                }

                reasons.Add(new MatchReason(text, evidenceId));
            }

            return Validation.Ok(new MatchAdvisory(assessment, reasons));
        }
    }

    private static JsonDocument? ParseObject(string? content, string[] exactProperties, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(content))
        {
            error = "empty response";
            return null;
        }

        if (content.Length > MaxContentLength)
        {
            error = "response is too long";
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = MaxDepth });
        }
        catch (JsonException)
        {
            error = "not valid JSON (surrounding text or markdown fences are not accepted)";
            return null;
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            error = "the top-level value must be a JSON object";
            return null;
        }

        if (!HasExactlyProperties(document.RootElement, exactProperties))
        {
            document.Dispose();
            error = $"the object must contain exactly: {string.Join(", ", exactProperties)} (nothing extra, nothing missing)";
            return null;
        }

        return document;
    }

    private static bool HasExactlyProperties(JsonElement element, string[] expected)
    {
        var names = element.EnumerateObject().Select(property => property.Name).ToArray();
        return names.Length == expected.Length && names.Distinct(StringComparer.Ordinal).Count() == names.Length &&
               expected.All(name => names.Contains(name, StringComparer.Ordinal));
    }

    private static string? RequiredString(JsonElement element, string name, int minLength, int maxLength, out string? error)
    {
        error = null;
        var value = element.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String)
        {
            error = $"{name} must be a string";
            return null;
        }

        var text = value.GetString()!.Trim();
        if (text.Length < minLength || text.Length > maxLength || text.Any(char.IsControl))
        {
            error = $"{name} must be {minLength}-{maxLength} plain characters";
            return null;
        }

        return text;
    }

    private static string Clip(string value) => value.Length <= 40 ? value : value[..40] + "…";
}
