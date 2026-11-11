using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Domain.Audit;

namespace FamilyLibrarian.Application.Storage;

/// <summary>
/// How long database rows with no lasting household value are kept. Defaults are the agreed periods; <see cref="Clamped"/> keeps any
/// configured value inside fixed limits so a typo cannot erase history early or let a table grow forever.
/// </summary>
public sealed record DatabaseRetentionOptions
{
    /// <summary>Detailed file-check evidence for files that no longer exist (D-6: at least 90 days after the workflow completed).</summary>
    public int EvidenceDays { get; init; } = 90;

    /// <summary>Advisory AI findings.</summary>
    public int SemanticFindingDays { get; init; } = 180;

    /// <summary>The per-source attempt history of requests that are finished (in the library or cancelled).</summary>
    public int AttemptDays { get; init; } = 180;

    /// <summary>Which search wordings were tried for finished requests.</summary>
    public int SearchMemoryDays { get; init; } = 30;

    /// <summary>In-app notifications, read or not.</summary>
    public int NotificationDays { get; init; } = 90;

    /// <summary>Closed source-interaction alerts and their per-person delivery rows.</summary>
    public int AlertDays { get; init; } = 90;

    /// <summary>Chat/e-mail messages that have already been sent.</summary>
    public int CommunicationDays { get; init; } = 90;

    /// <summary>The audit trail. Two years by default and never less than a year.</summary>
    public int AuditDays { get; init; } = 730;

    /// <summary>Rows removed per database statement, so one pass never holds a long lock.</summary>
    public int BatchSize { get; init; } = 1000;

    /// <summary>Most statements per table per pass; a bigger backlog finishes over the following days.</summary>
    public int MaxBatchesPerTable { get; init; } = 20;

    public DatabaseRetentionOptions Clamped() => this with
    {
        EvidenceDays = MaintenanceLimits.Clamp(EvidenceDays, MaintenanceLimits.EvidenceDays),
        SemanticFindingDays = MaintenanceLimits.Clamp(SemanticFindingDays, MaintenanceLimits.SemanticFindingDays),
        AttemptDays = MaintenanceLimits.Clamp(AttemptDays, MaintenanceLimits.AttemptDays),
        SearchMemoryDays = MaintenanceLimits.Clamp(SearchMemoryDays, MaintenanceLimits.SearchMemoryDays),
        NotificationDays = MaintenanceLimits.Clamp(NotificationDays, MaintenanceLimits.NotificationDays),
        AlertDays = MaintenanceLimits.Clamp(AlertDays, MaintenanceLimits.AlertDays),
        CommunicationDays = MaintenanceLimits.Clamp(CommunicationDays, MaintenanceLimits.CommunicationDays),
        AuditDays = MaintenanceLimits.Clamp(AuditDays, MaintenanceLimits.AuditDays),
        BatchSize = Math.Clamp(BatchSize, 100, 5000),
        MaxBatchesPerTable = Math.Clamp(MaxBatchesPerTable, 1, 100)
    };

    public int DaysFor(RetentionTarget target) => target switch
    {
        RetentionTarget.ArtifactEvidence => EvidenceDays,
        RetentionTarget.SemanticFindings => SemanticFindingDays,
        RetentionTarget.ProviderAttempts => AttemptDays,
        RetentionTarget.SearchVariantMemory => SearchMemoryDays,
        RetentionTarget.Notifications => NotificationDays,
        RetentionTarget.InteractionAlerts => AlertDays,
        RetentionTarget.Communications => CommunicationDays,
        RetentionTarget.AuditEvents => AuditDays,
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown retention target.")
    };
}

public enum RetentionTarget
{
    ArtifactEvidence,
    SemanticFindings,
    ProviderAttempts,
    SearchVariantMemory,
    Notifications,
    InteractionAlerts,
    Communications,
    AuditEvents
}

/// <summary>Removes at most <c>batchSize</c> expired rows of one kind and returns how many went. Each rule keeps what is still in use.</summary>
public interface IDatabaseRetentionStore
{
    Task<int> PurgeAsync(RetentionTarget target, DateTimeOffset cutoffUtc, int batchSize, CancellationToken cancellationToken);
}

public sealed record DatabaseRetentionReport(IReadOnlyDictionary<RetentionTarget, int> Removed, int Failures)
{
    public int TotalRemoved => Removed.Values.Sum();
}

/// <summary>
/// Trims old rows from tables that only ever grow, so an installation that runs unattended for years keeps a small database.
/// Every table is isolated (one failure never stops the others), every pass is bounded, and the audit entry carries counts only.
/// </summary>
public sealed class DatabaseRetentionService(IDatabaseRetentionStore store, IAuditWriter audit, IClock clock, DatabaseRetentionOptions options)
{
    public Task<DatabaseRetentionReport> RunAsync(CancellationToken cancellationToken) => RunAsync(options, cancellationToken);

    /// <summary>Runs with the given periods (the administrator's saved settings), not the ones this service was built with.</summary>
    public async Task<DatabaseRetentionReport> RunAsync(DatabaseRetentionOptions effective, CancellationToken cancellationToken)
    {
        var o = effective.Clamped();
        var now = clock.UtcNow;
        var removed = new Dictionary<RetentionTarget, int>();
        var failures = 0;
        foreach (var target in Enum.GetValues<RetentionTarget>())
        {
            var cutoff = now - TimeSpan.FromDays(o.DaysFor(target));
            var total = 0;
            try
            {
                for (var batch = 0; batch < o.MaxBatchesPerTable; batch++)
                {
                    var count = await store.PurgeAsync(target, cutoff, o.BatchSize, cancellationToken);
                    total += count;
                    if (count < o.BatchSize)
                    {
                        break;
                    }
                }
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // One table failing (a lock, a constraint) must not stop the others; the next pass retries.
                failures++;
            }

            removed[target] = total;
        }

        var report = new DatabaseRetentionReport(removed, failures);
        if (report.TotalRemoved > 0)
        {
            try
            {
                await audit.WriteSystemAsync(AuditActions.DatabaseRetentionRun, AuditSubjectTypes.Storage, null,
                    removed.Where(pair => pair.Value > 0).ToDictionary(pair => pair.Key.ToString(), pair => pair.Value), cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                failures++;
                report = report with { Failures = failures };
            }
        }

        return report;
    }
}
