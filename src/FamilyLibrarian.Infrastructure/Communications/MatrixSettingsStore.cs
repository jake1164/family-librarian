using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Communications;
using FamilyLibrarian.Domain.Communications;
using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.Communications;

public sealed class MatrixSettingsStore(AppDbContext database, IClock clock) : IMatrixSettingsStore
{
    public Task<MatrixSettings?> FindAsync(CancellationToken cancellationToken) =>
        database.MatrixSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);

    public async Task<MatrixSettings> GetOrCreateAsync(CancellationToken cancellationToken)
    {
        var existing = await database.MatrixSettings.FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var created = new MatrixSettings(clock.UtcNow);
        database.MatrixSettings.Add(created);
        return created;
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) => database.SaveChangesAsync(cancellationToken);
}
