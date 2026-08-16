using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Domain.Providers;
using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.Providers;

public sealed class ExternalProviderStore(AppDbContext database) : IExternalProviderStore
{
    public async Task<IReadOnlyList<ExternalProvider>> ListAsync(CancellationToken cancellationToken) =>
        await database.ExternalProviders.OrderBy(provider => provider.DisplayName).ToArrayAsync(cancellationToken);

    public async Task<IReadOnlyList<ExternalProvider>> ListEnabledAsync(CancellationToken cancellationToken) =>
        await database.ExternalProviders.Where(provider => provider.IsEnabled).ToArrayAsync(cancellationToken);

    public Task<ExternalProvider?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        database.ExternalProviders.FirstOrDefaultAsync(provider => provider.Id == id, cancellationToken);

    public Task<ExternalProvider?> FindByProviderIdAsync(string providerId, CancellationToken cancellationToken)
    {
        var normalized = providerId.ToLowerInvariant();
        return database.ExternalProviders.FirstOrDefaultAsync(
            provider => provider.ProviderId == normalized, cancellationToken);
    }

    public void Add(ExternalProvider provider) => database.ExternalProviders.Add(provider);

    public void Remove(ExternalProvider provider) => database.ExternalProviders.Remove(provider);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => database.SaveChangesAsync(cancellationToken);
}
