namespace FamilyLibrarian.Application.Matching;

public enum BookMatchDecision
{
    Match,
    Ambiguous,
    NoMatch
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
/// <see cref="BookMatchDecision.Match"/>.
/// </remarks>
public sealed record BookMatchResult(
    BookMatchDecision Decision, string? MatchedId, IReadOnlyList<CandidateBook> Candidates)
{
    public static readonly BookMatchResult NoMatchResult = new(BookMatchDecision.NoMatch, null, []);

    public static BookMatchResult Match(CandidateBook candidate) =>
        new(BookMatchDecision.Match, candidate.ExternalId, [candidate]);

    public static BookMatchResult Ambiguous(IReadOnlyList<CandidateBook> candidates) =>
        new(BookMatchDecision.Ambiguous, null, candidates);
}
