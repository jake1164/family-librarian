namespace FamilyLibrarian.Application.Matching;

/// <summary>
/// The single entry point every destination client (CWA, Audiobookshelf, ...)
/// calls to turn fetched search results into a match decision. Wraps
/// <see cref="IBookMatcher"/> with an advisory <see cref="IAmbiguityResolver"/> pass:
/// the resolver may annotate an ambiguous result (<see cref="BookMatchResult.AdvisoryPick"/>)
/// but never decides it -- only the deterministic matcher produces a <see cref="BookMatchDecision.Match"/>.
/// </summary>
public interface IBookMatchService
{
    /// <param name="title">
    /// The requested title, passed through only as context for the advisory
    /// <see cref="IAmbiguityResolver"/> — the query that produced
    /// <paramref name="candidates"/> is already identifier-scoped, so it is
    /// not used to filter them.
    /// </param>
    Task<BookMatchResult> ResolveUniqueAsync(
        string title, string? author, IReadOnlyList<CandidateBook> candidates, CancellationToken cancellationToken,
        string? acceptedLanguage = null);

    Task<BookMatchResult> MatchByTitleAuthorAsync(
        string title, string? author, IReadOnlyList<CandidateBook> candidates, CancellationToken cancellationToken,
        string? acceptedLanguage = null);
}

public sealed class BookMatchService(IBookMatcher matcher, IAmbiguityResolver ambiguityResolver) : IBookMatchService
{
    public Task<BookMatchResult> ResolveUniqueAsync(
        string title, string? author, IReadOnlyList<CandidateBook> candidates, CancellationToken cancellationToken,
        string? acceptedLanguage = null) =>
        ResolveAmbiguityAsync(
            matcher.ResolveUnique(candidates, acceptedLanguage), title, author, BookMatchBasis.Identifier, cancellationToken);

    public Task<BookMatchResult> MatchByTitleAuthorAsync(
        string title, string? author, IReadOnlyList<CandidateBook> candidates, CancellationToken cancellationToken,
        string? acceptedLanguage = null) =>
        ResolveAmbiguityAsync(
            matcher.MatchByTitleAuthor(title, author, candidates, acceptedLanguage), title, author, BookMatchBasis.TitleAuthor,
            cancellationToken);

    private async Task<BookMatchResult> ResolveAmbiguityAsync(
        BookMatchResult result, string title, string? author, BookMatchBasis basis, CancellationToken cancellationToken)
    {
        if (result.Decision == BookMatchDecision.Match)
        {
            return result with { Basis = basis };
        }

        if (result.Decision != BookMatchDecision.Ambiguous)
        {
            return result;
        }

        // The resolver is advisory only: whatever it returns is attached to the still-Ambiguous
        // result for a reviewer or later semantic analysis, and can never change the decision.
        // A pick that is not one of the candidates already offered is ignored, and a resolver
        // failure (including its own timeout) must not break deterministic matching; only the
        // caller's cancellation propagates.
        CandidateBook? pick;
        try
        {
            pick = await ambiguityResolver.ResolveAsync(title, author, result.Candidates, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return result;
        }

        return pick is not null && result.Candidates.Any(candidate => candidate.ExternalId == pick.ExternalId)
            ? result with { AdvisoryPick = pick }
            : result;
    }
}
