using FamilyLibrarian.Domain.Publishing;

namespace FamilyLibrarian.Application.Publishing;

public interface ILibraryImportRepository
{
    Task<LibraryImport?> FindAsync(Guid id, CancellationToken cancellationToken);

    Task<LibraryImport?> FindByAssetIdAsync(Guid assetId, CancellationToken cancellationToken);

    /// <summary>Every import not created long ago and/or not yet <c>Available</c> — the admin queue.</summary>
    Task<IReadOnlyList<LibraryImportView>> ListRecentAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Imports whose bytes were handed to CWA but whose catalog entry has not
    /// yet been observed. These are safe to verify again without re-sending
    /// the source file.
    /// </summary>
    Task<IReadOnlyList<Guid>> ListAwaitingVerificationIdsAsync(CancellationToken cancellationToken);

    void Add(LibraryImport import);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
