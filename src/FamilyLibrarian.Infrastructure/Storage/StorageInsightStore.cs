using FamilyLibrarian.Application.Storage;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.Storage;

/// <summary>Reads what the records say is held, and PostgreSQL's own statistics for table sizes. Read-only; returns numbers, never content.</summary>
public sealed class StorageInsightStore(AppDbContext database) : IStorageInsightStore
{
    private static readonly MediaAssetStorageState[] FileStates =
    [
        MediaAssetStorageState.Quarantine, MediaAssetStorageState.Processing, MediaAssetStorageState.Trusted,
        MediaAssetStorageState.Unmatched, MediaAssetStorageState.Rejected
    ];

    public async Task<IReadOnlyDictionary<MediaAssetStorageState, IReadOnlySet<string>>> GetKnownStoredNamesAsync(CancellationToken cancellationToken)
    {
        var rows = await database.MediaAssets.AsNoTracking().Where(asset => FileStates.Contains(asset.StorageState))
            .Select(asset => new { asset.StorageState, asset.StoredFilename }).ToListAsync(cancellationToken);
        return rows.GroupBy(row => row.StorageState).ToDictionary(
            group => group.Key, group => (IReadOnlySet<string>)group.Select(row => row.StoredFilename).ToHashSet(StringComparer.Ordinal));
    }

    public async Task<IReadOnlyList<HeldFileGroup>> GetHeldFilesAsync(CancellationToken cancellationToken)
    {
        var rows = await database.MediaAssets.AsNoTracking().Where(asset => FileStates.Contains(asset.StorageState))
            .GroupBy(asset => new { asset.StorageState, asset.MediaType, RuledOut = asset.RuledOutAtUtc != null })
            .Select(group => new
            {
                group.Key.StorageState, group.Key.MediaType, group.Key.RuledOut, Files = group.Count(),
                Bytes = group.Sum(asset => asset.SizeBytes), Oldest = group.Min(asset => (DateTimeOffset?)asset.UpdatedAtUtc)
            }).ToListAsync(cancellationToken);
        return [.. rows.OrderBy(row => row.StorageState).ThenBy(row => row.MediaType).ThenBy(row => row.RuledOut)
            .Select(row => new HeldFileGroup(row.StorageState, row.MediaType, row.RuledOut, row.Files, row.Bytes, row.Oldest))];
    }

    public async Task<IReadOnlyList<StorageTableInfo>> GetTableInfoAsync(CancellationToken cancellationToken)
    {
        // The row count is the planner's estimate (n_live_tup), so it is approximate; the size is exact and includes indexes and TOAST.
        var rows = await database.Database.SqlQuery<TableRow>($"""
            SELECT schemaname AS "Schema", relname AS "Table", n_live_tup AS "Rows", pg_total_relation_size(relid) AS "Bytes"
            FROM pg_stat_user_tables
            """).ToListAsync(cancellationToken);
        return [.. rows.Select(row => new StorageTableInfo(row.Schema, row.Table, row.Rows, row.Bytes))];
    }

    private sealed record TableRow(string Schema, string Table, long Rows, long Bytes);
}
