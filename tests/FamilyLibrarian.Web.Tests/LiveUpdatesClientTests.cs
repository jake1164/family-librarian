using System.Security.Claims;
using System.Threading.Channels;
using FamilyLibrarian.Contracts.Realtime;
using FamilyLibrarian.Web.Client.Realtime;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging.Abstractions;

namespace FamilyLibrarian.Web.Tests;

[TestClass]
public sealed class LiveUpdatesClientTests
{
    [TestMethod]
    public async Task MultipleSubscribersShareOneConnectionAndReceiveOnlyTheirTopics()
    {
        await using var transport = new FakeConnection();
        await using var service = CreateService(transport);
        var requests = Channel.CreateUnbounded<bool>();
        var security = Channel.CreateUnbounded<bool>();
        using var first = service.Subscribe(LiveUpdateTopics.Requests, _ =>
        { requests.Writer.TryWrite(true); return Task.CompletedTask; });
        using var second = service.Subscribe(LiveUpdateTopics.Security, _ =>
        { security.Writer.TryWrite(true); return Task.CompletedTask; });
        service.Start();
        service.Start();
        await NextAsync(requests);
        await NextAsync(security);
        Assert.AreEqual(1, transport.Starts);

        transport.Emit(LiveUpdateTopics.Requests);
        await NextAsync(requests);
        Assert.IsFalse(security.Reader.TryRead(out _));
        second.Dispose();
        transport.Emit(LiveUpdateTopics.Security);
        Assert.IsFalse(security.Reader.TryRead(out _));
    }

    [TestMethod]
    public async Task InitialFailureRetriesAndReconnectRefreshesAllSubscribers()
    {
        await using var transport = new FakeConnection { FailuresRemaining = 1 };
        await using var service = CreateService(transport);
        var refreshes = Channel.CreateUnbounded<bool>();
        using var subscription = service.Subscribe(LiveUpdateTopics.All, _ =>
        { refreshes.Writer.TryWrite(true); return Task.CompletedTask; });
        service.Start();
        await NextAsync(refreshes);
        Assert.AreEqual(2, transport.Starts);
        transport.Disconnect();
        await NextAsync(refreshes);
        Assert.AreEqual(3, transport.Starts);
        Assert.AreEqual(LiveConnectionState.Connected, service.State);
        Assert.AreEqual(1, transport.MaximumConcurrentConnections);
    }

    [TestMethod]
    public async Task AuthenticationChangesStopAndRestartTheSharedConnection()
    {
        await using var transport = new FakeConnection();
        var authentication = new FakeAuthentication();
        await using var service = CreateService(transport, authentication);
        var states = Channel.CreateUnbounded<LiveConnectionState>();
        service.StateChanged += () => states.Writer.TryWrite(service.State);
        service.Start();
        await WaitForStateAsync(states, LiveConnectionState.Connected);
        authentication.SetAuthenticated(false);
        await WaitForStateAsync(states, LiveConnectionState.SignedOut);
        Assert.AreEqual(0, transport.ActiveConnections);
        authentication.SetAuthenticated(true);
        await WaitForStateAsync(states, LiveConnectionState.Connected);
        Assert.AreEqual(2, transport.Starts);
        await service.DisposeAsync();
        Assert.AreEqual(0, transport.ActiveConnections);
    }

    [TestMethod]
    public async Task ChangesDuringARefreshAreCoalescedButNotLost()
    {
        await using var transport = new FakeConnection();
        await using var service = CreateService(transport);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var subscription = service.Subscribe(LiveUpdateTopics.Requests, async token =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
        });
        var first = subscription.RefreshAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = subscription.RefreshAsync();
        var third = subscription.RefreshAsync();
        Assert.AreEqual(1, calls);
        release.TrySetResult();
        await Task.WhenAll(first, second, third).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public async Task SubscriberFailureDoesNotPreventOtherRefreshesAndDisposalCancelsWork()
    {
        await using var transport = new FakeConnection();
        await using var service = CreateService(transport);
        using var failing = service.Subscribe(LiveUpdateTopics.Requests, _ => throw new HttpRequestException());
        var calls = 0;
        using var working = service.Subscribe(LiveUpdateTopics.Requests, _ =>
        { calls++; return Task.CompletedTask; });
        await service.RefreshAsync();
        Assert.AreEqual(1, calls);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var pending = service.Subscribe(LiveUpdateTopics.Security, async token =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        var refresh = pending.RefreshAsync();
        await entered.Task;
        pending.Dispose();
        await refresh.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static LiveUpdatesService CreateService(FakeConnection connection, FakeAuthentication? authentication = null) =>
        new(connection, authentication ?? new FakeAuthentication(), NullLogger<LiveUpdatesService>.Instance);

    private static async Task NextAsync(Channel<bool> channel)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await channel.Reader.ReadAsync(timeout.Token);
    }

    private static async Task WaitForStateAsync(Channel<LiveConnectionState> states, LiveConnectionState expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (await states.Reader.ReadAsync(timeout.Token) != expected) { }
    }

    private sealed class FakeAuthentication : AuthenticationStateProvider
    {
        private bool authenticated = true;
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(
            new AuthenticationState(new ClaimsPrincipal(authenticated
                ? new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "test")], "test")
                : new ClaimsIdentity())));
        public void SetAuthenticated(bool value)
        {
            authenticated = value;
            NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
        }
    }

    private sealed class FakeConnection : ILiveUpdatesConnection
    {
        public event Action<LiveUpdateTopics>? Changed;
        public event Action? Closed;
        public int Starts { get; private set; }
        public int ActiveConnections { get; private set; }
        public int MaximumConcurrentConnections { get; private set; }
        public int FailuresRemaining { get; set; }
        public Task StartAsync(CancellationToken cancellationToken)
        {
            Starts++;
            if (FailuresRemaining-- > 0) throw new HttpRequestException("Offline");
            ActiveConnections++;
            MaximumConcurrentConnections = Math.Max(MaximumConcurrentConnections, ActiveConnections);
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken cancellationToken)
        {
            if (ActiveConnections > 0) Disconnect();
            return Task.CompletedTask;
        }
        public void Disconnect() { ActiveConnections = 0; Closed?.Invoke(); }
        public void Emit(LiveUpdateTopics topics) => Changed?.Invoke(topics);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
