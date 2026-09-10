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
        kindle.MapPost("/send-existing", SendExistingBookAsync);
        kindle.MapGet("/attempts", ListMyAttemptsAsync);
        kindle.MapGet("/attempts/{id:guid}", GetMyAttemptAsync);
        kindle.MapPost("/attempts/{id:guid}/retry", RetryDeliveryAsync);
        kindle.MapPost("/attempts/{id:guid}/confirm-received", ConfirmReceivedAsync);
        kindle.MapPost("/attempts/{id:guid}/report-missing", ReportMissingAsync);
    }

    private static async Task<IResult> ListMyAttemptsAsync(DeliveryAttemptService service, CancellationToken cancellationToken)
    {
        var attempts = await service.ListMineAsync(cancellationToken);
        return attempts is null ? Results.Unauthorized() : Results.Ok(attempts.Select(ToPersonalResponse).ToArray());
    }

    private static async Task<IResult> GetMyAttemptAsync(Guid id, DeliveryAttemptService service, CancellationToken cancellationToken)
    {
        var attempt = await service.GetMineAsync(id, cancellationToken);
        return attempt is null ? Results.NotFound() : Results.Ok(ToPersonalResponse(attempt));
    }

    private static PersonalDeliveryAttemptResponse ToPersonalResponse(PersonalDeliveryAttemptView view) => new(
        view.Id, view.DeliveryId, view.RequestId, view.BookTitle, view.Status.ToString(),
        view.ConfirmationStatus.ToString(), view.AttemptNumber, view.FailureReason, view.CreatedAtUtc,
        view.CompletedAtUtc, view.ConfirmedAtUtc, view.LatestAttemptId, view.CanRetry,
        view.NextAutomaticRetryAtUtc, view.AutomaticRetriesExhausted);

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
        var result = await service.SetMyKindleAddressAsync(
            request.Address, request.ExpectedVersion, request.SendByDefault, cancellationToken);

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

    private static async Task<IResult> SendExistingBookAsync(
        SendExistingBookRequest request,
        DeliveryAttemptService service,
        CancellationToken cancellationToken)
    {
        var result = await service.SendExistingBookAsync(
            request.WorkId, cancellationToken, request.ConfirmLowConfidenceMatch);

        return result.Outcome switch
        {
            SendExistingBookOutcome.Success => Results.Ok(new SendExistingBookResponse(true, null, result.Attempt!.Id)),
            SendExistingBookOutcome.Failed => Results.Ok(new SendExistingBookResponse(false, result.Error, result.Attempt!.Id)),
            SendExistingBookOutcome.Unauthenticated => Results.Unauthorized(),
            SendExistingBookOutcome.LowConfidenceMatchConfirmationRequired => Results.Conflict(
                new SendExistingBookResponse(false, result.Error, RequiresConfirmation: true)),
            _ => Results.NotFound(new SendExistingBookResponse(false, result.Error))
        };
    }

    private static async Task<IResult> RetryDeliveryAsync(
        Guid id,
        DeliveryAttemptService service,
        CancellationToken cancellationToken,
        bool confirmPossibleDuplicate = false)
    {
        var result = await service.RetryAsync(id, cancellationToken, confirmPossibleDuplicate);

        return result.Outcome switch
        {
            RetryDeliveryOutcome.Success => Results.Ok(new SendExistingBookResponse(true, null)),
            RetryDeliveryOutcome.DuplicateConfirmationRequired => Results.Conflict(
                new { message = "This send may already have been accepted. Confirm that you want to resend despite the possible duplicate." }),
            RetryDeliveryOutcome.NotFound => Results.NotFound(),
            RetryDeliveryOutcome.NotFailed => Results.Conflict(
                new { message = "This delivery isn't in a failed state." }),
            _ => Results.Unauthorized()
        };
    }

    private static async Task<IResult> ConfirmReceivedAsync(
        Guid id,
        DeliveryAttemptService service,
        CancellationToken cancellationToken)
    {
        var result = await service.ConfirmReceivedAsync(id, cancellationToken);
        return ToConfirmationResult(result);
    }

    private static async Task<IResult> ReportMissingAsync(
        Guid id,
        DeliveryAttemptService service,
        CancellationToken cancellationToken)
    {
        var result = await service.ReportMissingAsync(id, cancellationToken);
        return ToConfirmationResult(result);
    }

    private static IResult ToConfirmationResult(ConfirmDeliveryResult result) => result.Outcome switch
    {
        ConfirmDeliveryOutcome.Success => Results.Ok(new SendExistingBookResponse(true, null)),
        ConfirmDeliveryOutcome.NotFound => Results.NotFound(),
        ConfirmDeliveryOutcome.NotSubmitted => Results.Conflict(
            new { message = "This delivery hasn't been sent yet." }),
        _ => Results.Unauthorized()
    };

    private static DeliveryTargetResponse ToResponse(DeliveryTarget target) => new(
        target.Id,
        target.Provider.ToString(),
        target.Name,
        target.Address,
        target.IsEnabled,
        target.SendByDefault,
        target.Version);
}
