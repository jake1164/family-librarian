using FamilyLibrarian.Application.Accounts;
using FamilyLibrarian.Contracts.Accounts;

namespace FamilyLibrarian.Web.Endpoints;

/// <summary>A household member's own quiet-hours window (HUMAN-ACQ-1 D9) -- self-service, not admin-authorized.</summary>
internal static class QuietHoursEndpoints
{
    public static void MapQuietHoursEndpoints(this IEndpointRouteBuilder app)
    {
        var quietHours = app.MapGroup("/api/v1/me/quiet-hours")
            .RequireAuthorization()
            .AddEndpointFilter<AntiforgeryEndpointFilter>();

        quietHours.MapGet("/", GetAsync);
        quietHours.MapPut("/", SetAsync);
    }

    private static async Task<IResult> GetAsync(QuietHoursPreferenceService service, CancellationToken cancellationToken)
    {
        var current = await service.GetAsync(cancellationToken);
        return Results.Ok(new QuietHoursResponse(current?.TimeZoneId, current?.StartMinute, current?.EndMinute));
    }

    private static async Task<IResult> SetAsync(
        SetQuietHoursRequest request, QuietHoursPreferenceService service, CancellationToken cancellationToken)
    {
        var result = await service.SetAsync(request.TimeZoneId, request.StartMinute, request.EndMinute, cancellationToken);
        return result.Succeeded
            ? Results.Ok(new QuietHoursResponse(request.TimeZoneId, request.StartMinute, request.EndMinute))
            : Results.ValidationProblem(new Dictionary<string, string[]> { ["quietHours"] = [result.Error!] });
    }
}
