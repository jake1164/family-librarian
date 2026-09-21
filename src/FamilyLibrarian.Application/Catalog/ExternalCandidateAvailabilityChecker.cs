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
    Providers.PrivateEgressRouteResolver routeResolver,
    Providers.ExternalProviderMatchVerifier matchVerifier,
    ICredentialProtector protector)
{
    /// <summary>
    /// A provider whose declared egress policy the gateway cannot currently
    /// satisfy, or whose search call fails, is silently skipped — same
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
        var resolution = routeResolver.Resolve(provider.EffectiveEgressPolicy);
        if (!resolution.IsAllowed || IsKnownSearchUnavailable(provider))
        {
            return [];
        }

        try
        {
            return await FindForProviderAsync(provider, resolution.Route!, identity, mediaType, cancellationToken);
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
    public async Task<IReadOnlyList<FulfillmentOption>> FindForProviderAsync(
        Domain.Providers.ExternalProvider provider, Providers.EgressRoute route, BookIdentity identity,
        RequestMediaType mediaType, CancellationToken cancellationToken)
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
            route,
            cancellationToken);

        if (candidates.Count == 0)
        {
            return [];
        }

        var verdicts = await matchVerifier.VerifyAsync(
            identity.Title, identity.Author, isbn13, candidates, cancellationToken, identity.Language);

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

        // A duplicate provider record is not a meaningful choice when every
        // candidate spells the same title and observed author exactly. Pick
        // one only after FL's own language/release/format filters have run;
        // format preference is FL policy, response order only breaks ties.
        var selectedStrictCandidate = candidatesWithVerdicts
            .Where(candidate => candidate.MatchVerdict.Basis == Matching.BookMatchBasis.StrictTitleAuthor)
            .OrderBy(candidate => Providers.ExternalEbookFormatPolicy.AcquisitionPreference(
                candidate.Candidate.Release?.Format))
            .FirstOrDefault();
        var selectedStrictReference = selectedStrictCandidate.Candidate?.ProviderReference;

        return candidatesWithVerdicts.Select(candidate =>
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
                Format: sourceCandidate.Format,
                Language: sourceCandidate.Edition?.Language,
                Quality: null,
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
                MatchBasis: candidate.Candidate.ProviderReference == selectedStrictReference
                    ? Matching.BookMatchBasis.StrictTitleAuthor
                    : candidate.MatchVerdict.Basis == Matching.BookMatchBasis.StrictTitleAuthor
                        ? null
                        : candidate.MatchVerdict.Basis,
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
                IsUnabridged: sourceCandidate.Release?.IsUnabridged);
        }).ToArray();
    }

    /// <summary>
    /// One live <c>GET /health</c> probe for <paramref name="provider"/>
    /// (protocol v2 §5), used by <c>ExternalProviderRecheckService</c>'s own
    /// periodic refresh. Kept here rather than duplicated at that call site
    /// since it needs the same API-key unprotect step <see cref="FindForProviderAsync"/>
    /// already does.
    /// </summary>
    public async Task<Providers.ExternalProviderHealth> CheckHealthAsync(
        Domain.Providers.ExternalProvider provider, Providers.EgressRoute route, CancellationToken cancellationToken)
    {
        var apiKey = provider.HasApiKey
            ? protector.Unprotect(
                Providers.ExternalProviderSecretPurposes.ApiKey, provider.ProtectedApiKey!, provider.ApiKeyFormatVersion)
            : null;

        return await externalProviderClient.GetHealthAsync(provider.BaseUrl, apiKey, route, cancellationToken);
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
