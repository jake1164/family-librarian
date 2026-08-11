namespace FamilyLibrarian.Application.Catalog;

public static class BookCandidateGrouper
{
    public static IReadOnlyList<BookCandidate> GroupExactIsbnMatches(
        IEnumerable<BookCandidate> candidates) =>
        candidates
            .GroupBy(GetExactMatchKey, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(GetCompletenessScore)
                .ThenBy(candidate => candidate.ProviderId, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.ExternalId, StringComparer.Ordinal)
                .First())
            .OrderBy(candidate => candidate.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => GetFirstAuthor(candidate), StringComparer.OrdinalIgnoreCase)
            .ToArray();

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
        candidate.Authors.Count +
        candidate.Editions.Count +
        candidate.Series.Count;

    private static string? GetFirstAuthor(BookCandidate candidate) =>
        candidate.Authors.Count == 0 ? null : candidate.Authors[0];
}
