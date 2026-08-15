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

/// <summary>Advertises free/direct legal acquisition. No concrete implementation ships in M8.</summary>
public interface IDirectAcquisitionProvider
{
    string Id { get; }

    Task<IReadOnlyList<FulfillmentOption>> FindDirectAcquisitionsAsync(
        Guid workId,
        RequestMediaType mediaType,
        CancellationToken cancellationToken);
}

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
    IEnumerable<IOwnedLibraryProvider> ownedLibraryProviders) : IWorkFulfillmentOptionsService
{
    public async Task<IReadOnlyList<FulfillmentOption>> GetOptionsAsync(
        Guid workId,
        RequestMediaType mediaType,
        CancellationToken cancellationToken)
    {
        var options = new List<FulfillmentOption>();

        foreach (var provider in availabilityProviders)
        {
            options.AddRange(await provider.FindAvailabilityAsync(workId, mediaType, cancellationToken));
        }

        foreach (var provider in storeOfferProviders)
        {
            options.AddRange(await provider.FindOffersAsync(workId, mediaType, cancellationToken));
        }

        foreach (var provider in directAcquisitionProviders)
        {
            options.AddRange(await provider.FindDirectAcquisitionsAsync(workId, mediaType, cancellationToken));
        }

        foreach (var provider in ownedLibraryProviders)
        {
            options.AddRange(await provider.FindOwnedMatchesAsync(workId, mediaType, cancellationToken));
        }

        return options;
    }
}
