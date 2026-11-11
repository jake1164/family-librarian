using FamilyLibrarian.Application.ModelGateway;
using FamilyLibrarian.Application.ModelTesting;
using FamilyLibrarian.Domain.DecisionCapture;
using FamilyLibrarian.Domain.ModelGateway;
using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyLibrarian.Infrastructure.ModelGateway;

/// <summary>
/// The usage ledger. Each operation uses its own short-lived context, so recording a model call can never flush (or be
/// blocked by) unrelated unsaved changes in the caller's unit of work.
/// </summary>
public sealed class ModelUsageLedger(IServiceScopeFactory scopes, TimeProvider time) : IModelUsageLedger
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(90);
    private static long lastPruneTicks;

    public async Task<ModelUsageTotals> GetTotalsAsync(ModelBudgetClass budget, Guid? userId, DateTimeOffset since, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var records = database.ModelUsageRecords.AsNoTracking().Where(record => record.OccurredAtUtc >= since);
        var installation = records.Where(record => record.BudgetClass == budget);
        var user = userId is { } id ? installation.Where(record => record.UserId == id) : installation.Where(record => false);
        var cloud = records.Where(record => record.IsCloud);
        var fileReview = installation.Where(record => record.Role == ModelRole.ArtifactValidationAssessment || record.Role == ModelRole.ArtifactValidationDecision);
        return new ModelUsageTotals(
            await user.CountAsync(cancellationToken), await user.SumAsync(record => (long)record.ChargedTokens, cancellationToken),
            await installation.CountAsync(cancellationToken), await installation.SumAsync(record => (long)record.ChargedTokens, cancellationToken),
            await cloud.CountAsync(cancellationToken), await cloud.SumAsync(record => (long)record.ChargedTokens, cancellationToken),
            await fileReview.CountAsync(cancellationToken), await fileReview.SumAsync(record => (long)record.ChargedTokens, cancellationToken));
    }

    public async Task AddAsync(ModelUsageRecord record, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        database.ModelUsageRecords.Add(record);
        await database.SaveChangesAsync(cancellationToken);
        await PruneOccasionallyAsync(database, cancellationToken);
    }

    public async Task CompleteAsync(
        Guid recordId, ModelCallOutcome outcome, long latencyMilliseconds, ModelUsage? usage, CancellationToken cancellationToken,
        ModelFailureReason? failureReason = null)
    {
        await using var scope = scopes.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var record = await database.ModelUsageRecords.FirstOrDefaultAsync(row => row.Id == recordId, cancellationToken);
        if (record is null)
        {
            return;
        }

        record.Complete(outcome, latencyMilliseconds, usage?.PromptTokens, usage?.CompletionTokens, usage?.TotalTokens, failureReason);
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task<ModelUsageSummary> SummarizeAsync(DateTimeOffset since, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await database.ModelUsageRecords.AsNoTracking()
            .Where(record => record.OccurredAtUtc >= since)
            .GroupBy(record => new { record.Role, record.BudgetClass, record.Outcome, record.IsCloud, record.Model, record.FailureReason, record.WorkflowEffects })
            .Select(group => new
            {
                group.Key.Role,
                group.Key.BudgetClass,
                group.Key.Outcome,
                group.Key.IsCloud,
                group.Key.Model,
                group.Key.FailureReason,
                group.Key.WorkflowEffects,
                Calls = group.Count(),
                Tokens = group.Sum(record => (long)(record.ReportedTotalTokens ?? 0)),
                Unreported = group.Count(record => record.ReportedTotalTokens == null)
            })
            .ToListAsync(cancellationToken);
        var recommendations = await database.SemanticFindings.AsNoTracking()
            .Where(finding => finding.CreatedAtUtc >= since && finding.Outcome == ModelCallOutcome.Succeeded)
            .GroupBy(finding => new { finding.Role, finding.Model, finding.IsCloud, finding.Verdict })
            .Select(group => new { group.Key.Role, group.Key.Model, group.Key.IsCloud, group.Key.Verdict, Findings = group.Count() })
            .ToListAsync(cancellationToken);
        var latencyRows = await database.ModelUsageRecords.AsNoTracking()
            .Where(record => record.OccurredAtUtc >= since && record.Outcome != ModelCallOutcome.Pending && record.LatencyMilliseconds > 0)
            .Select(record => new { record.Role, record.Model, record.IsCloud, Latency = record.LatencyMilliseconds!.Value, record.ReportedTotalTokens })
            .ToListAsync(cancellationToken);
        var latency = latencyRows.GroupBy(row => new { row.Role, row.Model, row.IsCloud })
            .Select(group =>
            {
                var sorted = group.Select(row => row.Latency).Order().ToArray();
                return new ModelLatencySummaryRow(group.Key.Role, group.Key.Model, group.Key.IsCloud, sorted.Length,
                    Percentile(sorted, 50), Percentile(sorted, 95), group.Sum(row => (long)(row.ReportedTotalTokens ?? 0)));
            })
            .OrderBy(row => row.Role).ThenBy(row => row.Model, StringComparer.Ordinal).ToArray();
        return new ModelUsageSummary(
            since,
            [.. rows.OrderBy(row => row.Role).ThenBy(row => row.Outcome)
                .Select(row => new ModelUsageSummaryRow(row.Role, row.BudgetClass, row.Outcome, row.IsCloud, row.Calls, row.Tokens, row.Unreported,
                    row.Model, row.FailureReason, row.WorkflowEffects))],
            [.. recommendations.Select(row => new ModelRecommendationSummaryRow(row.Role, row.Model, row.IsCloud, row.Verdict ?? "Other", row.Findings))],
            latency);
    }

    /// <summary>Nearest-rank percentile of an ascending sequence. Never interpolates, so it reports a latency that really happened.</summary>
    internal static long Percentile(IReadOnlyList<long> sortedAscending, int percentile)
    {
        if (sortedAscending.Count == 0) return 0;
        var rank = (int)Math.Ceiling(percentile / 100.0 * sortedAscending.Count);
        return sortedAscending[Math.Clamp(rank, 1, sortedAscending.Count) - 1];
    }

    public async Task<IReadOnlyList<ModelUsageRollupRow>> ListRollupsAsync(int weeks, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(weeks, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(weeks, 260);
        await using var scope = scopes.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var since = WeekStart(time.GetUtcNow()).AddDays(-7 * weeks);
        var rows = await database.ModelUsageRollups.AsNoTracking().Where(rollup => rollup.WeekStartUtc >= since)
            .OrderBy(rollup => rollup.WeekStartUtc).ThenBy(rollup => rollup.Role).ThenBy(rollup => rollup.Model).ToListAsync(cancellationToken);
        return [.. rows.Select(rollup => new ModelUsageRollupRow(
            rollup.WeekStartUtc, rollup.Role, rollup.Model, rollup.IsCloud, rollup.OpportunityFlags, rollup.Calls, rollup.AcceptedReplies,
            rollup.RejectedReplies, rollup.ProviderErrors, rollup.CancelledCalls, rollup.ActionCalls, rollup.NoChangeCalls, rollup.AbstainedCalls,
            rollup.AdvisoryCalls, rollup.ReportedTokens, rollup.CallsWithoutUsage, rollup.LatencyMillisecondsSum, rollup.LatencyCalls,
            rollup.LatencyP95Milliseconds, rollup.DownstreamPositive, rollup.DownstreamNegative))];
    }

    /// <summary>Monday 00:00 UTC of the week containing <paramref name="moment"/>.</summary>
    internal static DateTimeOffset WeekStart(DateTimeOffset moment)
    {
        var date = moment.UtcDateTime.Date;
        return new DateTimeOffset(date.AddDays(-(((int)date.DayOfWeek + 6) % 7)), TimeSpan.Zero);
    }

    /// <summary>
    /// Folds finished calls older than the cutoff into weekly counts before they are deleted, so the trend outlives the 90-day ledger.
    /// Additive: a week split across two prunes simply adds up. Runs in one transaction with the delete, so no call is counted twice
    /// or lost. Counts only; nothing about a person or a book.
    /// </summary>
    private static async Task RollUpAsync(AppDbContext database, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        var old = await database.ModelUsageRecords.AsNoTracking()
            .Where(record => record.OccurredAtUtc < cutoff && record.Outcome != ModelCallOutcome.Pending)
            .Select(record => new
            {
                record.Id, record.Role, record.Model, record.IsCloud, record.Outcome, record.FailureReason, record.WorkflowEffects,
                record.OccurredAtUtc, record.ReportedTotalTokens, record.LatencyMilliseconds
            }).ToListAsync(cancellationToken);
        if (old.Count == 0)
        {
            await PruneRollupsAsync(database, cutoff, cancellationToken);
            return;
        }

        // What happened afterwards to the decisions these calls were part of: positive only, negative only, or neither (mixed or none).
        var ids = old.Select(record => (Guid?)record.Id).ToArray();
        var downstream = (await (from call in database.DecisionCalls.AsNoTracking()
                                 join outcome in database.DecisionOutcomes.AsNoTracking() on call.SnapshotId equals outcome.SnapshotId
                                 where ids.Contains(call.ModelUsageRecordId)
                                 select new { Usage = call.ModelUsageRecordId!.Value, outcome.Kind }).ToListAsync(cancellationToken))
            .GroupBy(row => row.Usage)
            .ToDictionary(group => group.Key, group =>
            {
                var positive = group.Any(row => DecisionOutcomeKinds.IsPositive(row.Kind));
                var negative = group.Any(row => !DecisionOutcomeKinds.IsPositive(row.Kind));
                return positive == negative ? (bool?)null : positive;
            });

        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        foreach (var group in old.GroupBy(record => new
        {
            Week = WeekStart(record.OccurredAtUtc), record.Role, record.Model, record.IsCloud,
            Flags = (record.WorkflowEffects ?? 0) & ModelWorkflowEffects.OpportunityMask
        }))
        {
            var key = group.Key;
            var rollup = await database.ModelUsageRollups.FirstOrDefaultAsync(row =>
                row.WeekStartUtc == key.Week && row.Role == key.Role && row.Model == key.Model && row.IsCloud == key.IsCloud &&
                row.OpportunityFlags == key.Flags, cancellationToken);
            if (rollup is null)
            {
                rollup = new ModelUsageRollup(key.Week, key.Role, key.Model, key.IsCloud, key.Flags);
                database.ModelUsageRollups.Add(rollup);
            }

            foreach (var record in group)
            {
                rollup.Add(ModelCallClassification.Classify(record.Outcome, record.FailureReason), record.WorkflowEffects, record.ReportedTotalTokens,
                    record.LatencyMilliseconds > 0 ? record.LatencyMilliseconds : null, downstream.GetValueOrDefault(record.Id));
            }

            rollup.MergeLatencyP95(Percentile([.. group.Where(record => record.LatencyMilliseconds > 0).Select(record => record.LatencyMilliseconds!.Value).Order()], 95));
        }

        await database.SaveChangesAsync(cancellationToken);
        await database.ModelUsageRecords.Where(record => record.OccurredAtUtc < cutoff).ExecuteDeleteAsync(cancellationToken);
        // The weekly counts are kept for three years, then they go too: the table stays small and nothing in it is kept for ever.
        var rollupCutoff = WeekStart(cutoff).AddDays(-7 * RollupRetentionWeeks);
        await database.ModelUsageRollups.Where(rollup => rollup.WeekStartUtc < rollupCutoff).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    internal const int RollupRetentionWeeks = 156;

    private static async Task PruneRollupsAsync(AppDbContext database, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        var rollupCutoff = WeekStart(cutoff).AddDays(-7 * RollupRetentionWeeks);
        await database.ModelUsageRollups.Where(rollup => rollup.WeekStartUtc < rollupCutoff).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task ObserveWorkflowAsync(Guid recordId, ModelWorkflowEffect effects, CancellationToken cancellationToken)
    {
        if (effects == ModelWorkflowEffect.None || (effects & ~(ModelWorkflowEffect.All | ModelWorkflowEffect.AllOpportunities)) != 0)
            throw new ArgumentOutOfRangeException(nameof(effects));
        await using var scope = scopes.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await database.ModelUsageRecords.Where(record => record.Id == recordId && record.Outcome == ModelCallOutcome.Succeeded)
            .ExecuteUpdateAsync(setters => setters.SetProperty(record => record.WorkflowEffects,
                record => (record.WorkflowEffects ?? 0) | (int)effects), cancellationToken);
    }

    public async Task<ModelCallFailures> ListFailuresAsync(
        DateTimeOffset since, DateTimeOffset until, ModelRole? role, int page, CancellationToken cancellationToken,
        ModelCallDiagnosticFilter? filter = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(page, 2000);
        const int pageSize = 50;
        await using var scope = scopes.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var query = database.ModelUsageRecords.AsNoTracking().Where(record => record.OccurredAtUtc >= since && record.OccurredAtUtc <= until &&
            record.Outcome != ModelCallOutcome.Succeeded && record.Outcome != ModelCallOutcome.Pending);
        if (role is { } selected) query = query.Where(record => record.Role == selected);
        if (filter is not null)
        {
            var outcomes = filter.Outcomes.ToArray();
            query = filter.ExcludeOutcomes ? query.Where(record => !outcomes.Contains(record.Outcome))
                : query.Where(record => outcomes.Contains(record.Outcome));
            query = filter.Cancelled ? query.Where(record => record.FailureReason == ModelFailureReason.Cancelled)
                : query.Where(record => record.FailureReason != ModelFailureReason.Cancelled);
        }
        var rows = await query.OrderByDescending(record => record.OccurredAtUtc).ThenByDescending(record => record.Id)
            .Skip((page - 1) * pageSize).Take(pageSize + 1)
            .Select(record => new ModelCallFailure(record.Id, record.Role, record.Model, record.IsCloud, record.Outcome,
                record.FailureReason, record.OccurredAtUtc, record.LatencyMilliseconds, record.MediaAssetId, null)).ToListAsync(cancellationToken);
        var pageRows = rows.Take(pageSize).ToArray();
        var assetIds = pageRows.Where(row => row.MediaAssetId is not null).Select(row => row.MediaAssetId!.Value).Distinct().ToArray();
        var artifacts = assetIds.Length == 0 ? [] : await database.MediaAssets.AsNoTracking().Where(asset => assetIds.Contains(asset.Id))
            .Select(asset => new ModelCallArtifact(asset.Id, asset.WorkId, asset.Format, asset.BundleId, asset.BundleSequence, asset.BundleTrackCount))
            .ToListAsync(cancellationToken);
        var byId = artifacts.ToDictionary(artifact => artifact.AssetId);
        return new ModelCallFailures([.. pageRows.Select(row => row with
            { Artifact = row.MediaAssetId is { } id ? byId.GetValueOrDefault(id) : null })], rows.Count > pageSize && page < 2000);
    }

    private async Task PruneOccasionallyAsync(AppDbContext database, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var last = Interlocked.Read(ref lastPruneTicks);
        if (now.UtcTicks - last < TimeSpan.FromDays(1).Ticks || Interlocked.CompareExchange(ref lastPruneTicks, now.UtcTicks, last) != last)
        {
            return;
        }

        await RunPruneAsync(database, now, cancellationToken);
    }

    /// <summary>
    /// Folds finished calls older than the retention window into weekly counts and removes them, unconditionally. The throttled path above
    /// calls this once a day; it is public so a test (or an operator tool) can run it without waiting for the interval.
    /// </summary>
    public async Task RunPruneNowAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        await RunPruneAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), time.GetUtcNow(), cancellationToken);
    }

    private static Task RunPruneAsync(AppDbContext database, DateTimeOffset now, CancellationToken cancellationToken) =>
        RollUpAsync(database, now - Retention, cancellationToken);
}
