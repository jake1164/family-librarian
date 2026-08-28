using FamilyLibrarian.Application.Catalog;

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
        IGutenbergCatalog catalog,
        IGutenbergCatalogSynchronizer synchronizer,
        CancellationToken cancellationToken)
    {
        var currentStatus = await catalog.GetStatusAsync(cancellationToken);
        if (currentStatus.Status is "CheckingUpdates" or "Downloading" or "Parsing" or "Importing" or "Retrying")
        {
            return Results.Conflict(new
            {
                detail = "The Project Gutenberg catalogue is already being refreshed."
            });
        }

        var result = currentStatus.IsReady
            ? await synchronizer.SynchronizeIncrementalAsync(cancellationToken)
            : await synchronizer.SynchronizeAsync(cancellationToken);
        return result.Succeeded ? Results.Ok(result.Status) : Results.Problem(
            title: "Project Gutenberg catalogue synchronization failed.",
            detail: result.Error,
            statusCode: StatusCodes.Status502BadGateway);
    }

    private static async Task<IResult> PurgeAsync(
        IGutenbergCatalog catalog,
        IGutenbergCatalogMaintenance maintenance,
        CancellationToken cancellationToken)
    {
        var currentStatus = await catalog.GetStatusAsync(cancellationToken);
        if (currentStatus.Status is "CheckingUpdates" or "Downloading" or "Parsing" or "Importing" or "Retrying")
        {
            return Results.Conflict(new
            {
                detail = "The Project Gutenberg catalogue cannot be deleted while a refresh is in progress."
            });
        }

        return Results.Ok(await maintenance.PurgeAsync(cancellationToken));
    }
}
