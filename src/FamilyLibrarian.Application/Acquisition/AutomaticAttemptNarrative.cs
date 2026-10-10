using System.Text;

namespace FamilyLibrarian.Application.Acquisition;

/// <summary>
/// The plain wording of the provider-activity ledger for the retry loop, kept
/// in one place so the synchronous and the background failure paths say the
/// same thing (PROVIDER-7).
/// </summary>
/// <remarks>
/// The ledger is the first place an administrator looks to learn what the
/// system is doing, so each line answers three questions: which attempt this
/// is, whether anything needs doing, and what happens next. A failed copy that
/// is being replaced reads as progress; only an exhausted budget reads as a
/// problem for a person.
/// </remarks>
public static class AutomaticAttemptNarrative
{
    public static string Starting(string? candidateLabel, int attemptNumber, int attemptLimit)
    {
        var first = attemptNumber <= 1
            ? "Fetching a copy automatically."
            : $"Attempt {attemptNumber} of {attemptLimit}: fetching the next best copy.";
        return string.IsNullOrWhiteSpace(candidateLabel) ? first : $"{first} Release: {candidateLabel}";
    }

    public static string Advancing(string reason, int failuresSoFar, int attemptLimit) =>
        $"Attempt {failuresSoFar} of {attemptLimit} did not work out: {Sentence(reason)} " +
        "Nothing needs doing; trying the next best copy.";

    public static string Exhausted(string reason, int failuresSoFar, int attemptLimit) =>
        $"Attempt {failuresSoFar} of {attemptLimit} did not work out: {Sentence(reason)} " +
        "No attempts are left, so a librarian needs to choose a source.";

    /// <summary>
    /// A neutral, single-line description of the release being fetched. The
    /// name is provider-supplied and untrusted: control characters are dropped,
    /// whitespace collapsed and the length bounded. It is only ever rendered as
    /// text, in the administrator-only ledger.
    /// </summary>
    public static string? DescribeCandidate(string? releaseName, string? title, string? format, long? sizeBytes)
    {
        var name = Clean(string.IsNullOrWhiteSpace(releaseName) ? title : releaseName, 100);
        if (name is null)
        {
            return null;
        }

        var facts = new List<string>();
        if (Clean(format, 12) is { } cleanFormat)
        {
            facts.Add(cleanFormat.ToUpperInvariant());
        }

        if (sizeBytes is > 0)
        {
            facts.Add(FormatSize(sizeBytes.Value));
        }

        return facts.Count == 0 ? name : $"{name} ({string.Join(", ", facts)})";
    }

    private static string Sentence(string reason) =>
        string.IsNullOrWhiteSpace(reason)
            ? "no reason was given."
            : reason.TrimEnd().EndsWith('.') ? reason.TrimEnd() : reason.TrimEnd() + ".";

    private static string? Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var builder = new StringBuilder(value.Length);
        var lastWasSpace = true;
        foreach (var character in value)
        {
            if (char.IsControl(character) || char.IsWhiteSpace(character))
            {
                if (!lastWasSpace)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            builder.Append(character);
            lastWasSpace = false;
        }

        var cleaned = builder.ToString().Trim();
        if (cleaned.Length == 0)
        {
            return null;
        }

        return cleaned.Length <= maxLength ? cleaned : cleaned[..(maxLength - 1)] + "...";
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1_000_000_000 => $"{bytes / 1_000_000_000d:0.#} GB",
        >= 1_000_000 => $"{bytes / 1_000_000d:0.#} MB",
        _ => $"{Math.Max(1, bytes / 1_000d):0.#} KB"
    };
}
