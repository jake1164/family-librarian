using FamilyLibrarian.Application.Upgrades;
using FamilyLibrarian.Contracts.Upgrades;

namespace FamilyLibrarian.Web.Endpoints;

/// <summary>Administrator view of better copies found for delivered books, and setting one aside.</summary>
internal static class UpgradeEndpoints
{
    public static void MapUpgradeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/admin/upgrades").RequireAuthorization("Admin").AddEndpointFilter<AntiforgeryEndpointFilter>();
        group.MapGet("/", ListAsync);
        group.MapPost("/{id:guid}/dismiss", DismissAsync);
        group.MapPost("/{id:guid}/approve", ApproveAsync);
    }

    private static async Task<IResult> ListAsync(LibraryUpgradeAdminService service, CancellationToken cancellationToken)
    {
        var (forbidden, upgrades) = await service.ListAsync(cancellationToken);
        return forbidden ? Results.StatusCode(StatusCodes.Status403Forbidden) : Results.Ok(upgrades.Select(upgrade => new LibraryUpgradeResponse(
            upgrade.Id, upgrade.RequestId, upgrade.State.ToString(), [.. upgrade.Reasons.Split(',', StringSplitOptions.RemoveEmptyEntries)],
            upgrade.ProviderId, upgrade.FailureReason, upgrade.CreatedAtUtc, upgrade.UpdatedAtUtc)));
    }

    private static async Task<IResult> ApproveAsync(Guid id, LibraryUpgradeAdminService service, CancellationToken cancellationToken)
    {
        var result = await service.ApproveAsync(id, cancellationToken);
        return result switch
        {
            { Succeeded: true } => Results.Accepted(),
            { Forbidden: true } => Results.StatusCode(StatusCodes.Status403Forbidden),
            { NotFound: true } => Results.NotFound(),
            _ => Results.BadRequest(new { error = result.Error })
        };
    }

    private static async Task<IResult> DismissAsync(Guid id, LibraryUpgradeAdminService service, CancellationToken cancellationToken)
    {
        var result = await service.DismissAsync(id, cancellationToken);
        return result switch
        {
            { Succeeded: true } => Results.NoContent(),
            { Forbidden: true } => Results.StatusCode(StatusCodes.Status403Forbidden),
            { NotFound: true } => Results.NotFound(),
            _ => Results.BadRequest(new { error = result.Error })
        };
    }
}
