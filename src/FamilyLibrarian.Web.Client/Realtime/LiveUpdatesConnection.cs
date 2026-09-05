using FamilyLibrarian.Contracts.Realtime;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;

namespace FamilyLibrarian.Web.Client.Realtime;

public interface ILiveUpdatesConnection : IAsyncDisposable
{
    event Action<LiveUpdateTopics>? Changed;
    event Action? Closed;
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

public sealed class LiveUpdatesConnection : ILiveUpdatesConnection
{
    private readonly HubConnection connection;

    public LiveUpdatesConnection(NavigationManager navigation)
    {
        connection = new HubConnectionBuilder().WithUrl(navigation.ToAbsoluteUri(LiveUpdates.HubPath)).Build();
        connection.On<LiveUpdateTopics>(LiveUpdates.Changed, topics => Changed?.Invoke(topics));
        connection.Closed += _ =>
        {
            Closed?.Invoke();
            return Task.CompletedTask;
        };
    }

    public event Action<LiveUpdateTopics>? Changed;
    public event Action? Closed;

    public Task StartAsync(CancellationToken cancellationToken) => connection.StartAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => connection.StopAsync(cancellationToken);
    public ValueTask DisposeAsync() => connection.DisposeAsync();
}
