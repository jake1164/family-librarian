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
    public BookMatchResult ResolveUnique(IReadOnlyList<CandidateBook> candidates, string? acceptedLanguage = null)
    {
        if (candidates.Count == 0)
        {
            return BookMatchResult.NoMatchResult;
        }

        var eligible = candidates.Where(candidate =>
                LanguageAcceptance.IsAcceptedOrUnspecified(candidate.Language, acceptedLanguage))
            .ToArray();
        if (eligible.Length == 0)
        {
            // Every otherwise-matching candidate was excluded for language --
            // distinct from "nothing found," so a caller can hand this to a
            // preference decision instead of silently forcing a wrong-language
            // match through or losing the fact that something was found.
            return BookMatchResult.LanguageExcluded(candidates);
        }

        return eligible.Length switch
        {
            1 => BookMatchResult.Match(eligible[0]),
            _ => BookMatchResult.Ambiguous(eligible)
        };
    }

    public BookMatchResult MatchByTitleAuthor(
        string title, string? author, IReadOnlyList<CandidateBook> candidates, string? acceptedLanguage = null)
    {
        var matches = candidates
            .Where(candidate => TitleMatches(title, candidate.Title, author) && AuthorMatches(author, candidate.Author))
            .ToArray();

        return ResolveUnique(matches, acceptedLanguage);
    }

    public bool TitleMatches(string expectedTitle, string candidateTitle, string? expectedAuthor = null)
    {
        if (string.IsNullOrWhiteSpace(expectedTitle) || string.IsNullOrWhiteSpace(candidateTitle))
        {
            return false;
        }

        if (TitlePrefixMatches(expectedTitle, candidateTitle))
        {
            return true;
        }

        // The expected title is the catalog's, and a catalog title carries
        // edition packaging the source's release title does not -- the whole
        // reason "Moby Dick (Illustrated Classics)" matched nothing while
        // "Moby Dick; Or, The Whale" sat in the index. Compare the
        // work-identifying core too. Author agreement is still required
        // separately by every caller, so this widens what can be compared
        // without letting a bare title decide identity.
        var expectedCore = WorkTitleCore.Reduce(expectedTitle, expectedAuthor);
        return !string.Equals(expectedCore, expectedTitle.Trim(), StringComparison.Ordinal) &&
            TitlePrefixMatches(expectedCore, candidateTitle);
    }

    private static bool TitlePrefixMatches(string expectedTitle, string candidateTitle)
    {
        var normalizedExpected = NormalizeTitle(expectedTitle);
        var normalizedCandidate = NormalizeTitle(candidateTitle);
        return normalizedExpected.Length > 0 &&
            normalizedCandidate.StartsWith(normalizedExpected, StringComparison.OrdinalIgnoreCase) &&
            !IsUnwantedVariant(candidateTitle, normalizedCandidate, normalizedExpected);
    }

    public bool StrictTitleAuthorMatches(
        string expectedTitle, string? expectedAuthor, string candidateTitle, string? candidateAuthor)
    {
        if (string.IsNullOrWhiteSpace(expectedTitle) || string.IsNullOrWhiteSpace(expectedAuthor) ||
            string.IsNullOrWhiteSpace(candidateTitle))
        {
            return false;
        }

        var normalizedExpectedTitle = NormalizeTitle(expectedTitle);
        if (normalizedExpectedTitle.Length == 0)
        {
            return false;
        }

        // Ordinary provider metadata: the candidate's work title and author
        // are separate fields and both must be exact under the deterministic
        // normalizers. This is intentionally stricter than TitleMatches.
        if (string.Equals(normalizedExpectedTitle, NormalizeTitle(candidateTitle), StringComparison.OrdinalIgnoreCase))
        {
            return AuthorTokensEqual(expectedAuthor, candidateAuthor);
        }

        // The same exactness, with edition packaging removed from the
        // *expected* title only. A catalog title is FL's own record of what
        // was requested and routinely carries an imprint or an embedded
        // author ("Moby Dick (Illustrated Classics)", "Moby Dick by Herman
        // Melville"); without this, requesting any such title made the work
        // permanently unacquirable even with the plain record in hand.
        //
        // The candidate's title is deliberately NOT reduced here. An extra
        // subtitle on the *source* side is unverified third-party text and
        // stays in the weaker, confirmation-requiring TitleAuthor tier --
        // "The Hobbit: A Novel" is probably the right book, "Debt of Honor /
        // Executive Orders" is not, and nothing here can tell them apart.
        // The author must still be exactly token-equal, so this remains an
        // equality rule rather than a prefix one.
        var expectedCore = NormalizeTitle(WorkTitleCore.Reduce(expectedTitle, expectedAuthor));
        if (expectedCore.Length > 0 &&
            expectedCore.Length != normalizedExpectedTitle.Length &&
            string.Equals(expectedCore, NormalizeTitle(candidateTitle), StringComparison.OrdinalIgnoreCase) &&
            AuthorTokensEqual(expectedAuthor, candidateAuthor))
        {
            return true;
        }

        // A common source-record spelling embeds the author in the title
        // ("Net force by Tom Clancy"). Treat it as equivalent only if the
        // title before that marker is exact and the observed suffix agrees;
        // a contradictory structured author remains a rejection.
        var byMarker = candidateTitle.LastIndexOf(" by ", StringComparison.OrdinalIgnoreCase);
        if (byMarker <= 0)
        {
            return false;
        }

        var titlePart = candidateTitle[..byMarker];
        var authorPart = candidateTitle[(byMarker + 4)..];
        return string.Equals(normalizedExpectedTitle, NormalizeTitle(titlePart), StringComparison.OrdinalIgnoreCase) &&
            AuthorTokensEqual(expectedAuthor, authorPart) &&
            (string.IsNullOrWhiteSpace(candidateAuthor) || AuthorTokensEqual(expectedAuthor, candidateAuthor));
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

        return HasDerivativeOrCombinedWorkMarker(candidateTitle);
    }

    /// <summary>
    /// The raw-text negative evidence shared by title matching and external
    /// release-name verification: a known derivative marker, a <c>/</c>
    /// combined-work separator, or a spaced ampersand. Separated from
    /// <see cref="IsUnwantedVariant"/> only so the release-name verifier
    /// applies the identical rule rather than a second copy of it; callers
    /// that need the exact-title escape must still go through
    /// <see cref="IsUnwantedVariant"/>.
    /// </summary>
    internal static bool HasDerivativeOrCombinedWorkMarker(string value)
    {
        var comparableWords = NormalizeWords(value);
        return DerivativeTitleMarkers.Any(marker =>
                comparableWords.Contains(marker, StringComparison.OrdinalIgnoreCase)) ||
            value.Contains('/', StringComparison.Ordinal) ||
            Regex.IsMatch(value, @"\s&\s");
    }

    private static readonly string[] LeadingArticles = ["The ", "A ", "An "];
    private static readonly string[] TrailingArticles = [", The", ", A", ", An"];

    internal static string NormalizeTitle(string value) => new(RemoveArticleVariants(value)
        .Normalize(NormalizationForm.FormKC)
        .Where(char.IsLetterOrDigit)
        .ToArray());

    internal static string RemoveArticleVariants(string value)
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

    internal static string NormalizeWords(string value)
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

    internal static HashSet<string> AuthorTokens(string value)
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

    private static bool AuthorTokensEqual(string? expectedAuthor, string? candidateAuthor)
    {
        if (string.IsNullOrWhiteSpace(expectedAuthor) || string.IsNullOrWhiteSpace(candidateAuthor))
        {
            return false;
        }

        var expectedTokens = AuthorTokens(expectedAuthor);
        var candidateTokens = AuthorTokens(candidateAuthor);
        return expectedTokens.Count > 0 && expectedTokens.SetEquals(candidateTokens);
    }
}
