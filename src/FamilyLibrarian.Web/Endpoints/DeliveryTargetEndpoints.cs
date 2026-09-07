using FamilyLibrarian.Application.Delivery;
using FamilyLibrarian.Contracts.Delivery;
using FamilyLibrarian.Domain.Delivery;

namespace FamilyLibrarian.Web.Endpoints;

/// <summary>
/// A user's own Kindle delivery settings -- ownership is enforced inside
/// <see cref="DeliveryTargetService"/>, the same pattern as
/// <c>FeedbackEndpoints</c>.
/// </summary>
internal static class DeliveryTargetEndpoints
{
    public static void MapDeliveryTargetEndpoints(this IEndpointRouteBuilder app)
    {
        var kindle = app.MapGroup("/api/v1/me/delivery/kindle")
            .RequireAuthorization()
            .AddEndpointFilter<AntiforgeryEndpointFilter>();

        kindle.MapGet("/", GetMyKindleTargetAsync);
        kindle.MapPut("/", SetMyKindleAddressAsync);
        kindle.MapPut("/enabled", SetMyKindleEnabledAsync);
        kindle.MapPost("/test", TestMyKindleDeliveryAsync);
    }

    private static async Task<IResult> GetMyKindleTargetAsync(
        DeliveryTargetService service,
        CancellationToken cancellationToken)
    {
        var target = await service.GetMyKindleTargetAsync(cancellationToken);
        return target is null ? Results.NotFound() : Results.Ok(ToResponse(target));
    }

    private static async Task<IResult> SetMyKindleAddressAsync(
        SetKindleAddressRequest request,
        DeliveryTargetService service,
        CancellationToken cancellationToken)
    {
        var result = await service.SetMyKindleAddressAsync(request.Address, request.ExpectedVersion, cancellationToken);

        return result.Outcome switch
        {
            SetKindleTargetOutcome.Success => Results.Ok(ToResponse(result.Target!)),
            SetKindleTargetOutcome.Unauthenticated => Results.Unauthorized(),
            SetKindleTargetOutcome.Conflict => Results.Conflict(new { message = result.Error }),
            _ => Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["address"] = [result.Error ?? "That address could not be saved."]
            })
        };
    }

    private static async Task<IResult> SetMyKindleEnabledAsync(
        SetKindleEnabledRequest request,
        DeliveryTargetService service,
        CancellationToken cancellationToken)
    {
        var result = await service.SetMyKindleEnabledAsync(request.Enabled, request.ExpectedVersion, cancellationToken);

        return result.Outcome switch
        {
            SetKindleTargetOutcome.Success => Results.Ok(ToResponse(result.Target!)),
            SetKindleTargetOutcome.NotFound => Results.NotFound(),
            SetKindleTargetOutcome.Unauthenticated => Results.Unauthorized(),
            SetKindleTargetOutcome.Conflict => Results.Conflict(new { message = result.Error }),
            _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError)
        };
    }

    private static async Task<IResult> TestMyKindleDeliveryAsync(
        DeliveryTargetService service,
        CancellationToken cancellationToken)
    {
        var result = await service.TestKindleDeliveryAsync(cancellationToken);
        return Results.Ok(new TestKindleDeliveryResponse(result.Succeeded, result.Message));
    }

    private static DeliveryTargetResponse ToResponse(DeliveryTarget target) => new(
        target.Id,
        target.Provider.ToString(),
        target.Name,
        target.Address,
        target.IsEnabled,
        target.Version);
}
