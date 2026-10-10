using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Application.Catalog;

/// <summary>
/// Collapses identical interactive external-provider searches into one call.
/// A catalog search page asks for availability once per result, and several
/// results (different metadata sources or editions) describe the same book, so
/// without this each of them sends the same ebook and audiobook search to every
/// external provider. A concurrent caller joins the search already running; a
/// caller arriving shortly after reuses its result.
/// </summary>
/// <remarks>
/// Singleton: it holds no per-request state beyond the entries it owns. Only the
/// browsing/availability path uses it. Acquisition and scheduled rechecks call
/// <see cref="ExternalCandidateAvailabilityChecker.FindForProviderAsync"/>
/// directly because they consume per-candidate acquire tokens and must see a
/// fresh response. The shared search is cancelled only when its last waiter
/// leaves (a superseded browser search), or when it exceeds
/// <see cref="SearchTimeout"/>; one caller leaving never cancels another's search.
/// Failures and cancellations are never cached.
/// </remarks>
public sealed class ExternalSearchCoalescer(TimeProvider timeProvider)
{
    internal static readonly TimeSpan ResultLifetime = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan SearchTimeout = TimeSpan.FromMinutes(2);

    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);

    public async Task<IReadOnlyList<FulfillmentOption>> GetOrAddAsync(
        string key,
        Func<CancellationToken, Task<IReadOnlyList<FulfillmentOption>>> search,
        CancellationToken cancellationToken)
    {
        Entry entry;
        lock (gate)
        {
            Prune();
            if (!entries.TryGetValue(key, out entry!))
            {
                entry = new Entry(timeProvider, SearchTimeout);
                entries[key] = entry;
                entry.Task = RunAsync(key, entry, search);
            }

            entry.Waiters++;
        }

        try
        {
            return await entry.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            lock (gate)
            {
                entry.Waiters--;
                if (entry.Waiters == 0 && !entry.Task.IsCompleted)
                {
                    // Nobody is waiting for this any more (every browser search
                    // that wanted it was superseded): stop the provider call.
                    Forget(key, entry);
                    entry.Cancel();
                }
            }
        }
    }

    /// <summary>
    /// Builds the coalescing key from everything that shapes the provider
    /// request or the match verification, so only truly identical searches share.
    /// </summary>
    public static string CreateKey(
        Domain.Providers.ExternalProvider provider, BookIdentity identity, RequestMediaType mediaType)
    {
        var authors = identity.Authors is { Count: > 0 }
            ? identity.Authors.Select(author => author.Name)
            : identity.Author is null ? [] : [identity.Author];

        return string.Join(
            '\u001f',
            provider.ProviderId.ToUpperInvariant(),
            provider.BaseUrl.ToString().ToUpperInvariant(),
            mediaType,
            Normalize(identity.Title),
            string.Join('|', authors.Select(Normalize).Order(StringComparer.Ordinal)),
            string.Join('|', (identity.Series ?? []).Select(series => Normalize(series.Name)).Order(StringComparer.Ordinal)),
            string.Join('|', identity.Isbn13Candidates.Select(Normalize).Order(StringComparer.Ordinal)),
            Normalize(identity.Language),
            identity.PublicationYear,
            Normalize(identity.Publisher),
            string.Join('|', (identity.AlternateTitles ?? []).Select(Normalize).Order(StringComparer.Ordinal)));
    }

    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value)
        ? string.Empty
        : string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    private async Task<IReadOnlyList<FulfillmentOption>> RunAsync(
        string key,
        Entry entry,
        Func<CancellationToken, Task<IReadOnlyList<FulfillmentOption>>> search)
    {
        // Entered while the caller holds the lock; yield so the provider call
        // never runs under it.
        await Task.Yield();
        try
        {
            var result = await search(entry.CancellationToken);
            lock (gate)
            {
                entry.CompletedAtUtc = timeProvider.GetUtcNow();
            }

            return result;
        }
        catch
        {
            lock (gate)
            {
                Forget(key, entry);
            }

            throw;
        }
        finally
        {
            entry.Dispose();
        }
    }

    private void Forget(string key, Entry entry)
    {
        if (entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
        {
            entries.Remove(key);
        }
    }

    private void Prune()
    {
        var cutoff = timeProvider.GetUtcNow() - ResultLifetime;
        foreach (var pair in entries.Where(pair => pair.Value.CompletedAtUtc is { } completed && completed < cutoff).ToArray())
        {
            entries.Remove(pair.Key);
        }
    }

    private sealed class Entry : IDisposable
    {
        private readonly CancellationTokenSource cancellation;
        private bool disposed;

        public Entry(TimeProvider timeProvider, TimeSpan timeout) =>
            cancellation = new CancellationTokenSource(timeout, timeProvider);

        // Set exactly once, under the coalescer's lock, right after construction.
        public Task<IReadOnlyList<FulfillmentOption>> Task { get; set; } = null!;

        public int Waiters { get; set; }

        public DateTimeOffset? CompletedAtUtc { get; set; }

        public CancellationToken CancellationToken => cancellation.Token;

        // Called under the coalescer's lock; Dispose takes the same entry lock
        // so the two cannot race into an ObjectDisposedException.
        public void Cancel()
        {
            lock (cancellation)
            {
                if (!disposed)
                {
                    cancellation.Cancel();
                }
            }
        }

        public void Dispose()
        {
            lock (cancellation)
            {
                disposed = true;
                cancellation.Dispose();
            }
        }
    }
}
