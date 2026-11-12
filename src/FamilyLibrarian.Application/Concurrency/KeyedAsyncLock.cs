namespace FamilyLibrarian.Application.Concurrency;

/// <summary>
/// One-at-a-time access per key, for work that must not interleave within this host (a check followed by a save that another
/// request could slip between). Entries are removed when the last holder or waiter leaves, so the table only ever holds keys being
/// worked on right now. Declare it <c>static readonly</c>: request scopes come and go, the lock must outlive them.
/// </summary>
/// <typeparam name="TKey">What is being serialised: a record id, or a normalised name such as a book's catalog reference.</typeparam>
public sealed class KeyedAsyncLock<TKey> where TKey : notnull
{
    private readonly Dictionary<TKey, Entry> entries = [];

    public async Task<IDisposable> AcquireAsync(TKey key, CancellationToken cancellationToken)
    {
        Entry entry;
        lock (entries)
        {
            if (!entries.TryGetValue(key, out entry!))
            {
                entry = new Entry();
                entries[key] = entry;
            }

            entry.Users++;
        }

        try
        {
            await entry.Gate.WaitAsync(cancellationToken);
        }
        catch
        {
            Leave(key, entry, release: false);
            throw;
        }

        return new Releaser(this, key, entry);
    }

    /// <summary>Keys currently held or awaited (for tests).</summary>
    public int ActiveKeys
    {
        get
        {
            lock (entries)
            {
                return entries.Count;
            }
        }
    }

    private void Leave(TKey key, Entry entry, bool release)
    {
        if (release)
        {
            entry.Gate.Release();
        }

        lock (entries)
        {
            if (--entry.Users == 0)
            {
                entries.Remove(key);
            }
        }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public int Users { get; set; }
    }

    private sealed class Releaser(KeyedAsyncLock<TKey> owner, TKey key, Entry entry) : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                owner.Leave(key, entry, release: true);
            }
        }
    }
}
