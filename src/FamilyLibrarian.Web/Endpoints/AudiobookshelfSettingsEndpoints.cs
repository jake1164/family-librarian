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
        audiobookshelfSettings.MapPost("/libraries", DiscoverAudiobookshelfLibrariesAsync);
    }

    private static async Task<IResult> GetAudiobookshelfSettingsAsync(
        AudiobookshelfSettingsService service, CancellationToken cancellationToken) =>
        Results.Ok(ToAudiobookshelfResponse(await service.GetStatusAsync(cancellationToken)));

    private static async Task<IResult> SetAudiobookshelfSettingsAsync(
        SetAudiobookshelfSettingsRequest request,
        AudiobookshelfSettingsService service,
        CancellationToken cancellationToken) =>
        ToAudiobookshelfResult(
            await service.SetSettingsAsync(
                request.BaseUrl, request.PublicUrl, request.LibraryId, request.FolderId, cancellationToken));

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

    /// <summary>
    /// With values, tests what is in the administrator's form without saving it (the result counts only when those are
    /// the saved values). With no body or an empty one, tests and records the saved configuration, as the health check does.
    /// </summary>
    private static async Task<IResult> TestAudiobookshelfConnectionAsync(
        TestAudiobookshelfRequest? request,
        AudiobookshelfSettingsService service,
        CancellationToken cancellationToken)
    {
        var testSaved = request is null || (string.IsNullOrWhiteSpace(request.BaseUrl) &&
            string.IsNullOrWhiteSpace(request.LibraryId) && string.IsNullOrWhiteSpace(request.FolderId) &&
            string.IsNullOrWhiteSpace(request.ApiToken));
        var outcome = testSaved
            ? (await service.TestConnectionAsync(cancellationToken)).Outcome
            : await service.TestConfigurationAsync(
                request!.BaseUrl, request.LibraryId, request.FolderId, request.ApiToken, cancellationToken);
        return Results.Ok(new PublishingConnectionTestResponse(outcome.Succeeded, outcome.Message));
    }

    private static async Task<IResult> DiscoverAudiobookshelfLibrariesAsync(
        DiscoverAudiobookshelfLibrariesRequest request,
        AudiobookshelfSettingsService service,
        CancellationToken cancellationToken)
    {
        var outcome = await service.DiscoverLibrariesAsync(request.BaseUrl, request.ApiToken, cancellationToken);
        return Results.Ok(new AudiobookshelfLibraryDiscoveryResponse(
            outcome.Succeeded,
            outcome.Error,
            outcome.Libraries
                .Select(library => new AudiobookshelfLibraryOption(
                    library.Id,
                    library.Name,
                    library.Folders.Select(folder => new AudiobookshelfFolderOption(folder.Id, folder.Path)).ToList()))
                .ToList()));
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
        status.PublicUrl,
        status.LibraryId,
        status.FolderId,
        status.HasApiToken,
        status.ApiTokenHint,
        status.ApiTokenSetAtUtc,
        status.LastTestedAtUtc,
        status.LastTestSucceeded,
        status.LastTestMessage);
}
