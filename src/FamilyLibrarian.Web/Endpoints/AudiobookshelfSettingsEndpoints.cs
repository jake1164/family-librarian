using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Contracts.Publishing;

namespace FamilyLibrarian.Web.Endpoints;

/// <summary>
/// Audiobookshelf is the audiobook delivery destination, reached over its upload
/// API rather than an ingest folder — hence its own settings routes alongside
/// <see cref="CwaSettingsEndpoints"/> rather than a shared shape.
/// </summary>
internal static class AudiobookshelfSettingsEndpoints
{
    public static void MapAudiobookshelfSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var audiobookshelfSettings = app.MapGroup("/api/v1/admin/publishing/audiobookshelf")
            .RequireAuthorization("Admin")
            .AddEndpointFilter<AntiforgeryEndpointFilter>();

        audiobookshelfSettings.MapGet("/", GetAudiobookshelfSettingsAsync);
        audiobookshelfSettings.MapPut("/", SetAudiobookshelfSettingsAsync);
        audiobookshelfSettings.MapPut("/enabled", SetAudiobookshelfEnabledAsync);
        audiobookshelfSettings.MapPut("/api-token", SetAudiobookshelfApiTokenAsync);
        audiobookshelfSettings.MapDelete("/api-token", ClearAudiobookshelfApiTokenAsync);
        audiobookshelfSettings.MapPost("/test", TestAudiobookshelfConnectionAsync);
    }

    private static async Task<IResult> GetAudiobookshelfSettingsAsync(
        AudiobookshelfSettingsService service, CancellationToken cancellationToken) =>
        Results.Ok(ToAudiobookshelfResponse(await service.GetStatusAsync(cancellationToken)));

    private static async Task<IResult> SetAudiobookshelfSettingsAsync(
        SetAudiobookshelfSettingsRequest request,
        AudiobookshelfSettingsService service,
        CancellationToken cancellationToken) =>
        ToAudiobookshelfResult(
            await service.SetSettingsAsync(request.BaseUrl, request.LibraryId, request.FolderId, cancellationToken));

    private static async Task<IResult> SetAudiobookshelfEnabledAsync(
        SetPublishingEnabledRequest request,
        AudiobookshelfSettingsService service,
        CancellationToken cancellationToken) =>
        ToAudiobookshelfResult(await service.SetEnabledAsync(request.Enabled, cancellationToken));

    private static async Task<IResult> SetAudiobookshelfApiTokenAsync(
        SetPublishingSecretRequest request,
        AudiobookshelfSettingsService service,
        CancellationToken cancellationToken) =>
        ToAudiobookshelfResult(await service.SetApiTokenAsync(request.Value, cancellationToken));

    private static async Task<IResult> ClearAudiobookshelfApiTokenAsync(
        AudiobookshelfSettingsService service, CancellationToken cancellationToken) =>
        ToAudiobookshelfResult(await service.ClearApiTokenAsync(cancellationToken));

    private static async Task<IResult> TestAudiobookshelfConnectionAsync(
        AudiobookshelfSettingsService service, CancellationToken cancellationToken)
    {
        var result = await service.TestConnectionAsync(cancellationToken);
        return Results.Ok(new PublishingConnectionTestResponse(
            result.Status!.LastTestSucceeded ?? false, result.Status.LastTestMessage ?? string.Empty));
    }

    private static IResult ToAudiobookshelfResult(AudiobookshelfCommandResult result) => result.Outcome switch
    {
        PublishingCommandOutcome.Invalid => Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["audiobookshelf"] = [result.Error ?? "That change is not allowed."]
        }),
        _ => Results.Ok(ToAudiobookshelfResponse(result.Status!))
    };

    private static AudiobookshelfSettingsResponse ToAudiobookshelfResponse(AudiobookshelfStatus status) => new(
        status.IsEnabled,
        status.BaseUrl,
        status.LibraryId,
        status.FolderId,
        status.HasApiToken,
        status.ApiTokenHint,
        status.ApiTokenSetAtUtc,
        status.LastTestedAtUtc,
        status.LastTestSucceeded,
        status.LastTestMessage);
}
