namespace FamilyLibrarian.Web.Catalog;

/// <summary>
/// Holds short-lived, per-user availability runs. The browser receives only an
/// unguessable run id; provider identities stay inside the host.
/// </summary>
public sealed class AvailabilityRunCoordinator
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, AvailabilityRun> runs = [];
    private readonly System.Threading.Channels.Channel<AvailabilityRun> queue = System.Threading.Channels.Channel.CreateBounded<AvailabilityRun>(
        new System.Threading.Channels.BoundedChannelOptions(100) { FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait });

    public AvailabilityRun? Start(Guid ownerId, FamilyLibrarian.Application.Catalog.BookIdentity identity)
    {
        Prune();
        var run = new AvailabilityRun(ownerId, identity);
        runs[run.Id] = run;
        if (!queue.Writer.TryWrite(run))
        {
            run.Cancel();
            runs.TryRemove(run.Id, out _);
            return null;
        }

        return run;
    }

    public bool TryGet(Guid ownerId, Guid runId, out AvailabilityRun? run) =>
        runs.TryGetValue(runId, out run) && run.OwnerId == ownerId;

    public bool Cancel(Guid ownerId, Guid runId)
    {
        if (!TryGet(ownerId, runId, out var run) || run is null)
        {
            return false;
        }

        run.Cancel();
        runs.TryRemove(runId, out _);
        return true;
    }

    internal IAsyncEnumerable<AvailabilityRun> ReadPendingAsync(CancellationToken cancellationToken) =>
        queue.Reader.ReadAllAsync(cancellationToken);

    private void Prune()
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-10);
        foreach (var pair in runs.Where(pair => pair.Value.CompletedAtUtc is { } completed && completed < cutoff))
        {
            runs.TryRemove(pair.Key, out _);
        }
    }
}

public sealed class AvailabilityRun(Guid ownerId, FamilyLibrarian.Application.Catalog.BookIdentity identity) : IDisposable
{
    private readonly object sync = new();
    private readonly List<FamilyLibrarian.Application.Catalog.FulfillmentOption> options = [];
    private readonly CancellationTokenSource cancellation = new();

    public Guid Id { get; } = Guid.NewGuid();
    public Guid OwnerId { get; } = ownerId;
    public FamilyLibrarian.Application.Catalog.BookIdentity Identity { get; } = identity;
    public CancellationToken CancellationToken => cancellation.Token;
    public bool IsComplete { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public void Add(IReadOnlyList<FamilyLibrarian.Application.Catalog.FulfillmentOption> update)
    {
        lock (sync)
        {
            options.AddRange(update);
        }
    }

    public IReadOnlyList<FamilyLibrarian.Application.Catalog.FulfillmentOption> Snapshot()
    {
        lock (sync)
        {
            return options.ToArray();
        }
    }

    public void Complete()
    {
        lock (sync)
        {
            IsComplete = true;
            CompletedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    public void Cancel()
    {
        cancellation.Cancel();
        Complete();
    }

    public void Dispose() => cancellation.Dispose();
}
