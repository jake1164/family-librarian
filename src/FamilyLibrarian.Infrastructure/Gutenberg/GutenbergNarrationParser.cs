using System.Text.RegularExpressions;
using FamilyLibrarian.Application.Catalog;

namespace FamilyLibrarian.Infrastructure.Gutenberg;

/// <summary>
/// Deterministic, evidence-based narration classification for a Project
/// Gutenberg audio record, parsed from its own <c>*readme.txt</c> file (the
/// only place this catalogue's structured RDF/bibliographic metadata does
/// not carry: neither <c>dcterms:contributor</c>, <c>pgterms:bookshelf</c>,
/// nor the human-facing page's "Category" field distinguish a human reading
/// from a computer-generated one -- confirmed against real records before
/// writing this parser). Never guesses: absent a recognized statement, the
/// result is <see cref="NarrationKind.Unknown"/>, which is a valid,
/// expected outcome, not a failure.
/// </summary>
public static class GutenbergNarrationParser
{
    // Ordered most-specific-first is not required here: an explicit synthetic
    // statement and a human "read by" credit are checked independently, and a
    // record stating both would be self-contradictory data, not a case this
    // parser needs to arbitrate -- synthetic evidence wins if both appear,
    // since claiming human narration on a machine-read file would be the more
    // misleading of the two errors to make automatically.
    private static readonly string[] SyntheticPhrases =
    [
        "computer generated", "computer-generated", "computer voice",
        "text-to-speech", "text to speech", "speech synthesis",
        "synthesized speech", "synthetic voice", "machine generated",
        "machine-generated", "automatically generated audiobook"
    ];

    private static readonly Regex NarratorOnSameLine = new(
        @"(?:subtitle\s*:\s*)?(?:is\s+)?(?:reading|read)\s+by\s*[:\-]?\s*(?<name>[A-Za-z][\w.,'\-]*(?:\s+[A-Za-z][\w.,'\-]*){0,4})\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NarratedByOnSameLine = new(
        @"narrat(?:ed|or)\s*(?:by|:)\s*(?<name>[A-Za-z][\w.,'\-]*(?:\s+[A-Za-z][\w.,'\-]*){0,4})\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ReadByWithNameOnNextLine = new(
        @"(?:is\s+)?read(?:ing)?\s+by\s*[:\-]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static GutenbergNarrationEvidence Parse(string? readmeText)
    {
        if (string.IsNullOrWhiteSpace(readmeText))
        {
            return GutenbergNarrationEvidence.Unknown;
        }

        foreach (var phrase in SyntheticPhrases)
        {
            var index = readmeText.IndexOf(phrase, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                return new GutenbergNarrationEvidence(NarrationKind.Synthetic, null, ExtractLine(readmeText, index));
            }
        }

        var lines = readmeText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var match = NarratorOnSameLine.Match(line);
            if (!match.Success)
            {
                match = NarratedByOnSameLine.Match(line);
            }

            if (match.Success)
            {
                var name = CleanName(match.Groups["name"].Value);
                if (name is not null)
                {
                    return new GutenbergNarrationEvidence(NarrationKind.Human, name, line);
                }
            }

            if (ReadByWithNameOnNextLine.IsMatch(line) &&
                TryFindNameOnFollowingLine(lines, i + 1) is { } nextLineName)
            {
                return new GutenbergNarrationEvidence(NarrationKind.Human, nextLineName, $"{line} {nextLineName}");
            }
        }

        return GutenbergNarrationEvidence.Unknown;
    }

    /// <summary>
    /// Some Gutenberg readmes put the credit on its own line after "... is
    /// read by" rather than on the same line (real example: "This audio
    /// reading of Moby Dick is read by" followed by a blank line, then
    /// "Stewart Wills"). Only the immediately following non-blank line is
    /// trusted, and only when it reads like a name rather than the start of
    /// unrelated content (a chapter listing, a licence paragraph, ...).
    /// </summary>
    private static string? TryFindNameOnFollowingLine(string[] lines, int start)
    {
        for (var i = start; i < lines.Length && i < start + 3; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            return CleanName(trimmed);
        }

        return null;
    }

    private static string? CleanName(string value)
    {
        var trimmed = value.Trim().TrimEnd('.', ',');
        if (trimmed.Length is 0 or > 80)
        {
            return null;
        }

        // Reject an accidental match against unrelated content (a chapter
        // heading, a licence notice, ...) rather than reporting a bogus name.
        return trimmed.Contains("chapter", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("project gutenberg", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("contents", StringComparison.OrdinalIgnoreCase)
            ? null
            : trimmed;
    }

    private static string ExtractLine(string text, int index)
    {
        var start = text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        var end = text.IndexOf('\n', index);
        if (end < 0)
        {
            end = text.Length;
        }

        return text[start..end].Trim();
    }
}

/// <param name="Evidence">
/// The source line(s) the classification was derived from, kept only to
/// explain an automatic decision or review reason -- never a source URL.
/// </param>
public sealed record GutenbergNarrationEvidence(NarrationKind Kind, string? Narrator, string? Evidence)
{
    public static readonly GutenbergNarrationEvidence Unknown = new(NarrationKind.Unknown, null, null);
}
