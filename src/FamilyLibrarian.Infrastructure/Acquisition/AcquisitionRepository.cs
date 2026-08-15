using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.Acquisition;

public sealed class AcquisitionRepository(AppDbContext database) : IAcquisitionRepository
{
    public Task<bool> ExistsAssetWithChecksumForFormatAsync(
        Guid requestFormatId,
        string sha256,
        CancellationToken cancellationToken) =>
        database.MediaAssets.AnyAsync(
            asset => asset.AssociatedRequestFormatId == requestFormatId && asset.Sha256 == sha256,
            cancellationToken);

    public async Task<IReadOnlyList<MediaAssetAdminView>> ListActiveAsync(CancellationToken cancellationToken)
    {
        var activeStates = new[] { MediaAssetStorageState.Quarantine, MediaAssetStorageState.Processing };

        var query =
            from asset in database.MediaAssets
            where activeStates.Contains(asset.StorageState)
            join format in database.RequestFormats on asset.AssociatedRequestFormatId equals format.Id
            join work in database.Works on asset.WorkId equals work.Id
            orderby asset.CreatedAtUtc
            select new MediaAssetAdminView(
                asset.Id,
                format.RequestId,
                asset.WorkId,
                work.CanonicalTitle,
                asset.MediaType,
                asset.Format,
                asset.OriginalFilename,
                asset.SizeBytes,
                asset.StorageState,
                asset.CreatedAtUtc,
                asset.UpdatedAtUtc);

        return await query.ToArrayAsync(cancellationToken);
    }

    public void AddJob(AcquisitionJob job) => database.AcquisitionJobs.Add(job);

    public void AddAsset(MediaAsset asset) => database.MediaAssets.Add(asset);

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        database.SaveChangesAsync(cancellationToken);
}
