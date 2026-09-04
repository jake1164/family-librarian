namespace FamilyLibrarian.Application.Matching;

/// <summary>
/// Deterministic, synchronous, no-I/O matching decision over a set of
/// destination candidates already fetched by the caller. See
/// docs/family-librarian-book-matching-design-findings.md for the rationale
/// behind refusing to guess among ambiguous candidates.
/// </summary>
public interface IBookMatcher
{
    /// <summary>
    /// For identifier-scoped search results (e.g. every hit from an ISBN
    /// query): the query string itself is already the filter, so this only
    /// needs to confirm there is exactly one result.
    /// </summary>
    BookMatchResult ResolveUnique(IReadOnlyList<CandidateBook> candidates);

    /// <summary>
    /// Determines whether observed metadata can identify the expected title.
    /// The comparison normalizes Unicode, punctuation, and article placement,
    /// accepts a candidate title with an additional subtitle, and rejects known
    /// derivative or combined-work variants.
    /// </summary>
    bool TitleMatches(string expectedTitle, string candidateTitle);

    /// <summary>
    /// Determines whether observed author metadata supports an expected author.
    /// Missing metadata is unknown rather than contradictory; callers that
    /// require author evidence must require an observed value separately.
    /// </summary>
    bool AuthorMatches(string? expectedAuthor, string? candidateAuthor);

    /// <summary>
    /// For title/author search results: normalizes, filters unwanted
    /// variants (summaries, study guides, omnibuses, ...), applies the
    /// same-title/conflicting-author rule, then requires exactly one
    /// surviving candidate.
    /// </summary>
    BookMatchResult MatchByTitleAuthor(string title, string? author, IReadOnlyList<CandidateBook> candidates);
}
