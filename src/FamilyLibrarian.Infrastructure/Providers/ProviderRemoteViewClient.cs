using System.Net.WebSockets;
using FamilyLibrarian.Application.Acquisition;

namespace FamilyLibrarian.Infrastructure.Providers;

/// <summary>
/// Opens the outbound leg of a brokered remote-view session (docs/04
/// §8 "Optional interaction view") with a raw <see cref="ClientWebSocket"/>
/// — a genuinely different shape from <see cref="ExternalProviderClient"/>'s
/// HttpClient/JSON calls, so it is its own small type rather than a method
/// bolted onto that one.
/// </summary>
public sealed class ProviderRemoteViewClient : IProviderRemoteViewClient
{
    public async Task<IProviderRemoteViewConnection> ConnectAsync(
        string baseUrl, string? apiKey, string providerJobId, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        try
        {
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                // Browsers cannot send custom headers on a WebSocket handshake,
                // but this is a server-to-server ClientWebSocket connection, so
                // the same Bearer-apiKey convention every other provider call
                // uses (ExternalProviderClient.CreateClient) applies here too.
                socket.Options.SetRequestHeader("Authorization", $"Bearer {apiKey}");
            }

            var uri = BuildViewUri(baseUrl, providerJobId);
            await socket.ConnectAsync(uri, cancellationToken);
            return new ClientWebSocketConnection(socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static Uri BuildViewUri(string baseUrl, string providerJobId)
    {
        var trimmed = baseUrl.TrimEnd('/');
        var wsBase = trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? "wss://" + trimmed["https://".Length..]
            : trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                ? "ws://" + trimmed["http://".Length..]
                : throw new InvalidOperationException($"Provider base URL '{baseUrl}' is not http(s).");

        return new Uri($"{wsBase}/acquire/{Uri.EscapeDataString(providerJobId)}/interaction/view");
    }

    private sealed class ClientWebSocketConnection(ClientWebSocket socket) : IProviderRemoteViewConnection
    {
        public WebSocketState State => socket.State;

        public Task SendAsync(ReadOnlyMemory<byte> buffer, bool endOfMessage, CancellationToken cancellationToken) =>
            socket.SendAsync(buffer, WebSocketMessageType.Binary, endOfMessage, cancellationToken).AsTask();

        public Task<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
            socket.ReceiveAsync(buffer, cancellationToken).AsTask();

        public Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken cancellationToken) =>
            socket.CloseAsync(status, description, cancellationToken);

        public ValueTask DisposeAsync()
        {
            socket.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
