using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Domain.Providers;
using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.Providers;

public sealed class ProviderSettingsStore(AppDbContext database, IClock clock)
    : IProviderSettingsStore
{
    public async Task<IReadOnlyList<ProviderSetting>> GetAllAsync(
        CancellationToken cancellationToken) =>
        await database.ProviderSettings
            .AsNoTracking()
            .ToArrayAsync(cancellationToken);

    public async Task<ProviderSetting?> FindAsync(
        string providerId,
        CancellationToken cancellationToken) =>
        await database.ProviderSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(setting => setting.ProviderId == providerId, cancellationToken);

    public async Task<ProviderSetting> GetOrCreateAsync(
        string providerId,
        CancellationToken cancellationToken)
    {
        var existing = await database.ProviderSettings
            .FirstOrDefaultAsync(setting => setting.ProviderId == providerId, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var created = new ProviderSetting(providerId, clock.UtcNow);
        database.ProviderSettings.Add(created);
        return created;
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        database.SaveChangesAsync(cancellationToken);
}
