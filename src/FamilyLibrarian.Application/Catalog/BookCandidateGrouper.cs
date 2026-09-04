namespace FamilyLibrarian.Application.Catalog;

public static class BookCandidateGrouper
{
    public static IReadOnlyList<BookCandidate> GroupExactIsbnMatches(
        IEnumerable<BookCandidate> candidates) =>
        GroupExactIsbnMatches(candidates, null);

    public static IReadOnlyList<BookCandidate> GroupExactIsbnMatches(
        IEnumerable<BookCandidate> candidates,
        string? searchText)
    {
        var groupedCandidates = candidates
            .GroupBy(GetExactMatchKey, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(GetCompletenessScore)
                .ThenBy(candidate => candidate.ProviderId, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.ExternalId, StringComparer.Ordinal)
                .First())
            .ToArray();

        if (string.IsNullOrWhiteSpace(searchText))
        {
            return groupedCandidates
                .OrderByDescending(GetLanguageRank)
                .ThenBy(candidate => candidate.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate => GetFirstAuthor(candidate), StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return groupedCandidates
            .OrderByDescending(candidate => GetMatchKind(candidate, searchText))
            .ThenByDescending(GetLanguageRank)
            .ThenBy(candidate => candidate.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => GetFirstAuthor(candidate), StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // No per-user/global language preference exists yet, so this is a fixed
    // default rather than a setting. Unknown-language candidates are treated
    // as neutral (not demoted) since we can't confirm they're a mismatch.
    private const string DefaultPreferredLanguage = "en";

    private static int GetLanguageRank(BookCandidate candidate) =>
        candidate.Language is null ||
        string.Equals(candidate.Language, DefaultPreferredLanguage, StringComparison.OrdinalIgnoreCase)
            ? 1
            : 0;

    private static string GetExactMatchKey(BookCandidate candidate)
    {
        var isbn13s = candidate.Editions
            .Select(edition => edition.Isbn13)
            .Where(isbn13 => !string.IsNullOrWhiteSpace(isbn13))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        // An exact shared ISBN is authoritative. All other candidates intentionally
        // retain their provider-specific identity for user review.
        return isbn13s.Length == 1
            ? $"isbn:{isbn13s[0]}"
            : $"provider:{candidate.ProviderId}:{candidate.ExternalId}";
    }

    private static int GetCompletenessScore(BookCandidate candidate) =>
        (string.IsNullOrWhiteSpace(candidate.Description) ? 0 : 2) +
        (string.IsNullOrWhiteSpace(candidate.CoverUrl) ? 0 : 1) +
        (string.IsNullOrWhiteSpace(candidate.Publisher) ? 0 : 1) +
        (candidate.PageCount is null ? 0 : 1) +
        (candidate.Subjects.Count == 0 ? 0 : 1) +
        candidate.Authors.Count +
        candidate.Editions.Count +
        candidate.Series.Count;

    private static string? GetFirstAuthor(BookCandidate candidate) =>
        candidate.Authors.Count == 0 ? null : candidate.Authors[0];

    public static BookCandidateMatchKind GetMatchKind(BookCandidate candidate, string searchText)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var titleMatch = GetTextMatch(candidate.Title, searchText);
        var authorMatch = candidate.Authors
            .Select(author => GetTextMatch(author, searchText))
            .DefaultIfEmpty(TitleMatch.None)
            .Max();
        var match = titleMatch > authorMatch ? titleMatch : authorMatch;

        return match switch
        {
            TitleMatch.Exact => BookCandidateMatchKind.Exact,
            TitleMatch.StartsWithSearch => BookCandidateMatchKind.Close,
            _ => BookCandidateMatchKind.Other
        };
    }

    private static TitleMatch GetTextMatch(string title, string searchText)
    {
        var normalizedTitle = NormalizeForSearch(title);
        var normalizedSearchText = NormalizeForSearch(searchText);
        if (normalizedTitle.Length == 0 || normalizedSearchText.Length == 0)
        {
            return TitleMatch.None;
        }

        var comparableTitle = TrimLeadingArticle(normalizedTitle);
        var comparableSearchText = TrimLeadingArticle(normalizedSearchText);
        if (string.Equals(comparableTitle, comparableSearchText, StringComparison.Ordinal))
        {
            return TitleMatch.Exact;
        }

        if (comparableTitle.StartsWith(
                string.Concat(comparableSearchText, " "),
                StringComparison.Ordinal))
        {
            return TitleMatch.StartsWithSearch;
        }

        return comparableTitle.Contains(comparableSearchText, StringComparison.Ordinal)
            ? TitleMatch.ContainsSearch
            : TitleMatch.None;
    }

    private static string NormalizeForSearch(string value)
    {
        var characters = value
            .Trim()
            .ToUpperInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : ' ')
            .ToArray();

        return string.Join(' ', new string(characters)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string TrimLeadingArticle(string value)
    {
        foreach (var article in new[] { "A ", "AN ", "THE " })
        {
            if (value.StartsWith(article, StringComparison.Ordinal))
            {
                return value[article.Length..];
            }
        }

        return value;
    }

    private enum TitleMatch
    {
        None,
        ContainsSearch,
        StartsWithSearch,
        Exact
    }
}

public enum BookCandidateMatchKind
{
    Other,
    Close,
    Exact
}
