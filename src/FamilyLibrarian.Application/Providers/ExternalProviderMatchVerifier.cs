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
    /// Identifier confidence comes from the candidate's own structured
    /// <c>work.identifiers</c> or <c>edition.identifiers</c> evidence, never
    /// from the fact that an ISBN happened to be included in the search query
    /// or that a provider happened to return one result. A matching identifier
    /// is still corroborated with title/author evidence so a bad provider
    /// record cannot turn a query echo into an unattended download. Every
    /// other result falls back to the ordinary title/author tier.
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, ExternalProviderMatchVerdict>> VerifyAsync(
        string title, string? author, string? isbn13,
        IReadOnlyList<ExternalProviderCandidate> candidates, CancellationToken cancellationToken,
        string? acceptedLanguage = null)
    {
        if (candidates.Count == 0)
        {
            return new Dictionary<string, ExternalProviderMatchVerdict>();
        }

        var candidateBooks = candidates
            .Select(candidate => new CandidateBook(
                candidate.ProviderReference, candidate.Title, candidate.Author, candidate.Edition?.Language))
            .ToArray();

        if (!string.IsNullOrWhiteSpace(isbn13))
        {
            var identifierCandidates = candidates
                .Where(candidate => HasIdentifier(candidate, "isbn13", isbn13))
                .Select(candidate => new CandidateBook(
                    candidate.ProviderReference, candidate.Title, candidate.Author, candidate.Edition?.Language))
                .ToArray();
            var identifierResult = await matchService.ResolveUniqueAsync(
                title, author, identifierCandidates, cancellationToken, acceptedLanguage);
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

        var strictMatches = candidates
            .Where(candidate =>
                LanguageAcceptance.IsAcceptedOrUnspecified(candidate.Edition?.Language, acceptedLanguage) &&
                matcher.StrictTitleAuthorMatches(title, author, candidate.Title, candidate.Author))
            .Select(candidate => candidate.ProviderReference)
            .ToHashSet(StringComparer.Ordinal);
        if (strictMatches.Count > 0)
        {
            return candidates.ToDictionary(
                candidate => candidate.ProviderReference,
                candidate => strictMatches.Contains(candidate.ProviderReference)
                    ? new ExternalProviderMatchVerdict(BookMatchBasis.StrictTitleAuthor, RequiresLanguageConfirmation: false)
                    : ExternalProviderMatchVerdict.Unconfirmed,
                StringComparer.Ordinal);
        }

        var titleAuthorResult = await matchService.MatchByTitleAuthorAsync(
            title, author, candidateBooks, cancellationToken, acceptedLanguage);
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

    private static bool HasIdentifier(ExternalProviderCandidate candidate, string scheme, string expectedValue) =>
        candidate.Work.Identifiers
            .Concat(candidate.Edition?.Identifiers ?? [])
            .Any(identifier =>
                string.Equals(identifier.Scheme, scheme, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(NormalizeIdentifier(identifier.Value), NormalizeIdentifier(expectedValue), StringComparison.Ordinal));

    private static string NormalizeIdentifier(string value) => new(value
        .Where(char.IsLetterOrDigit)
        .Select(char.ToUpperInvariant)
        .ToArray());

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
