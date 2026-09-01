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
                .OrderBy(candidate => candidate.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate => GetFirstAuthor(candidate), StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        var titleMatches = groupedCandidates
            .Select(candidate => new { Candidate = candidate, Match = GetTitleMatch(candidate.Title, searchText) })
            .ToArray();

        // A precise title match is a much stronger signal than the broad matches
        // providers often return. Keep edition/capitalization variants, but avoid
        // presenting unrelated partial, translated, or keyword-only results.
        if (titleMatches.Any(result => result.Match == TitleMatch.Exact))
        {
            return titleMatches
                .Where(result => result.Match == TitleMatch.Exact)
                .OrderByDescending(result => GetCompletenessScore(result.Candidate))
                .ThenBy(result => result.Candidate.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(result => GetFirstAuthor(result.Candidate), StringComparer.OrdinalIgnoreCase)
                .Select(result => result.Candidate)
                .ToArray();
        }

        return titleMatches
            .OrderByDescending(result => result.Match)
            .ThenBy(result => result.Candidate.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(result => GetFirstAuthor(result.Candidate), StringComparer.OrdinalIgnoreCase)
            .Select(result => result.Candidate)
            .ToArray();
    }

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

    private static TitleMatch GetTitleMatch(string title, string searchText)
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
