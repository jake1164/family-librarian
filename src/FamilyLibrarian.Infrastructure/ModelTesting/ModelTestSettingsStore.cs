using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.ModelTesting;
using FamilyLibrarian.Domain.ModelTesting;
using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.ModelTesting;

public sealed class ModelTestSettingsStore(AppDbContext database, IClock clock) : IModelTestSettingsStore
{
    public Task<ModelTestSettings?> FindAsync(CancellationToken cancellationToken) =>
        database.ModelTestSettingsRows.AsNoTracking().FirstOrDefaultAsync(cancellationToken);

    public async Task<ModelTestSettings> GetOrCreateAsync(CancellationToken cancellationToken)
    {
        var existing = await database.ModelTestSettingsRows.FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var created = new ModelTestSettings(clock.UtcNow);
        database.ModelTestSettingsRows.Add(created);
        return created;
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) => database.SaveChangesAsync(cancellationToken);
}
