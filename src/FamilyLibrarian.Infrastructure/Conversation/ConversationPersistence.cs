using System.Collections.Concurrent;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Conversation;
using FamilyLibrarian.Domain.Conversation;
using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.Conversation;

public sealed class EfConversationSessionStore(AppDbContext database) : IConversationSessionStore
{
    public async Task<ConversationSession?> FindAsync(Guid userId, string transport, string scopeKey, CancellationToken cancellationToken)
    {
        var stored = await database.ConversationSessions.AsNoTracking()
            .FirstOrDefaultAsync(session => session.UserId == userId && session.Transport == transport && session.ScopeKey == scopeKey, cancellationToken);
        return stored is null
            ? null
            : new ConversationSession(
                stored.UserId, stored.Transport, stored.ScopeKey, ConversationState.Deserialize(stored.StateJson),
                stored.TurnCount, stored.StartedAtUtc, stored.LastActivityAtUtc);
    }

    public async Task SaveAsync(ConversationSession session, CancellationToken cancellationToken)
    {
        var json = session.State.Serialize();
        var stored = await database.ConversationSessions.FirstOrDefaultAsync(
            candidate => candidate.UserId == session.UserId && candidate.Transport == session.Transport && candidate.ScopeKey == session.ScopeKey,
            cancellationToken);
        if (stored is null)
        {
            database.ConversationSessions.Add(new StoredConversationSession(
                session.UserId, session.Transport, session.ScopeKey, json, session.TurnCount, session.StartedAtUtc, session.LastActivityAtUtc));
        }
        else
        {
            stored.Update(json, session.TurnCount, session.StartedAtUtc, session.LastActivityAtUtc);
        }

        await database.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// Processed-message and request-key records. Claims use <c>INSERT ... ON CONFLICT DO NOTHING</c> so a duplicate or a race is
/// an ordinary "already there" answer instead of a failed statement that would poison the surrounding transaction.
/// </summary>
public sealed class EfConversationEventLog(AppDbContext database, IClock clock) : IConversationEventLog
{
    /// <summary>Processed-event rows older than this are removed on the next claim. Request keys are kept.</summary>
    public static readonly TimeSpan EventRetention = TimeSpan.FromDays(30);

    public async Task<ConversationEventRecord?> FindAsync(string transport, string scopeKey, string eventId, CancellationToken cancellationToken)
    {
        var stored = await database.ProcessedConversationEvents.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Transport == transport && item.ScopeKey == scopeKey && item.EventId == eventId, cancellationToken);
        return stored is null ? null : new ConversationEventRecord(stored.UserId, stored.ReplyJson, stored.DeliveredAtUtc);
    }

    public async Task<bool> TryClaimAsync(Guid userId, string transport, string scopeKey, string eventId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var cutoff = now - EventRetention;
        await database.ProcessedConversationEvents.Where(item => item.CreatedAtUtc < cutoff).ExecuteDeleteAsync(cancellationToken);
        var inserted = await database.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO conversation.processed_events (id, user_id, transport, scope_key, event_id, created_at_utc)
            VALUES ({Guid.NewGuid()}, {userId}, {transport}, {scopeKey}, {eventId}, {now})
            ON CONFLICT (transport, scope_key, event_id) DO NOTHING
            """, cancellationToken);
        return inserted == 1;
    }

    public Task CompleteAsync(string transport, string scopeKey, string eventId, string replyJson, CancellationToken cancellationToken) =>
        database.ProcessedConversationEvents
            .Where(item => item.Transport == transport && item.ScopeKey == scopeKey && item.EventId == eventId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ReplyJson, replyJson), cancellationToken);

    public Task MarkDeliveredAsync(string transport, string scopeKey, string eventId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        return database.ProcessedConversationEvents
            .Where(item => item.Transport == transport && item.ScopeKey == scopeKey && item.EventId == eventId && item.DeliveredAtUtc == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.DeliveredAtUtc, now), cancellationToken);
    }

    public async Task<(bool Claimed, Guid? ExistingRequestId)> TryClaimRequestKeyAsync(Guid userId, string requestKey, CancellationToken cancellationToken)
    {
        var inserted = await database.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO conversation.request_keys (key, user_id, created_at_utc)
            VALUES ({requestKey}, {userId}, {clock.UtcNow})
            ON CONFLICT (key) DO NOTHING
            """, cancellationToken);
        if (inserted == 1)
        {
            return (true, null);
        }

        var existing = await database.ConversationRequestKeys.AsNoTracking()
            .Where(item => item.Key == requestKey)
            .Select(item => item.RequestId)
            .FirstOrDefaultAsync(cancellationToken);
        return (false, existing);
    }

    public Task AttachRequestAsync(string requestKey, Guid requestId, CancellationToken cancellationToken) =>
        database.ConversationRequestKeys.Where(item => item.Key == requestKey)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.RequestId, requestId), cancellationToken);
}

public sealed class EfConversationTransaction(AppDbContext database) : IConversationTransaction
{
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        // Join a transaction the caller already opened (the request repository does the same).
        if (database.Database.CurrentTransaction is not null)
        {
            return await operation(cancellationToken);
        }

        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var result = await operation(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            // Nothing written inside survives; also drop what the context still thinks is pending.
            database.ChangeTracker.Clear();
            throw;
        }
    }
}

/// <summary>
/// Per-user pacing, kept in memory: at most one turn in flight per user (so a user's turns are applied in order), and a
/// rolling message ceiling. Holds counters keyed by user id only; it is never an identity source.
/// </summary>
public sealed class ConversationLimiter(ConversationOptions options, TimeProvider time) : IConversationLimiter
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxWaitForPreviousTurn = TimeSpan.FromSeconds(5);
    private const int PruneThreshold = 1_000;

    private readonly ConcurrentDictionary<Guid, UserSlot> slots = new();

    public async Task<IDisposable?> TryEnterAsync(Guid userId, CancellationToken cancellationToken)
    {
        var slot = slots.GetOrAdd(userId, _ => new UserSlot());
        if (!slot.TryCount(time.GetUtcNow(), Window, options.MaxMessagesPerMinute))
        {
            return null;
        }

        if (!await slot.Gate.WaitAsync(MaxWaitForPreviousTurn, cancellationToken))
        {
            return null;
        }

        if (slots.Count > PruneThreshold)
        {
            Prune();
        }

        return new Lease(slot.Gate);
    }

    private void Prune()
    {
        var cutoff = time.GetUtcNow() - Window;
        foreach (var pair in slots)
        {
            if (pair.Value.IsIdle(cutoff))
            {
                slots.TryRemove(pair);
            }
        }
    }

    private sealed class UserSlot
    {
        private readonly Lock gate = new();
        private readonly Queue<DateTimeOffset> stamps = new();

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public bool TryCount(DateTimeOffset now, TimeSpan window, int limit)
        {
            lock (gate)
            {
                while (stamps.Count > 0 && now - stamps.Peek() > window)
                {
                    stamps.Dequeue();
                }

                if (stamps.Count >= limit)
                {
                    return false;
                }

                stamps.Enqueue(now);
                return true;
            }
        }

        public bool IsIdle(DateTimeOffset cutoff)
        {
            lock (gate)
            {
                return Gate.CurrentCount == 1 && (stamps.Count == 0 || stamps.Peek() < cutoff);
            }
        }
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}
