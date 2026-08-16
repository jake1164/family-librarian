using FamilyLibrarian.Domain.Acquisition;

namespace FamilyLibrarian.Application.Providers;

/// <summary>
/// The one place an external-provider egress policy turns into an actual
/// routing decision. <see cref="EgressPolicy.Normal"/> is always allowed
/// direct; <see cref="EgressPolicy.PrivateRequired"/>/<see cref="EgressPolicy.CustomProxy"/>
/// require a configured, tested gateway — otherwise the operation is
/// <see cref="EgressResolution.Blocked"/>. There is deliberately no
/// automatic fallback to direct egress here: that is the entire point of a
/// provider declaring it needs private routing.
/// </summary>
public sealed class PrivateEgressRouteResolver(IPrivateEgressGatewayRuntimeCache gatewayCache)
{
    public EgressResolution Resolve(EgressPolicy policy)
    {
        if (policy == EgressPolicy.Normal)
        {
            return EgressResolution.Allowed(EgressRoute.Direct);
        }

        var gateway = gatewayCache.Current;
        if (!gateway.IsUsable)
        {
            return EgressResolution.Blocked(
                "This provider requires the private acquisition gateway, but it is not configured or has not " +
                "passed a connection test yet.");
        }

        return EgressResolution.Allowed(EgressRoute.ViaGateway(new Uri(gateway.GatewayEndpoint!)));
    }
}

public sealed record EgressResolution(bool IsAllowed, EgressRoute? Route, string? BlockedReason)
{
    public static EgressResolution Allowed(EgressRoute route) => new(true, route, null);

    public static EgressResolution Blocked(string reason) => new(false, null, reason);
}
