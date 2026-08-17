using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Contracts.Providers;
using FamilyLibrarian.Domain.Providers;

namespace FamilyLibrarian.Web.Endpoints;

internal static class ProviderCatalogEndpoints
{
    public static void MapProviderCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var adminProviderCatalogs = app.MapGroup("/api/v1/admin/provider-catalogs")
            .RequireAuthorization("Admin")
            .AddEndpointFilter<AntiforgeryEndpointFilter>();

        adminProviderCatalogs.MapGet("/", ListProviderCatalogsAsync);
        adminProviderCatalogs.MapPost("/", AddProviderCatalogAsync);
        adminProviderCatalogs.MapPut("/{id:guid}/enabled", SetProviderCatalogEnabledAsync);
        adminProviderCatalogs.MapPost("/{id:guid}/refresh", RefreshProviderCatalogAsync);
        adminProviderCatalogs.MapDelete("/{id:guid}", RemoveProviderCatalogAsync);
    }

    private static async Task<IResult> ListProviderCatalogsAsync(
        ProviderCatalogService service, CancellationToken cancellationToken) =>
        Results.Ok((await service.ListAsync(cancellationToken)).Select(ToProviderCatalogResponse).ToArray());

    private static async Task<IResult> AddProviderCatalogAsync(
        AddProviderCatalogRequest request, ProviderCatalogService service, CancellationToken cancellationToken)
    {
        var result = await service.AddAsync(request.Url, request.DisplayName, cancellationToken);
        return result.Succeeded
            ? Results.Ok()
            : Results.ValidationProblem(new Dictionary<string, string[]> { ["catalog"] = [result.Error!] });
    }

    private static async Task<IResult> SetProviderCatalogEnabledAsync(
        Guid id, SetProviderCatalogEnabledRequest request, ProviderCatalogService service, CancellationToken cancellationToken)
    {
        var result = await service.SetEnabledAsync(id, request.Enabled, cancellationToken);
        return result.Succeeded
            ? Results.Ok()
            : Results.ValidationProblem(new Dictionary<string, string[]> { ["catalog"] = [result.Error!] });
    }

    private static async Task<IResult> RefreshProviderCatalogAsync(
        Guid id, ProviderCatalogService service, CancellationToken cancellationToken)
    {
        var result = await service.RefreshAsync(id, cancellationToken);
        return result.Succeeded
            ? Results.Ok()
            : Results.ValidationProblem(new Dictionary<string, string[]> { ["catalog"] = [result.Error!] });
    }

    private static async Task<IResult> RemoveProviderCatalogAsync(
        Guid id, ProviderCatalogService service, CancellationToken cancellationToken)
    {
        var result = await service.RemoveAsync(id, cancellationToken);
        return result.Succeeded
            ? Results.NoContent()
            : Results.ValidationProblem(new Dictionary<string, string[]> { ["catalog"] = [result.Error!] });
    }

    private static ProviderCatalogResponse ToProviderCatalogResponse(ProviderCatalog catalog) => new(
        catalog.Id,
        catalog.Url,
        catalog.DisplayName,
        catalog.IsEnabled,
        ProviderCatalogEntryParser.Parse(catalog.CachedEntriesJson)
            .Select(entry => new ProviderCatalogEntryResponse(
                entry.Id, entry.Name, entry.ProtocolVersion, entry.Capabilities, entry.License, entry.Publisher,
                entry.TrustLabel, entry.OciImageDigest, entry.HomepageUrl, entry.Description))
            .ToArray(),
        catalog.LastFetchedAtUtc,
        catalog.LastFetchSucceeded,
        catalog.LastFetchMessage);
}
