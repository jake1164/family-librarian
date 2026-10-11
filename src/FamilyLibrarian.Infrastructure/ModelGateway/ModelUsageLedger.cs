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
        return new ModelUsageTotals(
            await user.CountAsync(cancellationToken), await user.SumAsync(record => (long)record.ChargedTokens, cancellationToken),
            await installation.CountAsync(cancellationToken), await installation.SumAsync(record => (long)record.ChargedTokens, cancellationToken),
            await cloud.CountAsync(cancellationToken), await cloud.SumAsync(record => (long)record.ChargedTokens, cancellationToken));
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
        Guid recordId, ModelCallOutcome outcome, long latencyMilliseconds, ModelUsage? usage, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var record = await database.ModelUsageRecords.FirstOrDefaultAsync(row => row.Id == recordId, cancellationToken);
        if (record is null)
        {
            return;
        }

        record.Complete(outcome, latencyMilliseconds, usage?.PromptTokens, usage?.CompletionTokens, usage?.TotalTokens);
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task<ModelUsageSummary> SummarizeAsync(DateTimeOffset since, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await database.ModelUsageRecords.AsNoTracking()
            .Where(record => record.OccurredAtUtc >= since)
            .GroupBy(record => new { record.Role, record.BudgetClass, record.Outcome, record.IsCloud })
            .Select(group => new
            {
                group.Key.Role,
                group.Key.BudgetClass,
                group.Key.Outcome,
                group.Key.IsCloud,
                Calls = group.Count(),
                Tokens = group.Sum(record => (long)(record.ReportedTotalTokens ?? 0)),
                Unreported = group.Count(record => record.ReportedTotalTokens == null)
            })
            .ToListAsync(cancellationToken);
        return new ModelUsageSummary(
            since,
            [.. rows.OrderBy(row => row.Role).ThenBy(row => row.Outcome)
                .Select(row => new ModelUsageSummaryRow(row.Role, row.BudgetClass, row.Outcome, row.IsCloud, row.Calls, row.Tokens, row.Unreported))]);
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
