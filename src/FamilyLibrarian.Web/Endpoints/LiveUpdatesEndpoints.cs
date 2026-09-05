using FamilyLibrarian.Contracts.Realtime;
using FamilyLibrarian.Web.Realtime;

namespace FamilyLibrarian.Web.Endpoints;

internal static class LiveUpdatesEndpoints
{
    public static void MapLiveUpdatesEndpoints(this IEndpointRouteBuilder app) =>
        app.MapHub<LiveUpdatesHub>(LiveUpdates.HubPath,
                options => options.CloseOnAuthenticationExpiration = true)
            .RequireAuthorization();
}
