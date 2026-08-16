namespace FamilyLibrarian.Application.Providers;

/// <summary>
/// The gateway state <see cref="PrivateEgressRouteResolver"/> reads, held in
/// memory so resolving a route never blocks on a database call — same reason
/// <c>IOidcRuntimeSettingsCache</c> exists.
/// </summary>
public interface IPrivateEgressGatewayRuntimeCache
{
    PrivateEgressGatewayRuntimeState Current { get; }

    void Refresh(PrivateEgressGatewayRuntimeState state);
}

public sealed record PrivateEgressGatewayRuntimeState(bool IsEnabled, string? GatewayEndpoint, bool LastTestSucceeded)
{
    public static readonly PrivateEgressGatewayRuntimeState Disabled = new(false, null, false);

    /// <summary>Enabled, has an endpoint, and that endpoint's last test succeeded — no live probe per call.</summary>
    public bool IsUsable => IsEnabled && !string.IsNullOrWhiteSpace(GatewayEndpoint) && LastTestSucceeded;
}
