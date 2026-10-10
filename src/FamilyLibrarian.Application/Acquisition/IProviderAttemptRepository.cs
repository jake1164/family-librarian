using FamilyLibrarian.Domain.Acquisition;

namespace FamilyLibrarian.Application.Acquisition;

/// <summary>Persistence boundary for the append-only provider lookup ledger.</summary>
public interface IProviderAttemptRepository
{
    Task<IReadOnlyList<ProviderAttempt>> ListForRequestAsync(Guid requestId, CancellationToken cancellationToken);

    /// <summary>Recent entries from the append-only source lookup ledger, for
    /// the administrator's cross-request operational view.</summary>
    Task<IReadOnlyList<ProviderAttempt>> ListRecentAsync(int maximumCount, CancellationToken cancellationToken);

    Task<ProviderAttempt?> FindLatestForFormatAsync(
        Guid requestFormatId,
        string providerId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns the most recent lookup for each provider. This keeps a source
    /// health indicator current without discarding the request-level ledger.
    /// </summary>
    Task<IReadOnlyList<ProviderAttempt>> ListLatestByProviderAsync(
        CancellationToken cancellationToken);

    /// <summary>
    /// The newest lookups across providers with nothing collapsed, so source
    /// health can tell a repeated failure from a one-off. Defaults to the
    /// latest lookup per provider for stores that cannot do better.
    /// </summary>
    Task<IReadOnlyList<ProviderAttempt>> ListRecentForHealthAsync(
        int maximumCount, CancellationToken cancellationToken) =>
        ListLatestByProviderAsync(cancellationToken);

    void Add(ProviderAttempt attempt);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
