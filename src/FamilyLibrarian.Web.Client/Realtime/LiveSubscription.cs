namespace FamilyLibrarian.Web.Client.Realtime;

/// <summary>Serializes one subscriber's refreshes and coalesces bursts without
/// dropping a change that arrives while its API snapshot is being read.</summary>
public sealed class LiveSubscription : IDisposable
{
    private readonly object sync = new();
    private readonly Func<CancellationToken, Task> refresh;
    private readonly Action<Exception> onError;
    private readonly Action remove;
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken token;
    private TaskCompletionSource? running;
    private bool requested;
    private bool disposed;

    internal LiveSubscription(Func<CancellationToken, Task> refresh, Action<Exception> onError, Action remove)
    {
        this.refresh = refresh;
        this.onError = onError;
        this.remove = remove;
        token = lifetime.Token;
    }

    public Task RefreshAsync()
    {
        TaskCompletionSource completion;
        lock (sync)
        {
            if (disposed) return Task.CompletedTask;
            requested = true;
            if (running is not null) return running.Task;
            completion = running = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        _ = RunAsync(completion);
        return completion.Task;
    }

    private async Task RunAsync(TaskCompletionSource completion)
    {
        while (true)
        {
            lock (sync) requested = false;
            try
            {
                if (!token.IsCancellationRequested) await refresh(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception exception) { onError(exception); }

            lock (sync)
            {
                if (requested && !disposed) continue;
                running = null;
                completion.TrySetResult();
                return;
            }
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
        }
        remove();
        lifetime.Cancel();
        lifetime.Dispose();
    }
}
