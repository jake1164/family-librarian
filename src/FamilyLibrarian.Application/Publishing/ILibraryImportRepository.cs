using FamilyLibrarian.Domain.Publishing;

namespace FamilyLibrarian.Application.Publishing;

public interface ILibraryImportRepository
{
    Task<LibraryImport?> FindAsync(Guid id, CancellationToken cancellationToken);

    Task<LibraryImport?> FindByAssetIdAsync(Guid assetId, CancellationToken cancellationToken);

    /// <summary>Every import not created long ago and/or not yet <c>Available</c> — the admin queue.</summary>
    Task<IReadOnlyList<LibraryImportView>> ListRecentAsync(CancellationToken cancellationToken);

    void Add(LibraryImport import);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
