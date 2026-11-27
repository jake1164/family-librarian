using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Web.Gutenberg;

namespace FamilyLibrarian.Web.Endpoints;

/// <summary>Administrator diagnostics and recovery controls for the local Gutenberg catalogue.</summary>
internal static class GutenbergCatalogEndpoints
{
    public static IEndpointRouteBuilder MapGutenbergCatalogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/admin/gutenberg")
            .RequireAuthorization("Admin");

        group.MapGet("/status", GetStatusAsync);
        group.MapPost("/sync", SynchronizeAsync)
            .AddEndpointFilter<AntiforgeryEndpointFilter>();
        group.MapDelete("/catalog", PurgeAsync)
            .AddEndpointFilter<AntiforgeryEndpointFilter>();
        return endpoints;
    }

    private static async Task<IResult> GetStatusAsync(
        IGutenbergCatalog catalog,
        CancellationToken cancellationToken) =>
        Results.Ok(await catalog.GetStatusAsync(cancellationToken));

    private static async Task<IResult> SynchronizeAsync(
        GutenbergCatalogRefreshCoordinator coordinator,
        CancellationToken cancellationToken)
    {
        var result = await coordinator.QueueAsync(onlyWhenMissing: false, cancellationToken);
        return result.Accepted
            ? Results.Accepted("/api/v1/admin/gutenberg/status", result.Status)
            : Results.Conflict(new { detail = result.Error });
    }

    private static async Task<IResult> PurgeAsync(
        GutenbergCatalogRefreshCoordinator coordinator,
        CancellationToken cancellationToken)
    {
        var result = await coordinator.TryPurgeAsync(cancellationToken);
        if (result is null)
        {
            return Results.Conflict(new
            {
                detail = "The Project Gutenberg catalogue cannot be deleted while another catalogue operation is in progress."
            });
        }

        // Deletion continues server-side if the administrator navigates away.
        // Its persisted Purging status lets the page resume progress tracking.
        return Results.Ok(result);
    }
}
