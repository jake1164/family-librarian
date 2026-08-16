using FamilyLibrarian.Application.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyLibrarian.Infrastructure.Providers;

public static class GatewayRuntimeCacheInitializer
{
    /// <summary>
    /// Loads whatever gateway settings are already saved into the in-memory
    /// cache <c>PrivateEgressRouteResolver</c> reads from, so the very first
    /// acquisition after a restart sees the last-configured state — same
    /// reason <c>InitializeOidcRuntimeCacheAsync</c> exists.
    /// </summary>
    public static async Task InitializeGatewayRuntimeCacheAsync(
        this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var gatewayService = scope.ServiceProvider.GetRequiredService<PrivateEgressGatewayService>();
        var cache = scope.ServiceProvider.GetRequiredService<IPrivateEgressGatewayRuntimeCache>();

        cache.Refresh(await gatewayService.LoadRuntimeStateAsync(cancellationToken));
    }
}
