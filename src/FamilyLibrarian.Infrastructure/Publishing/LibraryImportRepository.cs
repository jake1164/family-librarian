using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Publishing;
using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.Publishing;

public sealed class LibraryImportRepository(AppDbContext database) : ILibraryImportRepository
{
    public Task<LibraryImport?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        database.LibraryImports.FirstOrDefaultAsync(import => import.Id == id, cancellationToken);

    public Task<LibraryImport?> FindByAssetIdAsync(Guid assetId, CancellationToken cancellationToken) =>
        database.LibraryImports.FirstOrDefaultAsync(import => import.AssetId == assetId, cancellationToken);

    public async Task<IReadOnlyList<LibraryImportView>> ListRecentAsync(CancellationToken cancellationToken)
    {
        var query =
            from import in database.LibraryImports
            join asset in database.MediaAssets on import.AssetId equals asset.Id
            join format in database.RequestFormats on asset.AssociatedRequestFormatId equals format.Id
            join work in database.Works on asset.WorkId equals work.Id
            orderby import.CreatedAtUtc descending
            select new LibraryImportView(
                import.Id,
                import.AssetId,
                format.RequestId,
                asset.WorkId,
                work.CanonicalTitle,
                asset.OriginalFilename,
                import.Status,
                import.ExternalBookId,
                import.FailureReason,
                import.CreatedAtUtc,
                import.CompletedAtUtc);

        return await query.ToArrayAsync(cancellationToken);
    }

    public void Add(LibraryImport import) => database.LibraryImports.Add(import);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => database.SaveChangesAsync(cancellationToken);
}
