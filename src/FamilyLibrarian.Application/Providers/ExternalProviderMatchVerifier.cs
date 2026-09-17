using FamilyLibrarian.Application.Matching;

namespace FamilyLibrarian.Application.Providers;

/// <summary>
/// Independently re-verifies an admin-registered external provider's
/// <c>/search</c> results against the request's own title/author/ISBN,
/// through the same <see cref="IBookMatchService"/>/<see cref="IBookMatcher"/>
/// every FL-controlled destination (CWA, Audiobookshelf) already goes
/// through. An external provider is unvetted third-party code, so its own
/// claimed title/author is never trusted as sufficient by itself (PROVIDER-1
/// plan §F2) -- a loosely-indexed source matched by keyword can plausibly
/// return the wrong book.
/// </summary>
public sealed class ExternalProviderMatchVerifier(IBookMatchService matchService, IBookMatcher matcher)
{
    /// <summary>
    /// Returns one verdict per candidate, keyed by <see cref="ExternalProviderCandidate.ProviderReference"/>.
    /// </summary>
    /// <remarks>
    /// The wire protocol has no per-candidate identifier field and issues one
    /// combined title+author+ISBN <c>/search</c> call -- unlike CWA/ABS, there
    /// is no separately-scoped identifier-only query result to trust at face
    /// value. When <paramref name="isbn13"/> was sent and exactly one
    /// candidate came back, that is treated as a tentative identifier-tier
    /// hit, but only proceeds as <see cref="BookMatchBasis.Identifier"/> once
    /// its own title/author also plausibly corroborates the request -- a
    /// provider ignoring the ISBN and returning one unrelated title must not
    /// earn frictionless trust just because it happened to return a single
    /// result. Every other case falls back to the ordinary title/author tier.
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, ExternalProviderMatchVerdict>> VerifyAsync(
        string title, string? author, string? isbn13,
        IReadOnlyList<ExternalProviderCandidate> candidates, CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return new Dictionary<string, ExternalProviderMatchVerdict>();
        }

        var candidateBooks = candidates
            .Select(candidate => new CandidateBook(candidate.ProviderReference, candidate.Title, candidate.Author))
            .ToArray();

        if (!string.IsNullOrWhiteSpace(isbn13))
        {
            var identifierResult = await matchService.ResolveUniqueAsync(title, author, candidateBooks, cancellationToken);
            if (identifierResult.Decision == BookMatchDecision.Match)
            {
                var matched = candidates.First(
                    candidate => candidate.ProviderReference == identifierResult.MatchedId);
                if (matcher.TitleMatches(title, matched.Title) &&
                    (author is null || matcher.AuthorMatches(author, matched.Author)))
                {
                    return BuildVerdicts(candidates, identifierResult.MatchedId, BookMatchBasis.Identifier, requiresLanguageConfirmation: false);
                }
            }
        }

        var titleAuthorResult = await matchService.MatchByTitleAuthorAsync(title, author, candidateBooks, cancellationToken);
        return titleAuthorResult.Decision switch
        {
            BookMatchDecision.Match => BuildVerdicts(
                candidates, titleAuthorResult.MatchedId, BookMatchBasis.TitleAuthor, requiresLanguageConfirmation: false),
            // No confirmed MatchBasis for a language-excluded result either --
            // it must be exactly as cautious as a plain non-match (never
            // eligible for frictionless use), not merely a display hint.
            BookMatchDecision.LanguageExcluded => BuildVerdicts(
                candidates, matchedId: null, basis: null, requiresLanguageConfirmation: true),
            _ => BuildVerdicts(candidates, matchedId: null, basis: null, requiresLanguageConfirmation: false)
        };
    }

    private static Dictionary<string, ExternalProviderMatchVerdict> BuildVerdicts(
        IReadOnlyList<ExternalProviderCandidate> candidates, string? matchedId, BookMatchBasis? basis,
        bool requiresLanguageConfirmation)
    {
        var verdicts = new Dictionary<string, ExternalProviderMatchVerdict>(candidates.Count);
        foreach (var candidate in candidates)
        {
            verdicts[candidate.ProviderReference] = candidate.ProviderReference == matchedId
                ? new ExternalProviderMatchVerdict(basis, RequiresLanguageConfirmation: false)
                : new ExternalProviderMatchVerdict(null, requiresLanguageConfirmation);
        }

        return verdicts;
    }
}

public sealed record ExternalProviderMatchVerdict(BookMatchBasis? Basis, bool RequiresLanguageConfirmation)
{
    public static readonly ExternalProviderMatchVerdict Unconfirmed = new(null, false);
}
