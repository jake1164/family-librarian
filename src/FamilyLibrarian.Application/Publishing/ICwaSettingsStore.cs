using FamilyLibrarian.Domain.Publishing;

namespace FamilyLibrarian.Application.Publishing;

public interface ICwaSettingsStore
{
    Task<CwaSettings?> FindAsync(CancellationToken cancellationToken);

    /// <summary>Returns the existing row, or a new unsaved one when CWA has never been configured.</summary>
    Task<CwaSettings> GetOrCreateAsync(CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
