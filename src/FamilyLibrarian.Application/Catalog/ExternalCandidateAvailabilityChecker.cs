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

            var apiKey = provider.HasApiKey
                ? protector.Unprotect(
                    Providers.ExternalProviderSecretPurposes.ApiKey, provider.ProtectedApiKey!, provider.ApiKeyFormatVersion)
                : null;

            IReadOnlyList<Providers.ExternalProviderCandidate> candidates;
            try
            {
                candidates = await externalProviderClient.SearchAsync(
                    provider.BaseUrl,
                    apiKey,
                    new Providers.ExternalProviderSearchRequest(
                        Guid.NewGuid(), mediaType, identity.Title,
                        identity.Author is null ? [] : [identity.Author], Isbn13: null),
                    resolution.Route!,
                    cancellationToken);
            }
            catch (HttpRequestException)
            {
                continue;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                continue;
            }

            found.AddRange(candidates.Select(candidate => new FulfillmentOption(
                ProviderId: provider.ProviderId,
                ProviderResultId: candidate.ProviderReference,
                WorkId: Guid.Empty,
                EditionId: null,
                MediaType: mediaType,
                OptionKind: OptionKind.DirectAcquisition,
                AcquisitionMethod: AcquisitionMethod.DirectDownload,
                Format: candidate.Format,
                Language: null,
                Quality: null,
                Availability: null,
                Cost: 0m,
                Currency: null,
                LicenseOrUsageStatus: null,
                DrmStatus: null,
                ExternalActionUri: null,
                ProviderData: candidate.ProviderReference)));
        }

        return found;
    }
}
