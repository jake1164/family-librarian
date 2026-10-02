using FamilyLibrarian.Application.Catalog;
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
    /// Verifies a provider response against the catalog identity, including
    /// only the Work's persisted edition-title aliases. Those aliases are
    /// catalog evidence, not provider labels and not inferred translations.
    /// </summary>
    public Task<IReadOnlyDictionary<string, ExternalProviderMatchVerdict>> VerifyAsync(
        BookIdentity identity, IReadOnlyList<ExternalProviderCandidate> candidates,
        CancellationToken cancellationToken) =>
        VerifyAsync(
            identity.Title,
            identity.AlternateTitles,
            identity.Author,
            identity.Isbn13Candidates.FirstOrDefault(),
            candidates,
            identity.Language,
            cancellationToken,
            identity.Series);

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
    public Task<IReadOnlyDictionary<string, ExternalProviderMatchVerdict>> VerifyAsync(
        string title, string? author, string? isbn13,
        IReadOnlyList<ExternalProviderCandidate> candidates, CancellationToken cancellationToken,
        string? acceptedLanguage = null) =>
        VerifyAsync(title, null, author, isbn13, candidates, acceptedLanguage, cancellationToken);

    private async Task<IReadOnlyDictionary<string, ExternalProviderMatchVerdict>> VerifyAsync(
        string title, IReadOnlyList<string>? alternateTitles, string? author, string? isbn13,
        IReadOnlyList<ExternalProviderCandidate> candidates, string? acceptedLanguage,
        CancellationToken cancellationToken, IReadOnlyList<BookSeries>? series = null)
    {
        if (candidates.Count == 0)
        {
            return new Dictionary<string, ExternalProviderMatchVerdict>();
        }

        // A source that reports only a release name (no `work` object, no
        // legacy flat title/author) leaves candidate.Title empty, which every
        // matcher rejects outright -- so its correct results and its wrong
        // ones look identical and nothing from it can ever be fulfilled. The
        // release name is the evidence that source did establish, so it is
        // read here rather than ignored.
        var expectedTitles = ExpectedTitles(title, alternateTitles).ToArray();
        // Indexer assignment, not ToDictionary: a provider is untrusted input
        // and may repeat a providerReference, which must not throw out of a
        // search that this dictionary is now built on every time.
        var releaseNames = new Dictionary<string, ReleaseNameVerdict>(candidates.Count, StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            releaseNames[candidate.ProviderReference] =
                ExternalReleaseNameEvidence.Evaluate(candidate.Release?.Name, expectedTitles, author, series);
        }

        var candidateBooks = candidates
            .Select(candidate => new CandidateBook(
                candidate.ProviderReference,
                EffectiveTitle(candidate),
                candidate.Author,
                EffectiveLanguage(candidate, releaseNames[candidate.ProviderReference])))
            .ToArray();

        if (!string.IsNullOrWhiteSpace(isbn13))
        {
            var identifierCandidates = candidates
                .Where(candidate => HasIdentifier(candidate, "isbn13", isbn13))
                .Select(candidate => new CandidateBook(
                    candidate.ProviderReference,
                    EffectiveTitle(candidate),
                    candidate.Author,
                    EffectiveLanguage(candidate, releaseNames[candidate.ProviderReference])))
                .ToArray();
            var identifierResult = await matchService.ResolveUniqueAsync(
                title, author, identifierCandidates, cancellationToken, acceptedLanguage);
            if (identifierResult.Decision == BookMatchDecision.Match)
            {
                var matched = candidates.First(
                    candidate => candidate.ProviderReference == identifierResult.MatchedId);
                if (TitleMatchesAnyExpectedTitle(title, alternateTitles, EffectiveTitle(matched)) &&
                    (author is null || matcher.AuthorMatches(author, matched.Author)))
                {
                    return BuildVerdicts(
                        candidates, releaseNames, identifierResult.MatchedId, BookMatchBasis.Identifier,
                        requiresLanguageConfirmation: false);
                }
            }
        }

        // Structured strict equivalence, or -- for a release-name-only source
        // -- a name that asserts the expected title *and* author with every
        // remaining token accounted for. A title-only assertion deliberately
        // does not qualify: same-title/missing-author is ambiguous, not a
        // match (design findings §10).
        var strictMatches = candidates
            .Where(candidate =>
                LanguageAcceptance.IsAcceptedOrUnspecified(
                    EffectiveLanguage(candidate, releaseNames[candidate.ProviderReference]), acceptedLanguage) &&
                (StrictTitleAuthorMatchesAnyExpectedTitle(title, alternateTitles, author, candidate.Title, candidate.Author) ||
                 releaseNames[candidate.ProviderReference].IsStrictWorkAssertion))
            .Select(candidate => candidate.ProviderReference)
            .ToHashSet(StringComparer.Ordinal);
        if (strictMatches.Count > 0)
        {
            return candidates.ToDictionary(
                candidate => candidate.ProviderReference,
                candidate => strictMatches.Contains(candidate.ProviderReference)
                    ? WithReleaseFacts(
                        new ExternalProviderMatchVerdict(BookMatchBasis.StrictTitleAuthor, RequiresLanguageConfirmation: false),
                        releaseNames[candidate.ProviderReference])
                    : WithReleaseFacts(
                        ExternalProviderMatchVerdict.Unconfirmed, releaseNames[candidate.ProviderReference]),
                StringComparer.Ordinal);
        }

        var titleAuthorResult = await matchService.MatchByTitleAuthorAsync(
            title, author, candidateBooks, cancellationToken, acceptedLanguage);
        return titleAuthorResult.Decision switch
        {
            BookMatchDecision.Match => BuildVerdicts(
                candidates, releaseNames, titleAuthorResult.MatchedId, BookMatchBasis.TitleAuthor,
                requiresLanguageConfirmation: false),
            // No confirmed MatchBasis for a language-excluded result either --
            // it must be exactly as cautious as a plain non-match (never
            // eligible for frictionless use), not merely a display hint.
            BookMatchDecision.LanguageExcluded => BuildVerdicts(
                candidates, releaseNames, matchedId: null, basis: null, requiresLanguageConfirmation: true),
            _ => BuildVerdicts(
                candidates, releaseNames, matchedId: null, basis: null, requiresLanguageConfirmation: false)
        };
    }

    /// <summary>
    /// The candidate's own work title when it has one, else its raw release
    /// name. Substituting the release name is what lets the ordinary
    /// title/author tier see a release-name-only source at all; that tier's
    /// existing normalizers and derivative-variant rules then judge it exactly
    /// as they judge any other candidate title.
    /// </summary>
    private static string EffectiveTitle(ExternalProviderCandidate candidate) =>
        string.IsNullOrWhiteSpace(candidate.Title)
            ? candidate.Release?.Name ?? string.Empty
            : candidate.Title;

    /// <summary>
    /// Structured edition language wins; a language word found in the release
    /// name is the fallback. Honoring the latter is not optional -- for a
    /// release-name-only source it is the sole place a foreign-language
    /// edition declares itself, and treating it as absent would read as
    /// "unspecified", which <see cref="LanguageAcceptance"/> accepts.
    /// </summary>
    private static string? EffectiveLanguage(
        ExternalProviderCandidate candidate, ReleaseNameVerdict releaseName) =>
        string.IsNullOrWhiteSpace(candidate.Edition?.Language)
            ? releaseName.AssertedLanguage
            : candidate.Edition!.Language;

    private static ExternalProviderMatchVerdict WithReleaseFacts(
        ExternalProviderMatchVerdict verdict, ReleaseNameVerdict releaseName) =>
        verdict with
        {
            ReleaseNameLanguage = releaseName.AssertedLanguage,
            ReleaseNameNarrator = releaseName.AssertedNarrator,
            ReleaseNameFormat = releaseName.AssertedFormat
        };

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

    private bool TitleMatchesAnyExpectedTitle(
        string title, IReadOnlyList<string>? alternateTitles, string candidateTitle) =>
        ExpectedTitles(title, alternateTitles).Any(expectedTitle => matcher.TitleMatches(expectedTitle, candidateTitle));

    private bool StrictTitleAuthorMatchesAnyExpectedTitle(
        string title, IReadOnlyList<string>? alternateTitles, string? author, string candidateTitle, string? candidateAuthor) =>
        ExpectedTitles(title, alternateTitles).Any(expectedTitle =>
            matcher.StrictTitleAuthorMatches(expectedTitle, author, candidateTitle, candidateAuthor));

    private static IEnumerable<string> ExpectedTitles(string title, IReadOnlyList<string>? alternateTitles) =>
        new[] { title }
            .Concat(alternateTitles ?? [])
            .Where(expectedTitle => !string.IsNullOrWhiteSpace(expectedTitle))
            .Distinct(StringComparer.Ordinal);

    private static Dictionary<string, ExternalProviderMatchVerdict> BuildVerdicts(
        IReadOnlyList<ExternalProviderCandidate> candidates,
        IReadOnlyDictionary<string, ReleaseNameVerdict> releaseNames,
        string? matchedId, BookMatchBasis? basis, bool requiresLanguageConfirmation)
    {
        var verdicts = new Dictionary<string, ExternalProviderMatchVerdict>(candidates.Count, StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            var verdict = candidate.ProviderReference == matchedId
                ? new ExternalProviderMatchVerdict(basis, RequiresLanguageConfirmation: false)
                : new ExternalProviderMatchVerdict(null, requiresLanguageConfirmation);
            verdicts[candidate.ProviderReference] = WithReleaseFacts(
                verdict,
                releaseNames.GetValueOrDefault(candidate.ProviderReference) ?? ReleaseNameVerdict.None);
        }

        return verdicts;
    }
}

/// <summary>
/// One candidate's confidence verdict, plus any fact that existed only in its
/// raw <c>release.name</c>.
/// </summary>
/// <remarks>
/// The release-name facts travel with the verdict so identity, language,
/// narration and format evidence are all derived in this one place.
/// <see cref="ExternalCandidateAvailabilityChecker"/>'s own documentation
/// commits to confidence being computed once; re-parsing the release name at
/// each consumer would break that and let two call sites disagree about what
/// the same string said.
/// </remarks>
public sealed record ExternalProviderMatchVerdict(
    BookMatchBasis? Basis,
    bool RequiresLanguageConfirmation,
    string? ReleaseNameLanguage = null,
    string? ReleaseNameNarrator = null,
    string? ReleaseNameFormat = null)
{
    public static readonly ExternalProviderMatchVerdict Unconfirmed = new(null, false);
}
