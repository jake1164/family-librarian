using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Contracts.Requests;

namespace FamilyLibrarian.Web.Endpoints;

internal static class AcquisitionPreferenceEndpoints
{
    public static void MapAcquisitionPreferenceEndpoints(this IEndpointRouteBuilder app)
    {
        var preferences = app.MapGroup("/api/v1/me/acquisition-preferences").RequireAuthorization()
            .AddEndpointFilter<AntiforgeryEndpointFilter>();
        preferences.MapGet("/", ListAsync);
        preferences.MapDelete("/{preferenceId:guid}", RemoveAsync);
    }

    private static async Task<IResult> ListAsync(AcquisitionPreferenceService preferences, CancellationToken cancellationToken)
    {
        var rows = await preferences.ListOwnAsync(cancellationToken);
        return rows is null ? Results.Unauthorized() : Results.Ok(rows.Select(p => new AcquisitionPreferenceResponse(p.Id, p.Kind, p.Value, p.CreatedAtUtc)));
    }

    private static async Task<IResult> RemoveAsync(Guid preferenceId, AcquisitionPreferenceService preferences, CancellationToken cancellationToken) =>
        await preferences.RemoveOwnAsync(preferenceId, cancellationToken) switch
        {
            PreferenceRemovalOutcome.Removed => Results.NoContent(),
            PreferenceRemovalOutcome.Unauthenticated => Results.Unauthorized(),
            _ => Results.NotFound()
        };
}
