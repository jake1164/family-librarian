namespace FamilyLibrarian.Application.Matching;

/// <summary>
/// The seam for a future semantic/LLM-assisted disambiguator: given a
/// set of candidates the deterministic matcher could not tell apart,
/// optionally suggest the one that seems to represent the requested work.
/// </summary>
/// <remarks>
/// Advisory only -- a resolver's pick is not proof.
/// <see cref="IBookMatchService"/> attaches a returned candidate to the result as
/// <see cref="BookMatchResult.AdvisoryPick"/> and leaves the result
/// <see cref="BookMatchDecision.Ambiguous"/>; it never promotes the pick to a
/// <see cref="BookMatchDecision.Match"/>. Return <c>null</c> when there is no suggestion.
/// Model-backed analysis deliberately does not use this seam (alpha.6 D-7): it records evidence-cited findings beside the
/// deterministic result instead of naming a candidate.
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
