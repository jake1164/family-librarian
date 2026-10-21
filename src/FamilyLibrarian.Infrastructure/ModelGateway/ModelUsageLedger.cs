using FamilyLibrarian.Application.ModelGateway;
using FamilyLibrarian.Application.ModelTesting;
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
        return new ModelUsageSummary(
            since,
            [.. rows.OrderBy(row => row.Role).ThenBy(row => row.Outcome)
                .Select(row => new ModelUsageSummaryRow(row.Role, row.BudgetClass, row.Outcome, row.IsCloud, row.Calls, row.Tokens, row.Unreported,
                    row.Model, row.FailureReason, row.WorkflowEffects))],
            [.. recommendations.Select(row => new ModelRecommendationSummaryRow(row.Role, row.Model, row.IsCloud, row.Verdict ?? "Other", row.Findings))]);
    }

    public async Task ObserveWorkflowAsync(Guid recordId, ModelWorkflowEffect effects, CancellationToken cancellationToken)
    {
        if (effects == ModelWorkflowEffect.None || (effects & ~ModelWorkflowEffect.All) != 0)
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

        var cutoff = now - Retention;
        await database.ModelUsageRecords.Where(record => record.OccurredAtUtc < cutoff).ExecuteDeleteAsync(cancellationToken);
    }
}
