using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Publishing;
using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.Publishing;

public sealed class CwaSettingsStore(AppDbContext database, IClock clock) : ICwaSettingsStore
{
    public Task<CwaSettings?> FindAsync(CancellationToken cancellationToken) =>
        database.CwaSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);

    public async Task<CwaSettings> GetOrCreateAsync(CancellationToken cancellationToken)
    {
        var existing = await database.CwaSettings.FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var created = new CwaSettings(clock.UtcNow);
        database.CwaSettings.Add(created);
        return created;
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) => database.SaveChangesAsync(cancellationToken);
}
