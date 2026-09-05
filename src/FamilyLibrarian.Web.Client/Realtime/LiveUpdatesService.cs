using System.Threading.Channels;
using FamilyLibrarian.Contracts.Realtime;
using Microsoft.AspNetCore.Components.Authorization;

namespace FamilyLibrarian.Web.Client.Realtime;

public enum LiveConnectionState { SignedOut, Connecting, Connected, Reconnecting }

/// <summary>One tab-wide connection, retry loop and refresh coordinator. Topics
/// select local subscribers only; the server chooses every message's audience.</summary>
public sealed partial class LiveUpdatesService(
    ILiveUpdatesConnection connection,
    AuthenticationStateProvider authentication,
    ILogger<LiveUpdatesService> logger) : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly Dictionary<LiveSubscription, LiveUpdateTopics> subscriptions = [];
    private readonly Channel<bool> wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true
    });
    private readonly CancellationTokenSource lifetime = new();
    private Task? runner;
    private bool stopping;
    private bool disposed;

    public LiveConnectionState State { get; private set; } = LiveConnectionState.Connecting;
    public event Action? StateChanged;

    public void Start()
    {
        lock (sync)
        {
            if (runner is not null || disposed) return;
            authentication.AuthenticationStateChanged += OnAuthenticationChanged;
            connection.Changed += OnChanged;
            connection.Closed += OnClosed;
            runner = RunAsync(lifetime.Token);
        }
    }

    public LiveSubscription Subscribe(LiveUpdateTopics topics, Func<CancellationToken, Task> refresh)
    {
        LiveSubscription? subscription = null;
        subscription = new LiveSubscription(refresh, exception => LogRefreshFailure(logger, exception), () =>
        {
            lock (sync) subscriptions.Remove(subscription!);
        });
        lock (sync) subscriptions.Add(subscription, topics);
        return subscription;
    }

    public Task RefreshAsync()
    {
        if (State != LiveConnectionState.Connected) wake.Writer.TryWrite(true);
        return Task.WhenAll(Snapshot(LiveUpdateTopics.All).Select(subscription => subscription.RefreshAsync()));
    }

    private LiveSubscription[] Snapshot(LiveUpdateTopics topics)
    {
        lock (sync) return subscriptions.Where(pair => (pair.Value & topics) != 0).Select(pair => pair.Key).ToArray();
    }

    private void OnChanged(LiveUpdateTopics topics)
    {
        foreach (var subscription in Snapshot(topics)) _ = subscription.RefreshAsync();
    }

    private void OnClosed()
    {
        if (!stopping && !disposed) wake.Writer.TryWrite(true);
    }

    private void OnAuthenticationChanged(Task<AuthenticationState> state) => wake.Writer.TryWrite(true);

    private void SetState(LiveConnectionState state)
    {
        if (State == state || disposed) return;
        State = state;
        StateChanged?.Invoke();
    }

    private async Task RunAsync(CancellationToken token)
    {
        var failures = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                while (wake.Reader.TryRead(out _)) { }
                try
                {
                    stopping = true;
                    await connection.StopAsync(token);
                    stopping = false;
                    var auth = await authentication.GetAuthenticationStateAsync();
                    token.ThrowIfCancellationRequested();
                    if (auth.User.Identity?.IsAuthenticated != true)
                    {
                        SetState(LiveConnectionState.SignedOut);
                        await wake.Reader.ReadAsync(token);
                        continue;
                    }

                    SetState(failures == 0 ? LiveConnectionState.Connecting : LiveConnectionState.Reconnecting);
                    await connection.StartAsync(token);
                    failures = 0;
                    SetState(LiveConnectionState.Connected);
                    // Subscribe first, then read snapshots: reconnect is a full
                    // resync, not an assumption that missed events were replayed.
                    OnChanged(LiveUpdateTopics.All);
                    await wake.Reader.ReadAsync(token);
                    SetState(LiveConnectionState.Reconnecting);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception exception)
                {
                    stopping = false;
                    SetState(LiveConnectionState.Reconnecting);
                    LogConnectionFailure(logger, exception);
                    var delay = TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(failures++, 5)));
                    // Retry connection/authentication failures, including the
                    // initial connection. Refresh/sign-in can interrupt backoff.
                    using var retry = CancellationTokenSource.CreateLinkedTokenSource(token);
                    retry.CancelAfter(delay);
                    try { await wake.Reader.ReadAsync(retry.Token); }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            stopping = true;
            await connection.StopAsync(CancellationToken.None);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        authentication.AuthenticationStateChanged -= OnAuthenticationChanged;
        connection.Changed -= OnChanged;
        connection.Closed -= OnClosed;
        foreach (var subscription in Snapshot(LiveUpdateTopics.All)) subscription.Dispose();
        await lifetime.CancelAsync();
        if (runner is not null) await runner;
        lifetime.Dispose();
        // The DI container owns and disposes the transport after this service.
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Live connection unavailable; retrying.")]
    private static partial void LogConnectionFailure(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A live subscriber could not refresh.")]
    private static partial void LogRefreshFailure(ILogger logger, Exception exception);
}
