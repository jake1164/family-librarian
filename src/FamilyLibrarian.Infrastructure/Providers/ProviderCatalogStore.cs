using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Domain.Providers;
using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.Providers;

public sealed class ProviderCatalogStore(AppDbContext database) : IProviderCatalogStore
{
    public async Task<IReadOnlyList<ProviderCatalog>> ListAsync(CancellationToken cancellationToken) =>
        await database.ProviderCatalogs.OrderBy(catalog => catalog.DisplayName).ToArrayAsync(cancellationToken);

    public Task<ProviderCatalog?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        database.ProviderCatalogs.FirstOrDefaultAsync(catalog => catalog.Id == id, cancellationToken);

    public void Add(ProviderCatalog catalog) => database.ProviderCatalogs.Add(catalog);

    public void Remove(ProviderCatalog catalog) => database.ProviderCatalogs.Remove(catalog);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => database.SaveChangesAsync(cancellationToken);
}
