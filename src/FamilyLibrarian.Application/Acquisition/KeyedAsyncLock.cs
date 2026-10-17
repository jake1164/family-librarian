namespace FamilyLibrarian.Application.Acquisition;

/// <summary>
/// One-at-a-time access per key, for work that must not interleave within this host (a check followed by a save that
/// another request could slip between). Entries are removed when the last holder or waiter leaves, so the table only
/// ever holds keys being worked on right now. Declare it <c>static readonly</c>: request scopes come and go, the lock
/// must outlive them.
/// </summary>
internal sealed class KeyedAsyncLock
{
    private readonly Dictionary<Guid, Entry> _entries = [];

    public async Task<IDisposable> AcquireAsync(Guid key, CancellationToken cancellationToken)
    {
        Entry entry;
        lock (_entries)
        {
            if (!_entries.TryGetValue(key, out entry!))
            {
                entry = new Entry();
                _entries[key] = entry;
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

    private void Leave(Guid key, Entry entry, bool release)
    {
        if (release)
        {
            entry.Gate.Release();
        }

        lock (_entries)
        {
            if (--entry.Users == 0)
            {
                _entries.Remove(key);
            }
        }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public int Users { get; set; }
    }

    private sealed class Releaser(KeyedAsyncLock owner, Guid key, Entry entry) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Leave(key, entry, release: true);
            }
        }
    }
}
