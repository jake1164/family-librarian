using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Application.Catalog;

/// <summary>
/// One permitted way to get a Work in a given format, from one provider.
/// </summary>
/// <remarks>
/// An option is not an authorization to spend, borrow, or download — it is
/// display/decision plumbing only. M8 defines this shape and the capability
/// interfaces below with zero concrete implementations: no real
/// availability/store/direct-acquisition provider exists until M11, so
/// <see cref="IWorkFulfillmentOptionsService"/> currently returns only the
/// baseline <see cref="OptionKind.Owned"/>/not-owned state, not real offers.
/// </remarks>
public sealed record FulfillmentOption(
    string ProviderId,
    string ProviderResultId,
    Guid WorkId,
    Guid? EditionId,
    RequestMediaType MediaType,
    OptionKind OptionKind,
    AcquisitionMethod AcquisitionMethod,
    string? Format,
    string? Language,
    string? Quality,
    string? Availability,
    decimal? Cost,
    string? Currency,
    string? LicenseOrUsageStatus,
    string? DrmStatus,
    Uri? ExternalActionUri,
    string? ProviderData);

public enum OptionKind
{
    Owned,
    Availability,
    StoreOffer,
    DirectAcquisition,
    ExternalAction
}

public enum AcquisitionMethod
{
    Borrow,
    Purchase,
    DirectDownload,
    ManualImport,
    OwnedImport,
    ProviderManaged
}

/// <summary>Advertises store-offer discovery. No concrete implementation ships in M8.</summary>
public interface IStoreOfferProvider
{
    string Id { get; }

    Task<IReadOnlyList<FulfillmentOption>> FindOffersAsync(
        Guid workId,
        RequestMediaType mediaType,
        CancellationToken cancellationToken);
}

/// <summary>Advertises library/subscription availability. No concrete implementation ships in M8.</summary>
public interface IAvailabilityProvider
{
    string Id { get; }

    Task<IReadOnlyList<FulfillmentOption>> FindAvailabilityAsync(
        Guid workId,
        RequestMediaType mediaType,
        CancellationToken cancellationToken);
}

/// <summary>Advertises free/direct legal acquisition, and can fetch the file for an option it returned.</summary>
public interface IDirectAcquisitionProvider
{
    string Id { get; }

    Task<IReadOnlyList<FulfillmentOption>> FindDirectAcquisitionsAsync(
        Guid workId,
        RequestMediaType mediaType,
        CancellationToken cancellationToken);

    /// <summary>
    /// Fetches the file(s) for a previously returned option — more than one
    /// for a multi-track acquisition (e.g. a chaptered audiobook), which the
    /// caller stages as one bundle rather than independent artifacts.
    /// <paramref name="fulfillmentOption"/> should be freshly re-derived by
    /// the caller (e.g. via <see cref="FindDirectAcquisitionsAsync"/>), never
    /// reconstructed from client-supplied data — <see cref="FulfillmentOption.ProviderData"/>
    /// carries whatever this provider needs (e.g. a resolved download URL),
    /// opaque to every caller but this one.
    /// </summary>
    Task<IReadOnlyList<DirectAcquisitionFile>> FetchAsync(FulfillmentOption fulfillmentOption, CancellationToken cancellationToken);
}

/// <summary>
/// A direct-acquisition provider whose returned options are conservative enough
/// for the server to fetch without a librarian choosing among them first.
/// </summary>
/// <remarks>
/// This is deliberately an opt-in capability rather than an inference from
/// <see cref="FulfillmentOption.OptionKind"/> or price. A provider must make
/// its own title/creator/identifier confidence decision before it implements
/// this contract. The normal security and identity checks still run after the
/// file is fetched.
/// </remarks>
public interface IAutomaticDirectAcquisitionProvider : IDirectAcquisitionProvider;

public sealed record DirectAcquisitionFile(Stream Content, string Filename);

/// <summary>Advertises matches in a linked owned library (e.g. Calibre-Web). No concrete implementation ships in M8.</summary>
public interface IOwnedLibraryProvider
{
    string Id { get; }

    Task<IReadOnlyList<FulfillmentOption>> FindOwnedMatchesAsync(
        Guid workId,
        RequestMediaType mediaType,
        CancellationToken cancellationToken);
}

public interface IWorkFulfillmentOptionsService
{
    Task<IReadOnlyList<FulfillmentOption>> GetOptionsAsync(
        Guid workId,
        RequestMediaType mediaType,
        CancellationToken cancellationToken);
}

/// <summary>
/// Aggregates whatever capability providers are registered. Today that is none,
/// so this returns an empty list — the plumbing exists so a real provider in
/// M11 is additive, not a redesign.
/// </summary>
public sealed class WorkFulfillmentOptionsService(
    IEnumerable<IAvailabilityProvider> availabilityProviders,
    IEnumerable<IStoreOfferProvider> storeOfferProviders,
    IEnumerable<IDirectAcquisitionProvider> directAcquisitionProviders,
    IEnumerable<IOwnedLibraryProvider> ownedLibraryProviders,
    Providers.IExternalProviderStore externalProviders,
    Providers.IExternalProviderClient externalProviderClient,
    Providers.PrivateEgressRouteResolver routeResolver,
    ICredentialProtector protector,
    IWorkLookup workLookup) : IWorkFulfillmentOptionsService
{
    public async Task<IReadOnlyList<FulfillmentOption>> GetOptionsAsync(
        Guid workId,
        RequestMediaType mediaType,
        CancellationToken cancellationToken)
    {
        var options = new List<FulfillmentOption>();

        foreach (var provider in availabilityProviders)
        {
            try
            {
                options.AddRange(await provider.FindAvailabilityAsync(workId, mediaType, cancellationToken));
            }
            catch (HttpRequestException)
            {
                // Availability is optional page enrichment. One unavailable
                // provider must not make the Work or request page unavailable.
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Treat a provider's own timeout as no options. Caller-requested
                // cancellation still propagates through the filter above.
            }
        }

        foreach (var provider in storeOfferProviders)
        {
            try
            {
                options.AddRange(await provider.FindOffersAsync(workId, mediaType, cancellationToken));
            }
            catch (HttpRequestException)
            {
                // Store offers are optional page enrichment.
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // A provider timeout degrades to no offers from that provider.
            }
        }

        foreach (var provider in directAcquisitionProviders)
        {
            try
            {
                options.AddRange(await provider.FindDirectAcquisitionsAsync(workId, mediaType, cancellationToken));
            }
            catch (HttpRequestException)
            {
                // The automatic worker records provider failures separately.
                // This read model only needs to omit unavailable options.
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // A provider timeout must not fail the containing page.
            }
        }

        foreach (var provider in ownedLibraryProviders)
        {
            try
            {
                options.AddRange(await provider.FindOwnedMatchesAsync(workId, mediaType, cancellationToken));
            }
            catch (HttpRequestException)
            {
                // Owned-library status is optional page enrichment.
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // A provider timeout degrades to an unknown owned status.
            }
        }

        options.AddRange(await FindExternalProviderOptionsAsync(workId, mediaType, cancellationToken));

        return options;
    }

    /// <summary>
    /// Same unified <see cref="FulfillmentOption"/> shape as every other
    /// direct-acquisition source (Gutendex included) — an external provider's
    /// results need no special handling anywhere downstream (UI, recommendation
    /// policy, acquire endpoint). A provider whose declared egress policy the
    /// gateway cannot currently satisfy is silently skipped, same "degrade to no
    /// results" posture every other search failure in this method already has.
    /// </summary>
    private async Task<IReadOnlyList<FulfillmentOption>> FindExternalProviderOptionsAsync(
        Guid workId, RequestMediaType mediaType, CancellationToken cancellationToken)
    {
        var enabled = await externalProviders.ListEnabledAsync(cancellationToken);
        if (enabled.Count == 0)
        {
            return [];
        }

        var work = await workLookup.FindAsync(workId, cancellationToken);
        if (work is null)
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
                        Guid.NewGuid(), mediaType, work.Title,
                        work.PrimaryAuthor is null ? [] : [work.PrimaryAuthor], Isbn13: null),
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
                WorkId: workId,
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
