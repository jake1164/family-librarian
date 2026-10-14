using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Domain.Security;
using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.Security;

public sealed class SecurityEvaluationRepository(AppDbContext database) : ISecurityEvaluationRepository
{
    public Task<MediaAsset?> FindAssetAsync(Guid assetId, CancellationToken cancellationToken) =>
        database.MediaAssets.FirstOrDefaultAsync(asset => asset.Id == assetId, cancellationToken);

    public async Task<IReadOnlyList<MediaAsset>> FindAssetsByBundleIdAsync(
        Guid bundleId, CancellationToken cancellationToken) =>
        await database.MediaAssets
            .Where(asset => asset.BundleId == bundleId)
            .OrderBy(asset => asset.BundleSequence)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<MediaAsset>> ListHeldAssetsOfSettledFormatsAsync(
        int maximumCount, CancellationToken cancellationToken) =>
        await (
            from asset in database.MediaAssets
            join format in database.RequestFormats on asset.AssociatedRequestFormatId equals format.Id
            join request in database.BookRequests on format.RequestId equals request.Id
            where (asset.StorageState == MediaAssetStorageState.Unmatched || asset.StorageState == MediaAssetStorageState.Rejected) &&
                (format.Status == RequestFormatStatus.Available || format.Status == RequestFormatStatus.Cancelled ||
                    request.Status == RequestStatus.Available || request.Status == RequestStatus.Cancelled)
            orderby asset.UpdatedAtUtc
            select asset)
            .Take(maximumCount)
            .ToListAsync(cancellationToken);

    public Task<SecurityEvaluation?> FindLatestEvaluationAsync(Guid assetId, CancellationToken cancellationToken) =>
        database.SecurityEvaluations
            .Include(evaluation => evaluation.ScanResults)
            .Include(evaluation => evaluation.ValidationResults)
            .Include(evaluation => evaluation.Approvals)
            .Where(evaluation => evaluation.AssetId == assetId)
            .OrderByDescending(evaluation => evaluation.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

    public void AddEvaluation(SecurityEvaluation evaluation) => database.SecurityEvaluations.Add(evaluation);

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        database.SaveChangesAsync(cancellationToken);
}
