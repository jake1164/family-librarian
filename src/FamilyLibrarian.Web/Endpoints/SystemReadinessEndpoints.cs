using FamilyLibrarian.Contracts.Operations;
using FamilyLibrarian.Web.Readiness;

namespace FamilyLibrarian.Web.Endpoints;

/// <summary>The plain healthy/degraded signal behind the status footer every signed-in user sees.</summary>
internal static class SystemReadinessEndpoints
{
    public static void MapSystemReadinessEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/system/readiness", GetReadinessAsync)
            .RequireAuthorization();
    }

    // Every signed-in user gets Healthy; DegradedComponents (which source or
    // publishing destination, and why) stays admin-only, matching
    // SystemReadinessService's own remarks -- a non-admin caller inspecting
    // the response body must not learn more than the footer chip already
    // shows them.
    private static async Task<IResult> GetReadinessAsync(
        HttpContext httpContext, SystemReadinessService readiness, CancellationToken cancellationToken)
    {
        var response = await readiness.GetReadinessAsync(cancellationToken);
        return Results.Ok(httpContext.User.IsInRole("Admin")
            ? response
            : new SystemReadinessResponse(response.Healthy, []));
    }
}
