using System.Text.RegularExpressions;

namespace FamilyLibrarian.Application.Matching;

/// <summary>
/// The matching logic originally written for <c>CwaCatalogClient</c>'s OPDS
/// lookup, extracted so Audiobookshelf (and any future destination) gets the
/// same ambiguity-averse behavior instead of a naive first-match.
/// </summary>
public sealed class DeterministicBookMatcher : IBookMatcher
{
    public BookMatchResult ResolveUnique(IReadOnlyList<CandidateBook> candidates) =>
        candidates.Count switch
        {
            0 => BookMatchResult.NoMatchResult,
            1 => BookMatchResult.Match(candidates[0]),
            _ => BookMatchResult.Ambiguous(candidates)
        };

    public BookMatchResult MatchByTitleAuthor(string title, string? author, IReadOnlyList<CandidateBook> candidates)
    {
        var matches = candidates
            .Where(candidate => TitleMatches(candidate.Title, title) && AuthorMatches(candidate.Author, author))
            .ToArray();

        return ResolveUnique(matches);
    }

    private static bool TitleMatches(string candidateTitle, string requestedTitle) =>
        !string.IsNullOrWhiteSpace(candidateTitle) &&
        candidateTitle.Contains(requestedTitle, StringComparison.OrdinalIgnoreCase) &&
        !IsUnwantedVariant(candidateTitle, requestedTitle);

    private static bool AuthorMatches(string? candidateAuthor, string? requestedAuthor)
    {
        if (string.IsNullOrWhiteSpace(requestedAuthor) || string.IsNullOrWhiteSpace(candidateAuthor))
        {
            return true;
        }

        return candidateAuthor.Contains(requestedAuthor, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Known, cheap textual markers of a different product than the one
    /// requested, even when the requested title appears in it as a
    /// substring — e.g. "Summary of Debt of Honor" or "Debt of Honor /
    /// Executive Orders". Not exhaustive; see
    /// docs/family-librarian-book-matching-design-findings.md §5/§6/§8 — a
    /// title-substring match is grounds for further comparison, not
    /// automatic identity, and a derivative or combined-work title is
    /// negative evidence even when otherwise unambiguous.
    /// </summary>
    private static readonly string[] DerivativeTitleMarkers =
    [
        "summary of", "study guide", "companion to", "workbook for", "analysis of",
        "cliffsnotes", "cliff notes", "sparknotes", "excerpt", "sample chapter",
        "abridged", "omnibus", "box set", "boxed set",
    ];

    private static bool IsUnwantedVariant(string candidateTitle, string requestedTitle)
    {
        if (NormalizeForExactComparison(candidateTitle) == NormalizeForExactComparison(requestedTitle))
        {
            // An exact title match is accepted regardless of these markers --
            // e.g. a work whose own real title happens to be "Box Set".
            return false;
        }

        var lowered = candidateTitle.ToLowerInvariant();
        return DerivativeTitleMarkers.Any(lowered.Contains) ||
            lowered.Contains('/') ||
            Regex.IsMatch(lowered, @"\s&\s");
    }

    private static string NormalizeForExactComparison(string value) =>
        string.Join(' ', value.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
