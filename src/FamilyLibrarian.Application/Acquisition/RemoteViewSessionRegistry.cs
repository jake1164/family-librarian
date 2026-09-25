using System.Collections.Concurrent;

namespace FamilyLibrarian.Application.Acquisition;

/// <summary>
/// Tracks the at-most-one-active-viewer-per-job rule for brokered remote
/// view sessions. Process-wide, in-memory state — same shape and same
/// single-instance assumption as the SignalR <c>LiveConnections</c> registry
/// (Family Librarian's default deployment is a single app host + PostgreSQL;
/// there is no multi-instance coordination story for either registry today).
/// Registered as a singleton, not per-request scoped, so a session claimed
/// by one request is visible to the request that tries to open a second one.
/// </summary>
public sealed class RemoteViewSessionRegistry
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> sessions = new();

    /// <summary>
    /// Claims the job for a new view session. Returns false (and does not
    /// register anything) if a session is already active for this job.
    /// </summary>
    public bool TryAcquire(Guid jobId, out CancellationTokenSource closeSignal)
    {
        var candidate = new CancellationTokenSource();
        if (sessions.TryAdd(jobId, candidate))
        {
            closeSignal = candidate;
            return true;
        }

        candidate.Dispose();
        closeSignal = null!;
        return false;
    }

    /// <summary>Whether a view session is currently active for this job — HUMAN-ACQ-1's claim-lease check.</summary>
    public bool IsActive(Guid jobId) => sessions.ContainsKey(jobId);

    /// <summary>Releases the job so a later session may claim it. Idempotent.</summary>
    public void Release(Guid jobId)
    {
        if (sessions.TryRemove(jobId, out var closeSignal))
        {
            closeSignal.Dispose();
        }
    }

    /// <summary>
    /// Signals an active session for this job to close immediately (e.g. an
    /// administrator cancelled the underlying interaction). Returns false if
    /// no session is currently active — that is not an error, just nothing
    /// to close.
    /// </summary>
    public bool RequestClose(Guid jobId)
    {
        if (!sessions.TryGetValue(jobId, out var closeSignal))
        {
            return false;
        }

        closeSignal.Cancel();
        return true;
    }

    /// <summary>Ends the old viewer before a replacement is allowed to connect.</summary>
    public async Task<bool> RequestCloseAndWaitAsync(Guid jobId, CancellationToken cancellationToken)
    {
        if (!RequestClose(jobId))
        {
            return true;
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (IsActive(jobId) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50, cancellationToken);
        }

        return !IsActive(jobId);
    }
}
