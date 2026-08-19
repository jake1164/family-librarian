using FamilyLibrarian.Domain.Acquisition;

namespace FamilyLibrarian.Application.Acquisition;

/// <summary>Persistence boundary for the append-only provider lookup ledger.</summary>
public interface IProviderAttemptRepository
{
    Task<IReadOnlyList<ProviderAttempt>> ListForRequestAsync(Guid requestId, CancellationToken cancellationToken);

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

    void Add(ProviderAttempt attempt);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
