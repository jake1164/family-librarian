namespace FamilyLibrarian.Application.Matching;

/// <summary>
/// The single entry point every destination client (CWA, Audiobookshelf, ...)
/// calls to turn fetched search results into a match decision. Wraps
/// <see cref="IBookMatcher"/> with an <see cref="IAmbiguityResolver"/> pass,
/// so registering a real resolver upgrades every consumer at once.
/// </summary>
public interface IBookMatchService
{
    /// <param name="title">
    /// The requested title, passed through only as context for a future
    /// <see cref="IAmbiguityResolver"/> — the query that produced
    /// <paramref name="candidates"/> is already identifier-scoped, so it is
    /// not used to filter them.
    /// </param>
    Task<BookMatchResult> ResolveUniqueAsync(
        string title, string? author, IReadOnlyList<CandidateBook> candidates, CancellationToken cancellationToken);

    Task<BookMatchResult> MatchByTitleAuthorAsync(
        string title, string? author, IReadOnlyList<CandidateBook> candidates, CancellationToken cancellationToken);
}

public sealed class BookMatchService(IBookMatcher matcher, IAmbiguityResolver ambiguityResolver) : IBookMatchService
{
    public Task<BookMatchResult> ResolveUniqueAsync(
        string title, string? author, IReadOnlyList<CandidateBook> candidates, CancellationToken cancellationToken) =>
        ResolveAmbiguityAsync(matcher.ResolveUnique(candidates), title, author, cancellationToken);

    public Task<BookMatchResult> MatchByTitleAuthorAsync(
        string title, string? author, IReadOnlyList<CandidateBook> candidates, CancellationToken cancellationToken) =>
        ResolveAmbiguityAsync(matcher.MatchByTitleAuthor(title, author, candidates), title, author, cancellationToken);

    private async Task<BookMatchResult> ResolveAmbiguityAsync(
        BookMatchResult result, string title, string? author, CancellationToken cancellationToken)
    {
        if (result.Decision != BookMatchDecision.Ambiguous)
        {
            return result;
        }

        var resolved = await ambiguityResolver.ResolveAsync(title, author, result.Candidates, cancellationToken);
        return resolved is null ? result : BookMatchResult.Match(resolved);
    }
}
