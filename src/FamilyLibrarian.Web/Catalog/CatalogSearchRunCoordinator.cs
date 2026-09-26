using System.Collections.Concurrent;
using System.Threading.Channels;
using FamilyLibrarian.Application.Catalog;

namespace FamilyLibrarian.Web.Catalog;

public sealed class CatalogSearchRunCoordinator
{
    private readonly ConcurrentDictionary<Guid, CatalogSearchRun> runs = [];
    private readonly Channel<CatalogSearchRun> queue = Channel.CreateBounded<CatalogSearchRun>(
        new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.Wait });

    public CatalogSearchRun? Start(Guid ownerId, BookSearchQuery query)
    {
        Prune();
        var run = new CatalogSearchRun(ownerId, query);
        runs[run.Id] = run;
        if (queue.Writer.TryWrite(run)) return run;
        run.Cancel();
        runs.TryRemove(run.Id, out _);
        return null;
    }

    public bool TryGet(Guid ownerId, Guid runId, out CatalogSearchRun? run) =>
        runs.TryGetValue(runId, out run) && run.OwnerId == ownerId;

    public bool Cancel(Guid ownerId, Guid runId)
    {
        if (!TryGet(ownerId, runId, out var run) || run is null) return false;
        run.Cancel();
        runs.TryRemove(runId, out _);
        return true;
    }

    internal IAsyncEnumerable<CatalogSearchRun> ReadPendingAsync(CancellationToken token) => queue.Reader.ReadAllAsync(token);

    private void Prune()
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-10);
        foreach (var pair in runs.Where(pair => pair.Value.CompletedAtUtc is { } completed && completed < cutoff))
            runs.TryRemove(pair.Key, out _);
    }
}

public sealed class CatalogSearchRun(Guid ownerId, BookSearchQuery query) : IDisposable
{
    private readonly object sync = new();
    private readonly List<(string ProviderId, string ProviderName, bool Succeeded, IReadOnlyList<BookCandidate> Candidates, bool HasMore)> results = [];
    private readonly CancellationTokenSource cancellation = new();

    public Guid Id { get; } = Guid.NewGuid();
    public Guid OwnerId { get; } = ownerId;
    public BookSearchQuery Query { get; } = query;
    public CancellationToken CancellationToken => cancellation.Token;
    public bool IsComplete { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public void Add(string id, string name, bool succeeded, IReadOnlyList<BookCandidate> candidates, bool hasMore)
    {
        lock (sync) results.Add((id, name, succeeded, candidates, hasMore));
    }

    public IReadOnlyList<(string ProviderId, string ProviderName, bool Succeeded, IReadOnlyList<BookCandidate> Candidates, bool HasMore)> Snapshot()
    {
        lock (sync) return results.ToArray();
    }

    public void Complete()
    {
        lock (sync) { IsComplete = true; CompletedAtUtc = DateTimeOffset.UtcNow; }
    }

    public void Cancel()
    {
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
        Complete();
    }
    public void Dispose() => cancellation.Dispose();
}
