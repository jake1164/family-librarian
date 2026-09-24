using System.Net.WebSockets;
using FamilyLibrarian.Application.Providers;

namespace FamilyLibrarian.Application.Acquisition;

/// <summary>
/// Opens the outbound, provider-facing leg of a brokered remote-view session
/// (docs/04-external-provider-http-protocol.md §8 "Optional interaction
/// view"). Deliberately separate from <see cref="IExternalProviderClient"/>:
/// that interface is one HTTP request in, one parsed JSON response out; this
/// is a raw, long-lived, bidirectional byte stream with no JSON envelope at
/// all, and mixing the two shapes into one interface would make every
/// existing HTTP-only implementation carry a method it can never sensibly
/// exercise.
/// </summary>
public interface IProviderRemoteViewClient
{
    /// <summary>
    /// Connects to a provider's <c>GET /acquire/{providerJobId}/interaction/view</c>
    /// endpoint, authenticated the same way every other provider call is
    /// (<paramref name="apiKey"/> as a bearer header), routed the same way
    /// (<paramref name="route"/>, resolved by <see cref="PrivateEgressRouteResolver"/>
    /// exactly like the HTTP control-plane calls).
    /// </summary>
    Task<IProviderRemoteViewConnection> ConnectAsync(
        string baseUrl,
        string? apiKey,
        string providerJobId,
        EgressRoute route,
        CancellationToken cancellationToken);
}

/// <summary>One open provider-facing view socket. Disposing closes it.</summary>
public interface IProviderRemoteViewConnection : IAsyncDisposable
{
    WebSocketState State { get; }

    Task SendAsync(ReadOnlyMemory<byte> buffer, bool endOfMessage, CancellationToken cancellationToken);

    Task<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken);

    Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken cancellationToken);
}
