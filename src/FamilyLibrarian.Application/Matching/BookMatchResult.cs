namespace FamilyLibrarian.Application.Matching;

public enum BookMatchDecision
{
    Match,
    Ambiguous,
    NoMatch
}

/// <summary>
/// How confidently a <see cref="BookMatchDecision.Match"/> was made --
/// <see cref="Identifier"/> for an ISBN (or other effectively-unique
/// identifier) lookup, <see cref="TitleAuthor"/> for the normalized
/// title/author fallback. A title/author match is a reviewable fallback, not
/// a verified identity: see
/// .ai_docs/cwa-topology-and-delivery-design-review.md item 4. Consumers that
/// treat an owned match as safe to act on automatically (e.g. the Kindle
/// existing-book send) should require explicit confirmation for
/// <see cref="TitleAuthor"/> matches.
/// </summary>
public enum BookMatchBasis
{
    Identifier,
    TitleAuthor
}

/// <summary>
/// The outcome of matching a requested title/author (or identifier) against a
/// destination's search results.
/// </summary>
/// <remarks>
/// <see cref="Candidates"/> is empty for <see cref="BookMatchDecision.NoMatch"/>,
/// holds every conflicting candidate for <see cref="BookMatchDecision.Ambiguous"/>
/// (so a future <see cref="IAmbiguityResolver"/> or an admin review screen has
/// something to reason about), and holds the single matched candidate for
/// <see cref="BookMatchDecision.Match"/>. <see cref="Basis"/> is set only for
/// a <see cref="BookMatchDecision.Match"/>, by <see cref="IBookMatchService"/>
/// (the one place that knows which lookup tier produced <paramref name="Candidates"/>).
/// </remarks>
public sealed record BookMatchResult(
    BookMatchDecision Decision, string? MatchedId, IReadOnlyList<CandidateBook> Candidates,
    BookMatchBasis? Basis = null)
{
    public static readonly BookMatchResult NoMatchResult = new(BookMatchDecision.NoMatch, null, []);

    public static BookMatchResult Match(CandidateBook candidate) =>
        new(BookMatchDecision.Match, candidate.ExternalId, [candidate]);

    public static BookMatchResult Ambiguous(IReadOnlyList<CandidateBook> candidates) =>
        new(BookMatchDecision.Ambiguous, null, candidates);
}
