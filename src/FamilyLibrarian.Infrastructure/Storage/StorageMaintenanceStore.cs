using FamilyLibrarian.Application.Storage;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Domain.Security;
using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.Storage;

public sealed class StorageMaintenanceStore(AppDbContext database) : IStorageMaintenanceStore
{
    private static readonly MediaAssetStorageState[] Removable =
        [MediaAssetStorageState.Quarantine, MediaAssetStorageState.Processing, MediaAssetStorageState.Unmatched, MediaAssetStorageState.Rejected];

    public async Task<IReadOnlyList<RemovableCopy>> ListRemovableCopiesAsync(CancellationToken cancellationToken)
    {
        var rows = await (
            from asset in database.MediaAssets.AsNoTracking()
            where Removable.Contains(asset.StorageState)
            join format in database.RequestFormats.AsNoTracking() on asset.AssociatedRequestFormatId equals format.Id into formats
            from format in formats.DefaultIfEmpty()
            join work in database.Works.AsNoTracking() on asset.WorkId equals work.Id into works
            from work in works.DefaultIfEmpty()
            select new
            {
                asset.Id, asset.BundleId, asset.StorageState, asset.MediaType, RuledOut = asset.RuledOutAtUtc != null, asset.SizeBytes,
                asset.UpdatedAtUtc, RequestId = format == null ? (Guid?)null : format.RequestId, Title = work == null ? null : work.CanonicalTitle,
                // The newest evaluation tells a check that never finished from one that finished and waits for a librarian.
                LatestEvaluation = database.SecurityEvaluations.Where(evaluation => evaluation.AssetId == asset.Id)
                    .OrderByDescending(evaluation => evaluation.CreatedAtUtc).Select(evaluation => (SecurityEvaluationStatus?)evaluation.Status).FirstOrDefault()
            }).ToListAsync(cancellationToken);

        var bundles = rows.Where(row => row.BundleId != null).Select(row => row.BundleId!.Value).Distinct().ToArray();
        var trustedBundles = bundles.Length == 0
            ? []
            : (await database.MediaAssets.AsNoTracking()
                .Where(asset => asset.BundleId != null && bundles.Contains(asset.BundleId.Value) &&
                    (asset.StorageState == MediaAssetStorageState.Trusted || asset.StorageState == MediaAssetStorageState.Archived))
                .Select(asset => asset.BundleId!.Value).Distinct().ToListAsync(cancellationToken)).ToHashSet();

        return [.. rows.Select(row => new RemovableCopy(row.Id, row.BundleId, row.StorageState, row.MediaType, row.RuledOut, row.SizeBytes,
            row.UpdatedAtUtc, row.RequestId, row.Title, row.BundleId is { } bundle && trustedBundles.Contains(bundle),
            StorageRetentionPlanner.IsAwaitingDecision(row.StorageState, row.LatestEvaluation)))];
    }

    public async Task<IReadOnlyList<MediaAsset>> LoadForRemovalAsync(
        IReadOnlyCollection<Guid> assetIds, MediaAssetStorageState expectedState, CancellationToken cancellationToken) =>
        await database.MediaAssets.Where(asset => assetIds.Contains(asset.Id) && asset.StorageState == expectedState).ToListAsync(cancellationToken);

    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // A failed save (for example a file released a moment ago) must not poison the next step's tracked state.
            database.ChangeTracker.Clear();
            throw;
        }
    }

    public async Task NoteHeldCopyRemovedAsync(Guid requestId, string note, DateTimeOffset atUtc, CancellationToken cancellationToken)
    {
        var request = await database.BookRequests.Include(item => item.StatusHistory).FirstOrDefaultAsync(item => item.Id == requestId, cancellationToken);
        if (request is not { Status: RequestStatus.NeedsReview })
        {
            return;
        }

        request.AddReviewNote(note, atUtc);
        await database.SaveChangesAsync(cancellationToken);
    }
}
