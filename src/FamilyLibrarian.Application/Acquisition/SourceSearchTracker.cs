using System.Collections.Concurrent;

namespace FamilyLibrarian.Application.Acquisition;

/// <summary>Host-local searches, including catalog browsing. A lease ends even when a search fails or is cancelled.</summary>
public sealed class SourceSearchTracker
{
    private readonly ConcurrentDictionary<Guid, string> searches = new();
    public event Action? Changed;

    public IReadOnlyList<string> Snapshot() => searches.Values.ToArray();

    public IDisposable Begin(string providerId)
    {
        var id = Guid.NewGuid();
        searches[id] = providerId;
        Changed?.Invoke();
        return new Lease(this, id);
    }

    private sealed class Lease(SourceSearchTracker tracker, Guid id) : IDisposable
    {
        public void Dispose()
        {
            if (tracker.searches.TryRemove(id, out _)) tracker.Changed?.Invoke();
        }
    }
}
