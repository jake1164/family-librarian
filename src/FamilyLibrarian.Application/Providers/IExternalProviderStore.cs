using FamilyLibrarian.Domain.Providers;

namespace FamilyLibrarian.Application.Providers;

public interface IExternalProviderStore
{
    Task<IReadOnlyList<ExternalProvider>> ListAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ExternalProvider>> ListEnabledAsync(CancellationToken cancellationToken);

    Task<ExternalProvider?> FindAsync(Guid id, CancellationToken cancellationToken);

    Task<ExternalProvider?> FindByProviderIdAsync(string providerId, CancellationToken cancellationToken);

    void Add(ExternalProvider provider);

    void Remove(ExternalProvider provider);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
