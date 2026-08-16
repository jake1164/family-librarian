using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Accounts;
using FamilyLibrarian.Domain.Accounts;
using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.Identity;

public sealed class OidcSettingsStore(AppDbContext database, IClock clock) : IOidcSettingsStore
{
    public Task<OidcSettings?> FindAsync(CancellationToken cancellationToken) =>
        database.OidcSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);

    public async Task<OidcSettings> GetOrCreateAsync(CancellationToken cancellationToken)
    {
        var existing = await database.OidcSettings.FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var created = new OidcSettings(clock.UtcNow);
        database.OidcSettings.Add(created);
        return created;
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) => database.SaveChangesAsync(cancellationToken);
}
