using System.Collections.Concurrent;

namespace FamilyLibrarian.Application.Conversation;

/// <summary>
/// An asynchronous mutual-exclusion lock per string key, with no entry kept for a key nobody holds or awaits. Used to make
/// two people asking for the same never-before-seen book at the same moment take turns creating its catalog record, so the
/// second finds the first's instead of colliding on the catalog's unique references.
/// </summary>
public sealed class KeyedAsyncLock
{
    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly Lock gate = new();

    public async Task<IDisposable> AcquireAsync(string key, CancellationToken cancellationToken)
    {
        Entry entry;
        lock (gate)
        {
            entry = entries.GetOrAdd(key, _ => new Entry());
            entry.Users++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken);
        }
        catch
        {
            Leave(key, entry);
            throw;
        }

        return new Releaser(this, key, entry);
    }

    /// <summary>Keys currently held or awaited (for tests).</summary>
    public int ActiveKeys
    {
        get
        {
            lock (gate)
            {
                return entries.Count;
            }
        }
    }

    private void Leave(string key, Entry entry)
    {
        lock (gate)
        {
            if (--entry.Users == 0)
            {
                entries.TryRemove(key, out _);
            }
        }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int Users { get; set; }
    }

    private sealed class Releaser(KeyedAsyncLock owner, string key, Entry entry) : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                entry.Semaphore.Release();
                owner.Leave(key, entry);
            }
        }
    }
}
