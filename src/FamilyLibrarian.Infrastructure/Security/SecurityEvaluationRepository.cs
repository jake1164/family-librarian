using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Acquisition;
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
