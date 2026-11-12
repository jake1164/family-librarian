using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Notifications;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Audit;

namespace FamilyLibrarian.Application.Storage;

/// <summary>Database side of clean-up. Returns ids, sizes and states, never file contents.</summary>
public interface IStorageMaintenanceStore
{
    /// <summary>Every held file that clean-up could consider: received, being checked, held for identification, or rejected.</summary>
    Task<IReadOnlyList<RemovableCopy>> ListRemovableCopiesAsync(CancellationToken cancellationToken);

    /// <summary>Loads the assets for changing. Returns only those still in <paramref name="expectedState"/>.</summary>
    Task<IReadOnlyList<MediaAsset>> LoadForRemovalAsync(IReadOnlyCollection<Guid> assetIds, MediaAssetStorageState expectedState, CancellationToken cancellationToken);

    Task SaveAsync(CancellationToken cancellationToken);

    /// <summary>Adds a line to the request's review history when it is under review; does nothing otherwise.</summary>
    Task NoteHeldCopyRemovedAsync(Guid requestId, string note, DateTimeOffset atUtc, CancellationToken cancellationToken);
}

public sealed record StorageMaintenanceReport(
    int RemovedRejected, int RemovedRuledOut, int RemovedWaiting, int RemovedOverCap, int RemovedStuck, int RemovedOrphans,
    int RemindersSent, bool UnderDiskPressure, int Failures)
{
    public int TotalRemoved => RemovedRejected + RemovedRuledOut + RemovedWaiting + RemovedOverCap + RemovedStuck + RemovedOrphans;
}

/// <summary>
/// Unattended clean-up of files nobody needs, so an installation that runs for months cannot fill its disk. It only ever removes
/// files that are rejected, stuck before trust, ruled out, or waiting for a person past their limit; a trusted file is never touched,
/// and a file any record still points at is never treated as an orphan. Every step is isolated: one failure never stops the rest.
/// </summary>
public sealed class StorageMaintenanceService(
    IStorageMaintenanceStore store,
    IStorageInsightStore insight,
    IAssetStagingStore staging,
    IAuditWriter audit,
    NotificationService notifications,
    IClock clock,
    StorageRetentionOptions options)
{
    public Task<StorageMaintenanceReport> RunAsync(CancellationToken cancellationToken) => RunAsync(options, cancellationToken);

    /// <summary>Runs with the given periods (the administrator's saved settings), not the ones this service was built with.</summary>
    public async Task<StorageMaintenanceReport> RunAsync(StorageRetentionOptions effective, CancellationToken cancellationToken)
    {
        var o = effective.Clamped();
        var now = clock.UtcNow;
        var volume = staging.GetVolumeInfo();
        var pressure = StorageDiskFloor.IsUnderPressure(volume);
        int rejected = 0, ruledOut = 0, waiting = 0, overCap = 0, stuck = 0, orphans = 0, reminders = 0, failures = 0;

        if (pressure)
        {
            failures += await Safely(async () => await notifications.RecordStorageLowAsync(
                $"{StorageText(volume.FreeBytes)} free; the safety floor is {StorageText(StorageDiskFloor.RequiredFreeBytes(volume))}. " +
                "New downloads wait and safe clean-up runs sooner. Copies waiting for a person are not removed early.", cancellationToken), cancellationToken);
        }

        RetentionPlan? plan = null;
        failures += await Safely(async () =>
        {
            plan = StorageRetentionPlanner.Plan(await store.ListRemovableCopiesAsync(cancellationToken), o, now, pressure);
        }, cancellationToken);

        foreach (var removal in plan?.Removals ?? [])
        {
            var removed = 0;
            failures += await Safely(async () => removed = await RemoveAsync(removal, now, cancellationToken), cancellationToken);
            switch (removal.Reason)
            {
                case RemovalReason.Rejected: rejected += removed; break;
                case RemovalReason.RuledOut: ruledOut += removed; break;
                case RemovalReason.WaitingExpired: waiting += removed; break;
                case RemovalReason.HeldCap: overCap += removed; break;
                case RemovalReason.Stuck: stuck += removed; break;
            }
        }

        foreach (var reminder in plan?.Reminders ?? [])
        {
            failures += await Safely(async () =>
            {
                await notifications.RecordHeldCopyExpiringAsync(reminder.RequestId, reminder.WorkTitle, reminder.RemovalDateUtc, cancellationToken);
                reminders++;
            }, cancellationToken);
        }

        failures += await Safely(async () => orphans = await SweepOrphansAsync(o, now, cancellationToken), cancellationToken);

        var report = new StorageMaintenanceReport(rejected, ruledOut, waiting, overCap, stuck, orphans, reminders, pressure, failures);
        if (report.TotalRemoved > 0)
        {
            // Counts only: no title, name or path ever reaches the audit trail from clean-up.
            await Safely(async () => await audit.WriteSystemAsync(AuditActions.StorageMaintenanceRun, AuditSubjectTypes.Storage, null,
                new { rejected, ruledOut, waiting, overCap, stuck, orphans, underDiskPressure = pressure }, cancellationToken), cancellationToken);
        }

        return report;
    }

    private async Task<int> RemoveAsync(PlannedRemoval removal, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Re-read the state just before acting: a person may have released or deleted a file since the plan was made.
        var assets = await store.LoadForRemovalAsync(removal.AssetIds, removal.State, cancellationToken);
        if (assets.Count == 0)
        {
            return 0;
        }

        // The record is changed first. If that fails nothing was deleted; if the later file delete fails the file is merely a
        // leftover that the orphan sweep removes after its grace period. The reverse order could delete bytes a record still needs.
        foreach (var asset in assets)
        {
            asset.TransitionStorageState(MediaAssetStorageState.Destroyed, now);
        }

        await store.SaveAsync(cancellationToken);
        foreach (var asset in assets)
        {
            await staging.DeleteAsync(removal.State, asset.StoredFilename, cancellationToken);
            await audit.WriteSystemAsync(AuditActions.AssetDestroyed, AuditSubjectTypes.MediaAsset, asset.Id.ToString(),
                new { AssetId = asset.Id, PreviousState = removal.State.ToString(), Reason = "storage." + removal.Reason }, cancellationToken);
        }

        if (removal.WasWaitingForPerson && removal.RequestId is { } requestId)
        {
            await Safely(async () => await store.NoteHeldCopyRemovedAsync(requestId,
                "A copy that was waiting for a person was removed automatically after its time limit.", now, cancellationToken), cancellationToken);
        }

        return assets.Count;
    }

    private async Task<int> SweepOrphansAsync(StorageRetentionOptions o, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var known = await insight.GetKnownStoredNamesAsync(cancellationToken);
        // A name any record still expects on disk, in any zone, is never an orphan: a move interrupted between two zone folders
        // leaves the real file in the "wrong" one, and deleting it would lose the only copy.
        var protectedNames = known.Values.SelectMany(names => names).ToHashSet(StringComparer.Ordinal);
        var grace = TimeSpan.FromHours(o.OrphanGraceHours);
        var removed = 0;
        // The trusted zone is deliberately not swept: a file there may be a library copy even if its record is damaged.
        foreach (var zone in new[] { MediaAssetStorageState.Quarantine, MediaAssetStorageState.Processing, MediaAssetStorageState.Unmatched, MediaAssetStorageState.Rejected })
        {
            foreach (var file in staging.ListStoredFiles(zone))
            {
                if (removed >= o.BatchSize)
                {
                    return removed;
                }

                if (protectedNames.Contains(file.StoredFilename) || now - file.LastWriteUtc < grace)
                {
                    continue;
                }

                await staging.DeleteAsync(zone, file.StoredFilename, cancellationToken);
                removed++;
            }
        }

        return removed;
    }

    private static async Task<int> Safely(Func<Task> step, CancellationToken cancellationToken)
    {
        try
        {
            await step();
            return 0;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // One failed step must not stop the others; the next pass retries it.
            return 1;
        }
    }

    private static string StorageText(long? bytes) => bytes is { } b ? $"{b / (double)StorageRetentionOptions.Gigabyte:0.#} GB" : "unknown";
}
