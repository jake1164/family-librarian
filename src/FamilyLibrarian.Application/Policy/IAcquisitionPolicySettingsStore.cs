using FamilyLibrarian.Domain.Policy;

namespace FamilyLibrarian.Application.Policy;

public interface IAcquisitionPolicySettingsStore
{
    Task<AcquisitionPolicySettings?> FindAsync(CancellationToken cancellationToken);

    /// <summary>Returns the existing row, or a new unsaved one when a default has never been set.</summary>
    Task<AcquisitionPolicySettings> GetOrCreateAsync(CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
