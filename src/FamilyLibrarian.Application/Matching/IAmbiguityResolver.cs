namespace FamilyLibrarian.Application.Matching;

/// <summary>
/// The seam for a future semantic/LLM-assisted disambiguator (see
/// docs/family-librarian-book-matching-design-findings.md §15-19): given a
/// set of candidates the deterministic matcher could not tell apart,
/// optionally pick the one that represents the requested work.
/// </summary>
/// <remarks>
/// Advisory only — per §18/§19, a resolver's pick should not be treated as
/// proof; <see cref="IBookMatchService"/> promotes a non-null pick straight
/// to <see cref="BookMatchDecision.Match"/>, so a real implementation must
/// only return non-null when it is confident, and return <c>null</c>
/// (leaving the result <see cref="BookMatchDecision.Ambiguous"/>) otherwise.
/// </remarks>
public interface IAmbiguityResolver
{
    Task<CandidateBook?> ResolveAsync(
        string title, string? author, IReadOnlyList<CandidateBook> candidates, CancellationToken cancellationToken);
}

/// <summary>The default resolver: no semantic matching is wired up yet, so ambiguity always stays ambiguous.</summary>
public sealed class NoOpAmbiguityResolver : IAmbiguityResolver
{
    public Task<CandidateBook?> ResolveAsync(
        string title, string? author, IReadOnlyList<CandidateBook> candidates, CancellationToken cancellationToken) =>
        Task.FromResult<CandidateBook?>(null);
}
