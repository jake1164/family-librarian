using FamilyLibrarian.Contracts.Operations;
using FamilyLibrarian.Web.System;

namespace FamilyLibrarian.Web.Endpoints;

/// <summary>The plain healthy/degraded signal behind the status footer every signed-in user sees.</summary>
internal static class SystemReadinessEndpoints
{
    public static void MapSystemReadinessEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/system/readiness", GetReadinessAsync)
            .RequireAuthorization();
    }

    private static async Task<IResult> GetReadinessAsync(
        SystemReadinessService readiness, CancellationToken cancellationToken) =>
        Results.Ok(new SystemReadinessResponse(await readiness.IsHealthyAsync(cancellationToken)));
}
