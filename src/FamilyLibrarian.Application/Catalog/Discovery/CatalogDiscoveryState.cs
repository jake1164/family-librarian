
namespace FamilyLibrarian.Application.Catalog.Discovery;

/// <summary>
/// Host-memory ledgers for explicit source lookups: one <see cref="DiscoveryJourney"/> per owner and root browse context, found
/// again by id from any window that lookup produced. Single-process and in-memory like the context store; it makes no
/// distributed-accounting claim and is lost on restart (a lost journey is simply a lookup that is no longer continuable).
/// </summary>
/// <remarks>
/// A journey expires at the absolute instant of the root context that issued the subject, never later: a lookup cannot extend
/// the life of the search it came from, and its windows and continuations inherit that single expiry.
/// </remarks>
public sealed class CatalogDiscoveryJourneys
{
    public const int MaxJourneysPerOwner = 10;
    public const int MaxJourneysTotal = 100;

    private readonly Lock gate = new();
    private readonly Dictionary<Guid, Entry> byId = [];
    private readonly Dictionary<(Guid Owner, Guid Root), Guid> byRoot = [];
    private long sequence;

    /// <summary>The journey for this owner's root context, created on first use with the root's remaining life; null when that life is spent.</summary>
    public DiscoveryJourney? GetOrCreateForRoot(
        Guid ownerId, Guid rootContextId, DateTimeOffset rootExpiresAtUtc, DateTimeOffset now, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        lock (gate)
        {
            Prune();
            if (byRoot.TryGetValue((ownerId, rootContextId), out var known) && byId.TryGetValue(known, out var existing))
            {
                return existing.Journey;
            }

            var remaining = rootExpiresAtUtc - now;
            if (remaining <= TimeSpan.Zero) return null;

            while (byId.Values.Count(entry => entry.OwnerId == ownerId) >= MaxJourneysPerOwner) Remove(Oldest(ownerId));
            while (byId.Count >= MaxJourneysTotal) Remove(Oldest(null));

            var journey = new DiscoveryJourney(ownerId, DiscoveryLimits.Default with { Lifetime = remaining }, clock);
            byId[journey.JourneyId] = new Entry(ownerId, rootContextId, journey, ++sequence);
            byRoot[(ownerId, rootContextId)] = journey.JourneyId;
            return journey;
        }
    }

    /// <summary>The journey a window belongs to, only for its own owner and only while it has not expired.</summary>
    public DiscoveryJourney? Find(Guid ownerId, Guid journeyId)
    {
        lock (gate)
        {
            Prune();
            return byId.TryGetValue(journeyId, out var entry) && entry.OwnerId == ownerId ? entry.Journey : null;
        }
    }

    public int Count
    {
        get
        {
            lock (gate) return byId.Count;
        }
    }

    private void Prune()
    {
        foreach (var entry in byId.Values.Where(entry => entry.Journey.IsExpired).ToArray()) Remove(entry);
    }

    private Entry Oldest(Guid? ownerId) =>
        byId.Values.Where(entry => ownerId is null || entry.OwnerId == ownerId).MinBy(entry => entry.Sequence)!;

    private void Remove(Entry entry)
    {
        byId.Remove(entry.Journey.JourneyId);
        if (byRoot.TryGetValue((entry.OwnerId, entry.RootContextId), out var id) && id == entry.Journey.JourneyId)
        {
            byRoot.Remove((entry.OwnerId, entry.RootContextId));
        }
    }

    private sealed record Entry(Guid OwnerId, Guid RootContextId, DiscoveryJourney Journey, long Sequence);
}

public enum ExpansionAdmission
{
    Admitted = 0,

    /// <summary>The user already started the allowed number of lookups in the current window.</summary>
    RateLimited = 1,

    /// <summary>The user already has a lookup running. There is no queue: the second request is refused, not held.</summary>
    Busy = 2
}

/// <summary>
/// The per-user admission rule for explicit source lookups: a fixed window of <see cref="MaxStarts"/> starts per
/// <see cref="Window"/> and at most one lookup running, with no queue. These are initial application defaults, not provider
/// quotas. The rule is checked before any provider is touched, and the clock is passed in so it can be pinned.
/// </summary>
public sealed class CatalogExpansionLimiter
{
    public const int DefaultMaxStarts = 10;

    public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(60);

    private readonly Lock gate = new();
    private readonly Dictionary<Guid, State> states = [];

    public CatalogExpansionLimiter() : this(DefaultMaxStarts, DefaultWindow)
    {
    }

    public CatalogExpansionLimiter(int maxStarts, TimeSpan window)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxStarts, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        MaxStarts = maxStarts;
        Window = window;
    }

    public int MaxStarts { get; }

    public TimeSpan Window { get; }

    /// <summary>
    /// Admits one start. A permit holds the user's single running slot until it is disposed; a refusal consumes nothing.
    /// <paramref name="retryAfter"/> is how long until the current window ends (zero for a busy refusal).
    /// </summary>
    public ExpansionAdmission TryStart(Guid ownerId, DateTimeOffset now, out IDisposable? permit, out TimeSpan retryAfter)
    {
        lock (gate)
        {
            // Idle users cannot pile up: an entry whose window ended and which is not running is dropped.
            if (states.Count > 1024)
            {
                foreach (var stale in states.Where(pair => !pair.Value.Active && now >= pair.Value.WindowStart + Window).Select(pair => pair.Key).ToArray())
                {
                    states.Remove(stale);
                }
            }

            var state = states.TryGetValue(ownerId, out var found) ? found : states[ownerId] = new State(now);
            if (now >= state.WindowStart + Window)
            {
                state.WindowStart = now;
                state.Count = 0;
            }

            permit = null;
            if (state.Active)
            {
                retryAfter = TimeSpan.Zero;
                return ExpansionAdmission.Busy;
            }

            if (state.Count >= MaxStarts)
            {
                retryAfter = state.WindowStart + Window - now;
                return ExpansionAdmission.RateLimited;
            }

            state.Count++;
            state.Active = true;
            retryAfter = TimeSpan.Zero;
            permit = new Permit(this, ownerId);
            return ExpansionAdmission.Admitted;
        }
    }

    private void Release(Guid ownerId)
    {
        lock (gate)
        {
            if (states.TryGetValue(ownerId, out var state)) state.Active = false;
        }
    }

    private sealed class State(DateTimeOffset windowStart)
    {
        public DateTimeOffset WindowStart { get; set; } = windowStart;

        public int Count { get; set; }

        public bool Active { get; set; }
    }

    private sealed class Permit(CatalogExpansionLimiter limiter, Guid ownerId) : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0) limiter.Release(ownerId);
        }
    }
}
