using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;

namespace FamilyLibrarian.Web.Realtime;

// No client-invokable operations or client-selected audiences. The publisher
// checks current accounts/roles and sends only topic invalidations.
internal sealed class LiveUpdatesHub(LiveConnections connections) : Hub
{
    public override Task OnConnectedAsync()
    {
        if (!Guid.TryParse(Context.UserIdentifier, out var userId))
        {
            Context.Abort();
            return Task.CompletedTask;
        }

        connections.Add(Context, userId);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}

internal sealed class LiveConnections
{
    private readonly ConcurrentDictionary<string, LiveConnection> connections = new();

    public void Add(HubCallerContext context, Guid userId) => connections[context.ConnectionId] =
        new(context, userId, context.User?.FindFirstValue("AspNet.Identity.SecurityStamp"));

    public void Remove(string connectionId) => connections.TryRemove(connectionId, out _);

    public LiveConnection[] Snapshot() => connections.Values.ToArray();
}

internal sealed record LiveConnection(HubCallerContext Context, Guid UserId, string? SecurityStamp);
