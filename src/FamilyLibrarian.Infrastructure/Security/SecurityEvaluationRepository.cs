using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Domain.Security;
using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.Security;

public sealed class SecurityEvaluationRepository(AppDbContext database) : ISecurityEvaluationRepository
{
    // From the moment the better copy is being fetched until the old one is retracted, copies of that format are the upgrade's business.
    private static readonly LibraryUpgradeState[] UpgradeInProgress =
        [LibraryUpgradeState.Acquiring, LibraryUpgradeState.Publishing, LibraryUpgradeState.Verified];

    public async Task<ApprovalResult> ExecuteModelReleaseAsync(Guid assetId, Func<Task<ApprovalResult>> action, CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        // A parameterized row lock plus reload prevents the scoped identity map from hiding a concurrent hold change.
        await database.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM acquisition.media_assets WHERE id = {assetId} FOR UPDATE", cancellationToken);
        var tracked = database.ChangeTracker.Entries<MediaAsset>().FirstOrDefault(e => e.Entity.Id == assetId);
        if (tracked is not null) await tracked.ReloadAsync(cancellationToken);
        try
        {
            var result = await action();
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            database.ChangeTracker.Clear();
            throw;
        }
    }

    public Task<MediaAsset?> FindAssetAsync(Guid assetId, CancellationToken cancellationToken) =>
        database.MediaAssets.FirstOrDefaultAsync(asset => asset.Id == assetId, cancellationToken);

    public async Task<IReadOnlyList<MediaAsset>> ListReservedDramatizationsAsync(Guid formatId, CancellationToken cancellationToken) =>
        await database.MediaAssets.Where(a => a.AssociatedRequestFormatId == formatId && a.StorageState == MediaAssetStorageState.Unmatched &&
            a.RuledOutAtUtc != null && a.IdentityHoldCode == Domain.Evidence.EvidenceReasonCodes.IdentityHold.DramatizedAdaptation)
            .OrderBy(a => a.CreatedAtUtc).ThenBy(a => a.BundleSequence).ToArrayAsync(cancellationToken);

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
                    request.Status == RequestStatus.Available || request.Status == RequestStatus.Cancelled) &&
                !database.LibraryUpgrades.Any(upgrade => upgrade.RequestFormatId == format.Id && UpgradeInProgress.Contains(upgrade.State))
            orderby asset.UpdatedAtUtc
            select asset)
            .Take(maximumCount)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<MediaAsset>> ListHeldAssetsSupersededByVerifiedCopyAsync(
        int maximumCount, DateTimeOffset awaitingDecisionBeforeUtc, CancellationToken cancellationToken) =>
        await database.MediaAssets
            .Where(held => (held.StorageState == MediaAssetStorageState.Unmatched ||
                    // Checked and waiting for a librarian: the newest evaluation finished without failing.
                    (held.StorageState == MediaAssetStorageState.Processing && held.UpdatedAtUtc < awaitingDecisionBeforeUtc &&
                        database.SecurityEvaluations.Where(evaluation => evaluation.AssetId == held.Id)
                            .OrderByDescending(evaluation => evaluation.CreatedAtUtc).Take(1)
                            .Any(evaluation => evaluation.Status == SecurityEvaluationStatus.Passed || evaluation.Status == SecurityEvaluationStatus.ReviewRequired))) &&
                !database.LibraryUpgrades.Any(upgrade => upgrade.RequestFormatId == held.AssociatedRequestFormatId && UpgradeInProgress.Contains(upgrade.State)) &&
                database.MediaAssets.Any(better => better.AssociatedRequestFormatId == held.AssociatedRequestFormatId &&
                    (better.StorageState == MediaAssetStorageState.Trusted || better.StorageState == MediaAssetStorageState.Archived) &&
                    (held.BundleId == null || better.BundleId != held.BundleId)))
            .OrderBy(held => held.UpdatedAtUtc)
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
