using System.Text;
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
            .Where(candidate => TitleMatches(title, candidate.Title) && AuthorMatches(author, candidate.Author))
            .ToArray();

        return ResolveUnique(matches);
    }

    public bool TitleMatches(string expectedTitle, string candidateTitle)
    {
        if (string.IsNullOrWhiteSpace(expectedTitle) || string.IsNullOrWhiteSpace(candidateTitle))
        {
            return false;
        }

        var normalizedExpected = NormalizeTitle(expectedTitle);
        var normalizedCandidate = NormalizeTitle(candidateTitle);
        return normalizedExpected.Length > 0 &&
            normalizedCandidate.StartsWith(normalizedExpected, StringComparison.OrdinalIgnoreCase) &&
            !IsUnwantedVariant(candidateTitle, normalizedCandidate, normalizedExpected);
    }

    public bool AuthorMatches(string? expectedAuthor, string? candidateAuthor)
    {
        if (string.IsNullOrWhiteSpace(expectedAuthor) || string.IsNullOrWhiteSpace(candidateAuthor))
        {
            return true;
        }

        var expectedTokens = AuthorTokens(expectedAuthor);
        var candidateTokens = AuthorTokens(candidateAuthor);
        return expectedTokens.Count > 0 && expectedTokens.All(candidateTokens.Contains);
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

    private static bool IsUnwantedVariant(
        string candidateTitle, string normalizedCandidate, string normalizedExpected)
    {
        if (string.Equals(normalizedCandidate, normalizedExpected, StringComparison.OrdinalIgnoreCase))
        {
            // An exact title match is accepted regardless of these markers --
            // e.g. a work whose own real title happens to be "Box Set".
            return false;
        }

        var comparableWords = NormalizeWords(candidateTitle);
        return DerivativeTitleMarkers.Any(marker =>
                comparableWords.Contains(marker, StringComparison.OrdinalIgnoreCase)) ||
            candidateTitle.Contains('/', StringComparison.Ordinal) ||
            Regex.IsMatch(candidateTitle, @"\s&\s");
    }

    private static readonly string[] LeadingArticles = ["The ", "A ", "An "];
    private static readonly string[] TrailingArticles = [", The", ", A", ", An"];

    private static string NormalizeTitle(string value) => new(RemoveArticleVariants(value)
        .Normalize(NormalizationForm.FormKC)
        .Where(char.IsLetterOrDigit)
        .ToArray());

    private static string RemoveArticleVariants(string value)
    {
        var trimmed = value.Replace("&", " and ", StringComparison.Ordinal).Trim();

        foreach (var article in TrailingArticles)
        {
            if (trimmed.EndsWith(article, StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed[..^article.Length].TrimEnd();
                break;
            }
        }

        foreach (var article in LeadingArticles)
        {
            if (trimmed.StartsWith(article, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[article.Length..];
            }
        }

        return trimmed;
    }

    private static string NormalizeWords(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(normalized.Length);
        var previousWasWhitespace = true;
        foreach (var character in normalized)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                previousWasWhitespace = false;
            }
            else if (!previousWasWhitespace)
            {
                builder.Append(' ');
                previousWasWhitespace = true;
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static HashSet<string> AuthorTokens(string value)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var token = new StringBuilder();
        foreach (var character in value.Normalize(NormalizationForm.FormKC))
        {
            if (char.IsLetterOrDigit(character))
            {
                token.Append(char.ToUpperInvariant(character));
            }
            else if (token.Length > 0)
            {
                tokens.Add(token.ToString());
                token.Clear();
            }
        }

        if (token.Length > 0)
        {
            tokens.Add(token.ToString());
        }

        return tokens;
    }
}
