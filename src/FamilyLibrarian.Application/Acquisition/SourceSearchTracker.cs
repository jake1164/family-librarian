using System.Collections.Concurrent;

namespace FamilyLibrarian.Application.Acquisition;

/// <summary>Host-local searches, including catalog browsing. A lease ends even when a search fails or is cancelled.</summary>
public sealed class SourceSearchTracker
{
    private readonly ConcurrentDictionary<Guid, string> searches = new();
    private readonly ConcurrentDictionary<string, long> startedCounts = new(StringComparer.OrdinalIgnoreCase);
    public DateTimeOffset CountsSinceUtc { get; } = DateTimeOffset.UtcNow;
    public event Action? Changed;

    public IReadOnlyList<string> Snapshot() => searches.Values.ToArray();

    /// <summary>Actual calls started in this host lifetime, including cancelled or failed calls.</summary>
    public long StartedCount(string providerId) => startedCounts.GetValueOrDefault(providerId);

    public IDisposable Begin(string providerId)
    {
        var id = Guid.NewGuid();
        searches[id] = providerId;
        startedCounts.AddOrUpdate(providerId, 1, static (_, count) => count + 1);
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
