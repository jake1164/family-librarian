using FamilyLibrarian.Domain.Publishing;

namespace FamilyLibrarian.Application.Publishing;

public interface IAudiobookshelfSettingsStore
{
    Task<AudiobookshelfSettings?> FindAsync(CancellationToken cancellationToken);

    /// <summary>Returns the existing row, or a new unsaved one when Audiobookshelf has never been configured.</summary>
    Task<AudiobookshelfSettings> GetOrCreateAsync(CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
