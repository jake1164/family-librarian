using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Domain.Integrations;
using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.Integrations;

public sealed class MetadataProviderSettingsStore(AppDbContext database, IClock clock)
    : IMetadataProviderSettingsStore
{
    public async Task<IReadOnlyList<MetadataProviderSetting>> GetAllAsync(
        CancellationToken cancellationToken) =>
        await database.MetadataProviderSettings
            .AsNoTracking()
            .ToArrayAsync(cancellationToken);

    public async Task<MetadataProviderSetting?> FindAsync(
        string providerId,
        CancellationToken cancellationToken) =>
        await database.MetadataProviderSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(setting => setting.ProviderId == providerId, cancellationToken);

    public async Task<MetadataProviderSetting> GetOrCreateAsync(
        string providerId,
        CancellationToken cancellationToken)
    {
        var existing = await database.MetadataProviderSettings
            .FirstOrDefaultAsync(setting => setting.ProviderId == providerId, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var created = new MetadataProviderSetting(providerId, clock.UtcNow);
        database.MetadataProviderSettings.Add(created);
        return created;
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        database.SaveChangesAsync(cancellationToken);
}
