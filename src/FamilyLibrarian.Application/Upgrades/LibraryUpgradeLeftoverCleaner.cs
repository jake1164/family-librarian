using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Audit;

namespace FamilyLibrarian.Application.Upgrades;

/// <summary>
/// Removes the better copy a stopped upgrade left in the trusted zone. The library already holds a copy of that book, nothing will ever
/// deliver the leftover, and nobody should have to find and delete it by hand: the system cleans up after itself. Every removal is
/// audited and noted on the request. Runs on the upgrade finalize timer; one file failing never stops the others.
/// </summary>
public sealed class LibraryUpgradeLeftoverCleaner(
    ILibraryUpgradeStore store,
    ISecurityEvaluationRepository assets,
    IAssetStagingStore staging,
    IAuditWriter audit,
    IClock clock)
{
    /// <summary>
    /// A copy is only called a leftover after sitting this long, so one still being delivered when the upgrade stopped is never taken.
    /// </summary>
    public static readonly TimeSpan Grace = TimeSpan.FromDays(1);

    private const int BatchSize = 20;

    public async Task<int> RemoveAsync(CancellationToken cancellationToken)
    {
        var removed = 0;
        var noted = new HashSet<Guid>();
        foreach (var leftover in await store.ListLeftoverCopiesAsync(clock.UtcNow - Grace, BatchSize, cancellationToken))
        {
            try
            {
                var asset = await assets.FindAssetAsync(leftover.AssetId, cancellationToken);
                if (asset is not { StorageState: MediaAssetStorageState.Trusted }) continue;

                // The file goes first: nothing sweeps the trusted zone, so a record changed ahead of a failed delete would leave a file
                // nothing ever looks at. If the delete fails the record is untouched and the next pass tries again.
                await staging.DeleteAsync(MediaAssetStorageState.Trusted, asset.StoredFilename, cancellationToken);
                asset.TransitionStorageState(MediaAssetStorageState.Destroyed, clock.UtcNow);
                await assets.SaveChangesAsync(cancellationToken);
                await audit.WriteSystemAsync(AuditActions.AssetDestroyed, AuditSubjectTypes.MediaAsset, asset.Id.ToString(),
                    new { AssetId = asset.Id, PreviousState = nameof(MediaAssetStorageState.Trusted), Reason = "upgrade.Leftover" }, cancellationToken);
                removed++;
                if (noted.Add(leftover.RequestId))
                {
                    await store.NoteAsync(leftover.RequestId,
                        "An unused extra copy left by a stopped upgrade was removed automatically; the copy in the library was not changed.",
                        clock.UtcNow, cancellationToken);
                }
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Retried on the next pass.
            }
        }

        return removed;
    }
}
