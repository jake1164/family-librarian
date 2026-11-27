using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Setup;
using FamilyLibrarian.Domain.Setup;
using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FamilyLibrarian.Infrastructure.Setup;

public sealed class SetupGuidanceStore(AppDbContext database, IClock clock) : ISetupGuidanceStore
{
    public Task<SetupGuidanceSettings?> FindAsync(CancellationToken cancellationToken) =>
        database.SetupGuidanceSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);

    public async Task<SetupGuidanceSettings> GetOrCreateAsync(CancellationToken cancellationToken)
    {
        var existing = await database.SetupGuidanceSettings.FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var created = new SetupGuidanceSettings(clock.UtcNow);
        database.SetupGuidanceSettings.Add(created);
        return created;
    }

    public async Task<bool> TrySaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await database.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            // The row changed after it was read.
            DiscardTrackedGuidance();
            return false;
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Someone else created the row first.
            DiscardTrackedGuidance();
            return false;
        }
    }

    private void DiscardTrackedGuidance()
    {
        foreach (var entry in database.ChangeTracker.Entries<SetupGuidanceSettings>().ToList())
        {
            entry.State = EntityState.Detached;
        }
    }
}
