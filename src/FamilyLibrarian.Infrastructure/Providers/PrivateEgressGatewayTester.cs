using System.Net.Sockets;
using FamilyLibrarian.Application.Providers;

namespace FamilyLibrarian.Infrastructure.Providers;

/// <summary>
/// Confirms the configured gateway endpoint is listening. Deliberately does
/// not try to prove real internet reachability through it — that would mean
/// calling out to some third party this app has no reason to depend on.
/// </summary>
public sealed class PrivateEgressGatewayTester : IPrivateEgressGatewayTester
{
    public async Task<GatewayTestOutcome> TestAsync(string? gatewayEndpoint, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(gatewayEndpoint) ||
            !Uri.TryCreate(gatewayEndpoint, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return GatewayTestOutcome.Failure("Enter a valid http(s) proxy endpoint first.");
        }

        var port = uri.IsDefaultPort ? (uri.Scheme == Uri.UriSchemeHttps ? 443 : 80) : uri.Port;

        try
        {
            using var client = new TcpClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

            await client.ConnectAsync(uri.Host, port, timeoutCts.Token);
            return GatewayTestOutcome.Success($"The gateway at {uri.Host}:{port} is listening.");
        }
        catch (SocketException exception)
        {
            return GatewayTestOutcome.Failure($"The gateway is not reachable: {exception.Message}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return GatewayTestOutcome.Failure("The gateway did not respond in time.");
        }
    }
}
