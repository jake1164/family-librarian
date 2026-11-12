using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Application.Storage;

/// <summary>Files the application's records say it holds, by state and media type. Counts, bytes and ages only: never a title or name.</summary>
/// <param name="AwaitingDecision">Checked files waiting in Processing for a librarian; they follow the waiting periods, not the stuck one.</param>
public sealed record HeldFileGroup(
    MediaAssetStorageState State, RequestMediaType MediaType, bool RuledOut, int Files, long Bytes, DateTimeOffset? OldestUpdatedAtUtc,
    bool AwaitingDecision = false);

/// <summary>One database table's approximate row count and total size (including indexes), from the database's own statistics.</summary>
public sealed record StorageTableInfo(string Schema, string Table, long ApproximateRows, long ApproximateBytes);

/// <summary>What is physically in one zone folder, and how much of it no record accounts for.</summary>
public sealed record StorageZoneInfo(
    MediaAssetStorageState Zone, int Files, long Bytes, DateTimeOffset? OldestFileUtc, int UnrecognizedFiles, long UnrecognizedBytes);

public sealed record StorageInsight(
    DateTimeOffset AsOfUtc,
    StorageVolumeInfo Volume,
    IReadOnlyList<StorageZoneInfo> Zones,
    IReadOnlyList<HeldFileGroup> HeldFiles,
    IReadOnlyList<StorageTableInfo> Tables,
    long DatabaseApproximateBytes);

/// <summary>Database side of the storage insight. Never returns a filename, title or any content.</summary>
public interface IStorageInsightStore
{
    /// <summary>Stored names of the assets whose bytes should be on disk (every state that keeps a file), per zone.</summary>
    Task<IReadOnlyDictionary<MediaAssetStorageState, IReadOnlySet<string>>> GetKnownStoredNamesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<HeldFileGroup>> GetHeldFilesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<StorageTableInfo>> GetTableInfoAsync(CancellationToken cancellationToken);
}

public sealed record StorageInsightResult(bool Succeeded, bool Forbidden, StorageInsight? Insight)
{
    public static StorageInsightResult Success(StorageInsight insight) => new(true, false, insight);

    public static StorageInsightResult NotAllowed() => new(false, true, null);
}

/// <summary>
/// What the installation is holding and what is using the disk, for an administrator. It reads only; it deletes nothing. A file in a
/// zone folder with no matching asset record and older than the grace period is reported as unrecognized (a crash leftover candidate);
/// a young file is not, because it may be in the middle of being written.
/// </summary>
public sealed class StorageInsightService(
    IStorageInsightStore store, IAssetStagingStore staging, ICurrentUser currentUser, ICurrentAdministrator administrator, IClock clock)
{
    /// <summary>A file this young is never called unrecognized: an upload or move may be in progress.</summary>
    public static readonly TimeSpan UnrecognizedGrace = TimeSpan.FromHours(24);

    private static readonly MediaAssetStorageState[] FileZones =
    [
        MediaAssetStorageState.Quarantine, MediaAssetStorageState.Processing, MediaAssetStorageState.Trusted,
        MediaAssetStorageState.Unmatched, MediaAssetStorageState.Rejected
    ];

    public async Task<StorageInsightResult> GetAsync(CancellationToken cancellationToken)
    {
        if (currentUser.UserId is null || !administrator.IsAdministrator)
        {
            return StorageInsightResult.NotAllowed();
        }

        var now = clock.UtcNow;
        var known = await store.GetKnownStoredNamesAsync(cancellationToken);
        var zones = new List<StorageZoneInfo>();
        foreach (var zone in FileZones)
        {
            var files = staging.ListStoredFiles(zone);
            var names = known.GetValueOrDefault(zone) ?? new HashSet<string>(StringComparer.Ordinal);
            var unrecognized = files.Where(file => !names.Contains(file.StoredFilename) && now - file.LastWriteUtc >= UnrecognizedGrace).ToArray();
            zones.Add(new StorageZoneInfo(
                zone, files.Count, files.Sum(file => file.SizeBytes), files.Count == 0 ? null : files.Min(file => file.LastWriteUtc),
                unrecognized.Length, unrecognized.Sum(file => file.SizeBytes)));
        }

        var tables = await store.GetTableInfoAsync(cancellationToken);
        return StorageInsightResult.Success(new StorageInsight(
            now, staging.GetVolumeInfo(), zones, await store.GetHeldFilesAsync(cancellationToken),
            [.. tables.OrderByDescending(table => table.ApproximateBytes).ThenBy(table => table.Table, StringComparer.Ordinal)],
            tables.Sum(table => table.ApproximateBytes)));
    }
}
