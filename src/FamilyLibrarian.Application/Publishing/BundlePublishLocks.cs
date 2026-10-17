namespace FamilyLibrarian.Application.Publishing;

/// <summary>
/// One-at-a-time access per bundle id, shared by every request scope in this host. Entries are removed when the last
/// holder or waiter leaves, so the table only ever holds bundles being published right now.
/// </summary>
internal static class BundlePublishLocks
{
    private static readonly Dictionary<Guid, Entry> Entries = [];

    public static async Task<IDisposable> AcquireAsync(Guid bundleId, CancellationToken cancellationToken)
    {
        Entry entry;
        lock (Entries)
        {
            if (!Entries.TryGetValue(bundleId, out entry!))
            {
                entry = new Entry();
                Entries[bundleId] = entry;
            }

            entry.Users++;
        }

        try
        {
            await entry.Gate.WaitAsync(cancellationToken);
        }
        catch
        {
            Leave(bundleId, entry, release: false);
            throw;
        }

        return new Releaser(bundleId, entry);
    }

    private static void Leave(Guid bundleId, Entry entry, bool release)
    {
        if (release)
        {
            entry.Gate.Release();
        }

        lock (Entries)
        {
            if (--entry.Users == 0)
            {
                Entries.Remove(bundleId);
            }
        }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public int Users { get; set; }
    }

    private sealed class Releaser(Guid bundleId, Entry entry) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                Leave(bundleId, entry, release: true);
            }
        }
    }
}
