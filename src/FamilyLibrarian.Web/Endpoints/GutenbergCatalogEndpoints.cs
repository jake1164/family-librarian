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
        return endpoints;
    }

    private static async Task<IResult> GetStatusAsync(
        IGutenbergCatalog catalog,
        CancellationToken cancellationToken) =>
        Results.Ok(await catalog.GetStatusAsync(cancellationToken));

    private static async Task<IResult> SynchronizeAsync(
        IGutenbergCatalogSynchronizer synchronizer,
        CancellationToken cancellationToken)
    {
        var result = await synchronizer.SynchronizeAsync(cancellationToken);
        return result.Succeeded ? Results.Ok(result.Status) : Results.Problem(
            title: "Project Gutenberg catalogue synchronization failed.",
            detail: result.Error,
            statusCode: StatusCodes.Status502BadGateway);
    }
}
