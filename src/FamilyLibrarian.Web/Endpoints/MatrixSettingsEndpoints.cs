using FamilyLibrarian.Application.Communications;
using FamilyLibrarian.Contracts.Communications;

namespace FamilyLibrarian.Web.Endpoints;

/// <summary>Admin-only configuration and probe routes for outbound Matrix (COMM-1 §B).</summary>
internal static class MatrixSettingsEndpoints
{
    public static void MapMatrixSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var matrix = app.MapGroup("/api/v1/admin/communications/matrix")
            .RequireAuthorization("Admin")
            .AddEndpointFilter<AntiforgeryEndpointFilter>();

        matrix.MapGet("/", GetAsync);
        matrix.MapPut("/", SetSettingsAsync);
        matrix.MapPut("/enabled", SetEnabledAsync);
        matrix.MapPut("/access-token", SetAccessTokenAsync);
        matrix.MapDelete("/access-token", ClearAccessTokenAsync);
        matrix.MapPost("/test", SendTestAsync);
    }

    private static async Task<IResult> GetAsync(MatrixSettingsService service, CancellationToken cancellationToken) =>
        Results.Ok(ToResponse(await service.GetStatusAsync(cancellationToken)));

    private static async Task<IResult> SetSettingsAsync(
        SetMatrixSettingsRequest request, MatrixSettingsService service, CancellationToken cancellationToken) =>
        ToResult(await service.SetSettingsAsync(request.HomeserverUrl, request.BotUserId, cancellationToken));

    private static async Task<IResult> SetEnabledAsync(
        SetMatrixEnabledRequest request, MatrixSettingsService service, CancellationToken cancellationToken) =>
        ToResult(await service.SetEnabledAsync(request.Enabled, cancellationToken));

    private static async Task<IResult> SetAccessTokenAsync(
        SetMatrixAccessTokenRequest request, MatrixSettingsService service, CancellationToken cancellationToken) =>
        ToResult(await service.SetAccessTokenAsync(request.AccessToken, cancellationToken));

    private static async Task<IResult> ClearAccessTokenAsync(
        MatrixSettingsService service, CancellationToken cancellationToken) =>
        ToResult(await service.ClearAccessTokenAsync(cancellationToken));

    private static async Task<IResult> SendTestAsync(
        SendMatrixTestRequest request, MatrixSettingsService service, CancellationToken cancellationToken)
    {
        var result = await service.SendTestAsync(request.HomeserverUrl, request.BotUserId, request.AccessToken, cancellationToken);
        return result.Succeeded
            ? Results.Ok(new MatrixTestResponse(result.Outcome!.Succeeded, result.Outcome.Message))
            : Invalid("matrix", result.Error!);
    }

    private static IResult ToResult(MatrixCommandResult result) => result.Succeeded
        ? Results.Ok(ToResponse(result.Status!))
        : Invalid("matrix", result.Error!);

    private static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    private static MatrixSettingsResponse ToResponse(MatrixStatus status) => new(
        status.IsEnabled,
        status.HomeserverUrl,
        status.BotUserId,
        status.HasAccessToken,
        status.AccessTokenSetAtUtc,
        status.LastTestedAtUtc,
        status.LastTestSucceeded,
        status.LastTestMessage);
}
