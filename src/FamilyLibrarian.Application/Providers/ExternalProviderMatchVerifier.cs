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
            identity.Series, identity);

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
        CancellationToken cancellationToken, IReadOnlyList<BookSeries>? series = null, BookIdentity? requestedMetadata = null)
    {
        if (candidates.Count == 0)
        {
            return new Dictionary<string, ExternalProviderMatchVerdict>();
        }

        var identity = requestedMetadata ?? new BookIdentity(title, author, isbn13 is null ? [] : [isbn13],
            Language: acceptedLanguage, Series: series, AlternateTitles: alternateTitles);
        var assessments = candidates.GroupBy(candidate => candidate.ProviderReference, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => DeterministicCandidateIdentityResolver.Assess(identity, group.First()), StringComparer.Ordinal);
        string? identifierMatch = null;
        if (!string.IsNullOrWhiteSpace(isbn13))
        {
            var corroborated = candidates.Where(candidate => HasIdentifier(candidate, "isbn13", isbn13) &&
                assessments[candidate.ProviderReference] is { HasStrongTitle: true, Decision: not WorkIdentityDecision.Mismatch })
                .Select(candidate => new CandidateBook(candidate.ProviderReference, EffectiveTitle(candidate), candidate.Author,
                    candidate.Edition?.Language ?? assessments[candidate.ProviderReference].ReleaseEvidence.AssertedLanguage)).ToArray();
            var result = await matchService.ResolveUniqueAsync(title, author, corroborated, cancellationToken, acceptedLanguage);
            if (result.Decision == BookMatchDecision.Match) identifierMatch = result.MatchedId;
        }
        var verdicts = new Dictionary<string, ExternalProviderMatchVerdict>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var assessment = assessments[candidate.ProviderReference];
            BookMatchBasis? basis = null;
            if (assessment.Decision is WorkIdentityDecision.Match or WorkIdentityDecision.MatchWithConditions)
                basis = candidate.ProviderReference == identifierMatch ? BookMatchBasis.Identifier :
                    assessment.AuthorEvidence.HasStrongSupport ? BookMatchBasis.StrictTitleAuthor : BookMatchBasis.StrictTitle;
            else if (assessment.Decision == WorkIdentityDecision.Ambiguous && candidates.Count == 1 &&
                !string.IsNullOrWhiteSpace(candidate.Title) &&
                TitleMatchesAnyExpectedTitle(title, alternateTitles, candidate.Title))
                basis = BookMatchBasis.TitleAuthor;
            var release = assessment.ReleaseEvidence;
            verdicts[candidate.ProviderReference] = new ExternalProviderMatchVerdict(basis,
                assessment.LanguageEvidence == IdentityEvidenceState.Conflicting,
                release.AssertedLanguage, release.AssertedNarrator, release.AssertedFormat,
                assessment.AuthorEvidence, assessment.TitleEvidence.IsPositive, release.Part, assessment);
        }
        return verdicts;
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

    private static IEnumerable<string> ExpectedTitles(string title, IReadOnlyList<string>? alternateTitles) =>
        new[] { title }
            .Concat(alternateTitles ?? [])
            .Where(expectedTitle => !string.IsNullOrWhiteSpace(expectedTitle))
            .Distinct(StringComparer.Ordinal);


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
    string? ReleaseNameFormat = null,
    AuthorAffinityResult? AuthorAffinity = null,
    bool HasPlausibleTitle = false,
    ExternalAudiobookPartEvidence? AudiobookPart = null,
    CandidateIdentityAssessment? IdentityAssessment = null)
{
    public static readonly ExternalProviderMatchVerdict Unconfirmed = new(null, false);
}
