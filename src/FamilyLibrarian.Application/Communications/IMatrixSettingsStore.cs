using FamilyLibrarian.Domain.Communications;

namespace FamilyLibrarian.Application.Communications;

public interface IMatrixSettingsStore
{
    Task<MatrixSettings?> FindAsync(CancellationToken cancellationToken);

    Task<MatrixSettings> GetOrCreateAsync(CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
