using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Domain.Requests;

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
        var enabled = await externalProviders.ListEnabledAsync(cancellationToken);
        if (enabled.Count == 0)
        {
            return [];
        }

        var found = new List<FulfillmentOption>();
        foreach (var provider in enabled)
        {
            var resolution = routeResolver.Resolve(provider.EffectiveEgressPolicy);
            if (!resolution.IsAllowed)
            {
                continue;
            }

            // Skip a provider its own last health probe (Test Connection, or
            // ExternalProviderRecheckService's periodic recheck) reported as
            // unable to search at all -- same "degrade to no results" posture
            // as every other failure this loop already tolerates, but without
            // spending a network round trip on a provider already known to be
            // down for this operation.
            if (IsKnownSearchUnavailable(provider))
            {
                continue;
            }

            try
            {
                found.AddRange(await FindForProviderAsync(provider, resolution.Route!, identity, mediaType, cancellationToken));
            }
            catch (HttpRequestException)
            {
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }

        return found;
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
            new Providers.ExternalProviderSearchRequest(Guid.NewGuid(), mediaType, work, edition),
            route,
            cancellationToken);

        if (candidates.Count == 0)
        {
            return [];
        }

        var verdicts = await matchVerifier.VerifyAsync(identity.Title, identity.Author, isbn13, candidates, cancellationToken);

        return candidates.Select(candidate =>
        {
            var verdict = verdicts.GetValueOrDefault(candidate.ProviderReference, Providers.ExternalProviderMatchVerdict.Unconfirmed);
            var releaseVerdict = Providers.ExternalReleasePolicy.Evaluate(candidate.Release, mediaType);
            return new FulfillmentOption(
                ProviderId: provider.ProviderId,
                ProviderResultId: candidate.ProviderReference,
                WorkId: Guid.Empty,
                EditionId: null,
                MediaType: mediaType,
                OptionKind: OptionKind.DirectAcquisition,
                AcquisitionMethod: AcquisitionMethod.DirectDownload,
                Format: candidate.Format,
                Language: candidate.Edition?.Language,
                Quality: null,
                Availability: null,
                Cost: 0m,
                Currency: null,
                LicenseOrUsageStatus: null,
                DrmStatus: null,
                ExternalActionUri: null,
                ProviderData: candidate.ProviderReference,
                MatchBasis: verdict.Basis,
                RequiresLanguageConfirmation: verdict.RequiresLanguageConfirmation,
                Title: candidate.Title,
                Author: candidate.Author,
                CandidateRevision: candidate.CandidateRevision,
                AcquireToken: candidate.AcquireToken,
                RequiresReleaseConfirmation: releaseVerdict.RequiresConfirmation,
                ReleaseConcern: releaseVerdict.Reason,
                PublicationYear: candidate.Edition?.PublicationYear,
                Publisher: candidate.Edition?.Publisher,
                SizeBytes: candidate.Release?.SizeBytes,
                PartCount: candidate.Release?.PartCount,
                IsAbridged: candidate.Release?.IsAbridged,
                IsUnabridged: candidate.Release?.IsUnabridged);
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
