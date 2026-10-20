using System.Text.RegularExpressions;
using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Evidence;

namespace FamilyLibrarian.Application.SemanticAssessment;

/// <summary>Deterministic observations of transient excerpts. Values are fixed labels, never passages from a book.</summary>
public static class ArtifactExcerptEvidence
{
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly string[] English = ["the", "and", "of", "to", "was", "were", "with", "this", "that", "had", "have", "not", "from", "her", "his", "their", "said", "for"];
    private static readonly string[] German = ["der", "die", "das", "und", "mit", "nicht", "sich", "eine", "einem", "einen", "hatten", "haben", "sie", "dem", "den", "auf", "sind", "ihre"];

    public static IReadOnlyList<EvidenceFinding> Derive(IReadOnlyList<ArtifactExcerpt> excerpts, ExpectedArtifactProfile? expected)
    {
        var facts = new List<EvidenceFinding>();
        foreach (var excerpt in excerpts)
        {
            if (excerpt.Id == "tc-last" && Regex.IsMatch(excerpt.Text, @"\b(free sample|continue reading|buy the full book)\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, PatternTimeout))
                facts.Add(new("tc-sample-marker", EvidenceReasonCodes.ArtifactExcerpt.SampleMarker,
                    EvidencePolarity.Condition, "Last-page sample phrase detected."));
            if (excerpt.Id == "tc-copyright")
            {
                if (Regex.IsMatch(excerpt.Text, @"\babridged\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, PatternTimeout))
                    facts.Add(new("tc-abridged-marker", EvidenceReasonCodes.ArtifactExcerpt.AbridgedMarker,
                        EvidencePolarity.Condition, "Copyright-page abridgement marker detected."));
                var printed = Regex.Matches(excerpt.Text, @"\b(?:97[89][\s-]*)?\d(?:[\s-]*\d){8}[\s-]*[\dXx]\b", RegexOptions.CultureInvariant, PatternTimeout)
                    .Select(m => Isbn(m.Value)).OfType<string>();
                var catalog = (expected?.Isbn13s ?? []).Select(Isbn).OfType<string>();
                if (printed.Intersect(catalog, StringComparer.Ordinal).Any())
                    facts.Add(new("tc-printed-isbn", EvidenceReasonCodes.ArtifactExcerpt.PrintedIsbnAgrees,
                        EvidencePolarity.Positive, "Printed ISBN matches a catalog ISBN."));
            }
            if (excerpt.Id == "tc-chapter1" && ProseMismatch(excerpt.Text, expected?.AcceptedLanguage))
                facts.Add(new("tc-prose-language", EvidenceReasonCodes.ArtifactExcerpt.ProseLanguageDiffers,
                    EvidencePolarity.Weak, "Chapter prose has a strong conflicting language cue."));
        }
        return facts;
    }

    // A limited cue, not a language detector: unsupported, short or mixed prose stays Unknown.
    private static bool ProseMismatch(string text, string? accepted)
    {
        var language = LanguageAcceptance.PreferenceCode(string.IsNullOrWhiteSpace(accepted) ? "en" : accepted);
        if (language is not ("en" or "de")) return false;
        var words = Words(text);
        if (words.Length < 40) return false;
        var wanted = language == "en" ? English : German;
        var other = language == "en" ? German : English;
        var opposing = words.Where(w => other.Contains(w, StringComparer.Ordinal)).ToArray();
        return opposing.Length >= 12 && opposing.Distinct(StringComparer.Ordinal).Count() >= 6 &&
            opposing.Length > words.Count(w => wanted.Contains(w, StringComparer.Ordinal)) * 2;
    }

    private static string? Isbn(string text)
    {
        var value = text.Replace("-", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        if (value.Length == 10 && value[..9].All(char.IsAsciiDigit) && (char.IsAsciiDigit(value[9]) || value[9] == 'X'))
        {
            if (value.Select((c, i) => (c == 'X' ? 10 : c - '0') * (10 - i)).Sum() % 11 != 0) return null;
            value = "978" + value[..9];
            value += (10 - value.Select((c, i) => (c - '0') * (i % 2 == 0 ? 1 : 3)).Sum() % 10) % 10;
        }
        return value.Length == 13 && value.All(char.IsAsciiDigit) && value.StartsWith("97", StringComparison.Ordinal) &&
            value.Select((c, i) => (c - '0') * (i % 2 == 0 ? 1 : 3)).Sum() % 10 == 0 ? value : null;
    }

    public static bool CopiesExcerpt(string note, IReadOnlyList<ArtifactExcerpt> excerpts)
    {
        var words = Words(note);
        if (words.Length < 8) return false;
        var windows = Enumerable.Range(0, words.Length - 7).Select(i => string.Join(' ', words.Skip(i).Take(8))).ToArray();
        return excerpts.Any(e =>
        {
            var text = " " + string.Join(' ', Words(e.Text)) + " ";
            return windows.Any(w => text.Contains(" " + w + " ", StringComparison.Ordinal));
        });
    }

    private static string[] Words(string text) => Regex.Matches(text.Normalize(System.Text.NormalizationForm.FormKC).ToLowerInvariant(),
        @"[\p{L}\p{N}]+", RegexOptions.CultureInvariant, PatternTimeout).Select(m => m.Value).ToArray();
}
