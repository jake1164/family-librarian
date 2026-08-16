using FamilyLibrarian.Domain.Providers;

namespace FamilyLibrarian.Application.Providers;

public interface IProviderCatalogStore
{
    Task<IReadOnlyList<ProviderCatalog>> ListAsync(CancellationToken cancellationToken);

    Task<ProviderCatalog?> FindAsync(Guid id, CancellationToken cancellationToken);

    void Add(ProviderCatalog catalog);

    void Remove(ProviderCatalog catalog);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
