using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Domain.Requests;
using System.Runtime.CompilerServices;

namespace FamilyLibrarian.Application.Catalog;

/// <summary>
/// Fans a <see cref="BookIdentity"/> out across every enabled admin
/// -registered external provider. Deliberately not an
/// <see cref="IDirectAcquisitionProvider"/> itself -- <c>DirectAcquisitionService</c>
/// relies on <see cref="IDirectAcquisitionProvider.Id"/> being one
/// compile-time provider per class, while an external provider is an
/// arbitrary-cardinality, admin-registered row; folding the two together
/// would collide that assumption. Same unified <see cref="FulfillmentOption"/>
/// shape as every other direct-acquisition source (Project Gutenberg
/// included) — an external provider's results need no special handling
/// anywhere downstream (UI, recommendation policy, acquire endpoint).
/// </summary>
public sealed class ExternalCandidateAvailabilityChecker(
    Providers.IExternalProviderStore externalProviders,
    Providers.IExternalProviderClient externalProviderClient,
    Providers.ExternalProviderMatchVerifier matchVerifier,
    ICredentialProtector protector,
    ExternalSearchCoalescer? coalescer = null)
{
    /// <summary>
    /// A provider whose search call fails is silently skipped — same
    /// "degrade to no results" posture the rest of the fulfillment pipeline
    /// already has. Returned options carry <see cref="FulfillmentOption.WorkId"/>
    /// as <see cref="Guid.Empty"/>; a caller resolving for a real Work should
    /// remap it.
    /// </summary>
    public async Task<IReadOnlyList<FulfillmentOption>> FindAsync(
        BookIdentity identity, RequestMediaType mediaType, CancellationToken cancellationToken)
    {
        var found = new List<FulfillmentOption>();
        await foreach (var update in FindUpdatesAsync(identity, mediaType, cancellationToken))
        {
            found.AddRange(update);
        }

        return found;
    }

    /// <summary>
    /// Emits one completed external provider at a time. This allows the
    /// requester-safe availability run to publish a fast source without
    /// waiting for another registered source.
    /// </summary>
    public async IAsyncEnumerable<IReadOnlyList<FulfillmentOption>> FindUpdatesAsync(
        BookIdentity identity,
        RequestMediaType mediaType,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var enabled = await externalProviders.ListEnabledAsync(cancellationToken);
        if (enabled.Count == 0)
        {
            yield break;
        }

        // Each external source is independent. Waiting for one slow source
        // before asking the next would turn a source outage into a delay for
        // all of them, and is especially wrong for browser enrichment.
        var pending = enabled.Select(provider =>
            FindProviderSafelyAsync(provider, identity, mediaType, cancellationToken)).ToList();
        while (pending.Count > 0)
        {
            var completed = await Task.WhenAny(pending);
            pending.Remove(completed);
            var options = await completed;
            if (options.Count > 0)
            {
                yield return options;
            }
        }
    }

    private async Task<IReadOnlyList<FulfillmentOption>> FindProviderSafelyAsync(
        Domain.Providers.ExternalProvider provider,
        BookIdentity identity,
        RequestMediaType mediaType,
        CancellationToken cancellationToken)
    {
        try
        {
            // This is the browsing path (search badges, work pages), where
            // several callers routinely ask the same provider the same thing.
            // Acquisition and rechecks call FindForProviderAsync directly and
            // always get a fresh response.
            return coalescer is null
                ? await FindForProviderAsync(provider, identity, mediaType, cancellationToken)
                : await coalescer.GetOrAddAsync(
                    ExternalSearchCoalescer.CreateKey(provider, identity, mediaType),
                    token => FindForProviderAsync(provider, identity, mediaType, token),
                    cancellationToken);
        }
        catch (HttpRequestException)
        {
            return [];
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return [];
        }
    }

    /// <summary>
    /// Searches one already-enabled provider and independently re-verifies
    /// every result against <paramref name="identity"/> via
    /// <see cref="Providers.ExternalProviderMatchVerifier"/> before stamping
    /// <see cref="FulfillmentOption.MatchBasis"/>/<see cref="FulfillmentOption.RequiresLanguageConfirmation"/>
    /// — the single place that confidence is computed, so every caller
    /// (this class's own <see cref="FindAsync"/> and a caller re-deriving one
    /// specific option before fetching it, e.g. <c>DirectAcquisitionService</c>)
    /// sees the same stamp with no duplicate match calls. Does not itself
    /// catch <see cref="HttpRequestException"/>/<see cref="OperationCanceledException"/>
    /// — a caller re-deriving a single option to fetch needs the real error,
    /// while <see cref="FindAsync"/>'s own aggregation loop degrades it.
    /// </summary>
    /// <param name="excludedProviderResultIds">
    /// Provider results the caller has already tried and ruled out, so the
    /// confirmed single candidate is nominated from what is left. Null/empty
    /// for every browsing caller, which has nothing to exclude.
    /// </param>
    public async Task<IReadOnlyList<FulfillmentOption>> FindForProviderAsync(
        Domain.Providers.ExternalProvider provider, BookIdentity identity,
        RequestMediaType mediaType, CancellationToken cancellationToken,
        IReadOnlySet<string>? excludedProviderResultIds = null)
    {
        var apiKey = provider.HasApiKey
            ? protector.Unprotect(
                Providers.ExternalProviderSecretPurposes.ApiKey, provider.ProtectedApiKey!, provider.ApiKeyFormatVersion)
            : null;

        var isbn13 = identity.Isbn13Candidates.FirstOrDefault();
        var work = new Providers.ExternalProviderWorkEvidence(
            identity.Title,
            Subtitle: null,
            Authors: identity.Authors ?? (identity.Author is null
                ? []
                : [new Providers.BookAuthor(identity.Author, "author")]),
            Series: identity.Series ?? [],
            Identifiers: []);
        var edition = new Providers.ExternalProviderEditionEvidence(
            identity.Language,
            identity.PublicationYear,
            identity.Publisher,
            Identifiers: isbn13 is null ? [] : [new Providers.BookIdentifier("isbn13", isbn13)]);

        var candidates = await externalProviderClient.SearchAsync(
            provider.BaseUrl,
            apiKey,
            new Providers.ExternalProviderSearchRequest(
                Guid.NewGuid(), mediaType, work, edition,
                mediaType == RequestMediaType.Ebook
                    ? new Providers.ExternalProviderSearchConstraints(Formats: Providers.ExternalEbookFormatPolicy.SearchFormats)
                    : null),
            cancellationToken);

        if (candidates.Count == 0)
        {
            return [];
        }

        var verdicts = await matchVerifier.VerifyAsync(identity, candidates, cancellationToken);

        var candidatesWithVerdicts = candidates.Select(candidate =>
        {
            var verdict = verdicts.GetValueOrDefault(candidate.ProviderReference, Providers.ExternalProviderMatchVerdict.Unconfirmed);
            var releaseVerdict = Providers.ExternalReleasePolicy.Evaluate(candidate.Release, mediaType);
            return (Candidate: candidate, MatchVerdict: verdict, ReleaseVerdict: releaseVerdict);
        })
            .Where(candidate => !candidate.ReleaseVerdict.IsRejected)
            .ToArray();

        // Do not put a requester in front of obvious foreign-language copies
        // when the requested/default language has matching candidates. The
        // default comes from LanguageAcceptance, not a hard-coded provider
        // filter, so a future/requested non-English work keeps its own path.
        var acceptedLanguageCandidates = candidatesWithVerdicts
            .Where(candidate => Matching.LanguageAcceptance.IsAcceptedOrUnspecified(
                candidate.Candidate.Edition?.Language, identity.Language))
            .ToArray();
        if (acceptedLanguageCandidates.Length > 0)
        {
            candidatesWithVerdicts = acceptedLanguageCandidates;
        }

        // Possible conversion sources are intentionally a fallback. Once the
        // same response supplies any Safe source, do not make the requester
        // choose a weaker conversion path or spend a limited source download
        // on it. The provider's own ordering never substitutes for this rule.
        if (mediaType == RequestMediaType.Ebook && candidatesWithVerdicts.Any(candidate =>
                Providers.ExternalEbookFormatPolicy.Classify(candidate.Candidate.Release?.Format) ==
                Providers.ExternalEbookFormatTier.Safe))
        {
            candidatesWithVerdicts = candidatesWithVerdicts.Where(candidate =>
                Providers.ExternalEbookFormatPolicy.Classify(candidate.Candidate.Release?.Format) !=
                Providers.ExternalEbookFormatTier.Possible).ToArray();
        }

        var options = candidatesWithVerdicts.Select(candidate =>
        {
            var sourceCandidate = candidate.Candidate;
            return new FulfillmentOption(
                ProviderId: provider.ProviderId,
                ProviderResultId: sourceCandidate.ProviderReference,
                WorkId: Guid.Empty,
                EditionId: null,
                MediaType: mediaType,
                OptionKind: OptionKind.DirectAcquisition,
                AcquisitionMethod: AcquisitionMethod.DirectDownload,
                // A release-name-only source states its container and its
                // language nowhere else, so the verifier's reading of the
                // release name is the fallback for both. Structured evidence
                // still wins whenever the provider supplied it.
                Format: sourceCandidate.Format ?? candidate.MatchVerdict.ReleaseNameFormat,
                Language: sourceCandidate.Edition?.Language ?? candidate.MatchVerdict.ReleaseNameLanguage,
                Quality: sourceCandidate.Release?.QualityTags
                    .FirstOrDefault(tag => string.Equals(tag, "retail", StringComparison.OrdinalIgnoreCase)) is not null
                    ? "retail"
                    : null,
                Availability: null,
                Cost: 0m,
                Currency: null,
                LicenseOrUsageStatus: null,
                DrmStatus: sourceCandidate.Release?.DrmStatus switch
                {
                    Providers.ExternalProviderDrmStatus.None => "none",
                    Providers.ExternalProviderDrmStatus.Encrypted => "encrypted",
                    _ => "unknown"
                },
                ExternalActionUri: null,
                ProviderData: sourceCandidate.ProviderReference,
                MatchBasis: candidate.MatchVerdict.Basis,
                RequiresLanguageConfirmation: candidate.MatchVerdict.RequiresLanguageConfirmation,
                Title: sourceCandidate.Title,
                Author: sourceCandidate.Author,
                CandidateRevision: sourceCandidate.CandidateRevision,
                AcquireToken: sourceCandidate.AcquireToken,
                RequiresReleaseConfirmation: candidate.ReleaseVerdict.RequiresConfirmation,
                ReleaseConcern: candidate.ReleaseVerdict.Reason,
                PublicationYear: sourceCandidate.Edition?.PublicationYear,
                Publisher: sourceCandidate.Edition?.Publisher,
                SizeBytes: sourceCandidate.Release?.SizeBytes,
                PartCount: sourceCandidate.Release?.PartCount,
                IsAbridged: sourceCandidate.Release?.IsAbridged,
                IsUnabridged: sourceCandidate.Release?.IsUnabridged,
                AdminInspectionUri: sourceCandidate.InspectionUri,
                // A "read by <name>" credit in the release name is, for a
                // release-name-only source, the only narration evidence that
                // exists. Reported as Human only when a reader is actually
                // named -- absence stays Unknown and is never inferred.
                NarrationKind: mediaType == RequestMediaType.Audiobook &&
                    candidate.MatchVerdict.ReleaseNameNarrator is not null
                        ? NarrationKind.Human
                        : null,
                Narrator: mediaType == RequestMediaType.Audiobook
                    ? candidate.MatchVerdict.ReleaseNameNarrator
                    : null,
                NarrationEvidence: mediaType == RequestMediaType.Audiobook &&
                    candidate.MatchVerdict.ReleaseNameNarrator is not null
                        ? "The release name credits a named reader."
                        : null);
        }).ToArray();

        return SelectOneStrictCandidate(options, mediaType, excludedProviderResultIds);
    }

    /// <summary>
    /// Several records can spell the same title and observed author exactly.
    /// That is a duplicate, not a meaningful choice, so exactly one keeps the
    /// confirmed <see cref="Matching.BookMatchBasis.StrictTitleAuthor"/> basis
    /// that makes it eligible for unattended acquisition and the rest fall
    /// back to reviewable.
    /// </summary>
    /// <remarks>
    /// The winner comes from <see cref="Providers.ExternalCandidateRanker"/>
    /// rather than response order, so the choice is reproducible: the provider
    /// may return the same set in a different order on the next search, and a
    /// retry loop that advances through candidates needs the ordering to be
    /// the same each time it looks.
    /// <para>
    /// <paramref name="excludedProviderResultIds"/> is how the acquisition
    /// path says "these were already tried and failed": without it, declining
    /// the winner would leave the next search nominating that same record and
    /// the request with no confirmed candidate at all.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<FulfillmentOption> SelectOneStrictCandidate(
        IReadOnlyList<FulfillmentOption> options,
        RequestMediaType mediaType,
        IReadOnlySet<string>? excludedProviderResultIds)
    {
        var strictWinner = Providers.ExternalCandidateRanker.SelectBest(
            options.Where(option =>
                option.MatchBasis == Matching.BookMatchBasis.StrictTitleAuthor &&
                excludedProviderResultIds?.Contains(option.ProviderResultId) != true),
            mediaType);
        if (strictWinner is null)
        {
            return options;
        }

        return options
            .Select(option => option.MatchBasis == Matching.BookMatchBasis.StrictTitleAuthor &&
                    !string.Equals(option.ProviderResultId, strictWinner.ProviderResultId, StringComparison.Ordinal)
                ? option with { MatchBasis = null }
                : option)
            .ToArray();
    }

    /// <summary>
    /// One live <c>GET /health</c> probe for <paramref name="provider"/>
    /// (protocol v2 §5), used by <c>ExternalProviderRecheckService</c>'s own
    /// periodic refresh. Kept here rather than duplicated at that call site
    /// since it needs the same API-key unprotect step <see cref="FindForProviderAsync"/>
    /// already does.
    /// </summary>
    public async Task<Providers.ExternalProviderHealth> CheckHealthAsync(
        Domain.Providers.ExternalProvider provider, CancellationToken cancellationToken)
    {
        var apiKey = provider.HasApiKey
            ? protector.Unprotect(
                Providers.ExternalProviderSecretPurposes.ApiKey, provider.ProtectedApiKey!, provider.ApiKeyFormatVersion)
            : null;

        return await externalProviderClient.GetHealthAsync(provider.BaseUrl, apiKey, cancellationToken);
    }

    /// <summary>
    /// True when <paramref name="provider"/>'s own last-observed health
    /// explicitly reported its search capability as unavailable (protocol v2
    /// §5's <c>operations.search</c>) — the admin-registered-provider
    /// equivalent of <see cref="IDirectAcquisitionProvider.IsReadyAsync"/>'s
    /// "not ready, don't bother" signal. Reads the cached result of the last
    /// live probe (Test Connection, or <see cref="CheckHealthAsync"/>'s own
    /// periodic refresh) rather than making a fresh call: a broken provider's
    /// own search call already fails cheaply on its own, so the point of
    /// gating here is not to save that call — it's so the reason is recorded
    /// as "known unavailable" instead of looking identical to "no candidates."
    /// A provider that has never been tested (both fields still <see langword="null"/>)
    /// is never treated as unavailable by this check.
    /// </summary>
    public static bool IsKnownSearchUnavailable(Domain.Providers.ExternalProvider provider) =>
        string.Equals(
            provider.CachedSearchOperationStatus,
            nameof(Providers.ProviderOperationalStatus.Unavailable),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>Same as <see cref="IsKnownSearchUnavailable"/>, for <c>operations.acquire</c>.</summary>
    public static bool IsKnownAcquireUnavailable(Domain.Providers.ExternalProvider provider) =>
        string.Equals(
            provider.CachedAcquireOperationStatus,
            nameof(Providers.ProviderOperationalStatus.Unavailable),
            StringComparison.OrdinalIgnoreCase);
}
