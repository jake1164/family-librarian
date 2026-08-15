using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Publishing;
using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.Publishing;

public sealed class AudiobookshelfSettingsStore(AppDbContext database, IClock clock) : IAudiobookshelfSettingsStore
{
    public Task<AudiobookshelfSettings?> FindAsync(CancellationToken cancellationToken) =>
        database.AudiobookshelfSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);

    public async Task<AudiobookshelfSettings> GetOrCreateAsync(CancellationToken cancellationToken)
    {
        var existing = await database.AudiobookshelfSettings.FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var created = new AudiobookshelfSettings(clock.UtcNow);
        database.AudiobookshelfSettings.Add(created);
        return created;
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) => database.SaveChangesAsync(cancellationToken);
}
