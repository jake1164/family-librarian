using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace FamilyLibrarian.Web.Realtime;

internal sealed class LiveUpdateBuffer
{
    public LiveChanges? Saving { get; set; }
    public LiveChanges? Pending { get; set; }
    public Guid? TransactionId { get; set; }

    public LiveChanges? TakePending()
    {
        var result = Pending;
        Pending = null;
        return result;
    }
}

internal sealed class LiveUpdatesSaveInterceptor(LiveUpdateBuffer buffer, LiveUpdatesPublisher publisher)
    : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (buffer.TransactionId != eventData.Context?.Database.CurrentTransaction?.TransactionId)
            buffer.Pending = null;
        buffer.Saving = eventData.Context is { } context ? LiveChanges.Capture(context) : null;
        return ValueTask.FromResult(result);
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        buffer.TransactionId = eventData.Context?.Database.CurrentTransaction?.TransactionId;
        if (buffer.Saving is { } changes)
        {
            (buffer.Pending ??= new LiveChanges()).Merge(changes);
            buffer.Saving = null;
        }

        if (eventData.Context?.Database.CurrentTransaction is null && buffer.TakePending() is { } committed)
            await publisher.PublishAsync(committed);
        return result;
    }

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        buffer.Saving = null;
        return Task.CompletedTask;
    }
}

internal sealed class LiveUpdatesTransactionInterceptor(LiveUpdateBuffer buffer, LiveUpdatesPublisher publisher)
    : DbTransactionInterceptor
{
    public override async Task TransactionCommittedAsync(DbTransaction transaction,
        TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        if (buffer.TakePending() is { } committed) await publisher.PublishAsync(committed);
    }

    public override Task TransactionRolledBackAsync(DbTransaction transaction,
        TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        buffer.Pending = null;
        buffer.Saving = null;
        return Task.CompletedTask;
    }
}
