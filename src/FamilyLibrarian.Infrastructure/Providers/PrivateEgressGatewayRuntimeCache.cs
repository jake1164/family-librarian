using FamilyLibrarian.Application.Providers;

namespace FamilyLibrarian.Infrastructure.Providers;

/// <summary>Singleton, lock-free holder for the current gateway state — mirrors <c>OidcRuntimeSettingsCache</c>.</summary>
public sealed class PrivateEgressGatewayRuntimeCache : IPrivateEgressGatewayRuntimeCache
{
    private PrivateEgressGatewayRuntimeState current = PrivateEgressGatewayRuntimeState.Disabled;

    public PrivateEgressGatewayRuntimeState Current => current;

    public void Refresh(PrivateEgressGatewayRuntimeState state) => current = state;
}
