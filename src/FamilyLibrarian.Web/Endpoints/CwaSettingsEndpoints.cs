using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Contracts.Publishing;
using FamilyLibrarian.Domain.Publishing;

namespace FamilyLibrarian.Web.Endpoints;

/// <summary>
/// CWA (Calibre-Web Automated) is the ebook publishing destination. It is not a
/// metadata provider, so it gets its own settings routes rather than stretching
/// the provider-registry shape.
/// </summary>
internal static class CwaSettingsEndpoints
{
    public static void MapCwaSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var cwaSettings = app.MapGroup("/api/v1/admin/publishing/cwa")
            .RequireAuthorization("Admin")
            .AddEndpointFilter<AntiforgeryEndpointFilter>();

        cwaSettings.MapGet("/", GetCwaSettingsAsync);
        cwaSettings.MapPut("/", SetCwaSettingsAsync);
        cwaSettings.MapPut("/enabled", SetCwaEnabledAsync);
        cwaSettings.MapPut("/sftp-key", SetCwaSftpPrivateKeyAsync);
        cwaSettings.MapDelete("/sftp-key", ClearCwaSftpPrivateKeyAsync);
        cwaSettings.MapPut("/sftp-passphrase", SetCwaSftpPassphraseAsync);
        cwaSettings.MapDelete("/sftp-passphrase", ClearCwaSftpPassphraseAsync);
        cwaSettings.MapPut("/opds-password", SetCwaOpdsPasswordAsync);
        cwaSettings.MapDelete("/opds-password", ClearCwaOpdsPasswordAsync);
        cwaSettings.MapPost("/test", TestCwaConnectionAsync);
    }

    private static async Task<IResult> GetCwaSettingsAsync(
        CwaSettingsService service, CancellationToken cancellationToken) =>
        Results.Ok(ToCwaResponse(await service.GetStatusAsync(cancellationToken)));

    private static async Task<IResult> SetCwaSettingsAsync(
        SetCwaSettingsRequest request, CwaSettingsService service, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<CwaTransportMode>(request.TransportMode, ignoreCase: true, out var mode) ||
            !Enum.IsDefined(mode))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["transportMode"] = ["That is not a valid transport mode."]
            });
        }

        return ToCwaResult(await service.SetSettingsAsync(
            mode, request.LocalIngestPath, request.SftpHost, request.SftpPort, request.SftpUsername,
            request.SftpIngestPath, request.OpdsBaseUrl, request.OpdsUsername, cancellationToken));
    }

    private static async Task<IResult> SetCwaEnabledAsync(
        SetPublishingEnabledRequest request, CwaSettingsService service, CancellationToken cancellationToken) =>
        ToCwaResult(await service.SetEnabledAsync(request.Enabled, cancellationToken));

    private static async Task<IResult> SetCwaSftpPrivateKeyAsync(
        SetPublishingSecretRequest request, CwaSettingsService service, CancellationToken cancellationToken) =>
        ToCwaResult(await service.SetSftpPrivateKeyAsync(request.Value, cancellationToken));

    private static async Task<IResult> ClearCwaSftpPrivateKeyAsync(
        CwaSettingsService service, CancellationToken cancellationToken) =>
        ToCwaResult(await service.ClearSftpPrivateKeyAsync(cancellationToken));

    private static async Task<IResult> SetCwaSftpPassphraseAsync(
        SetPublishingSecretRequest request, CwaSettingsService service, CancellationToken cancellationToken) =>
        ToCwaResult(await service.SetSftpPassphraseAsync(request.Value, cancellationToken));

    private static async Task<IResult> ClearCwaSftpPassphraseAsync(
        CwaSettingsService service, CancellationToken cancellationToken) =>
        ToCwaResult(await service.ClearSftpPassphraseAsync(cancellationToken));

    private static async Task<IResult> SetCwaOpdsPasswordAsync(
        SetPublishingSecretRequest request, CwaSettingsService service, CancellationToken cancellationToken) =>
        ToCwaResult(await service.SetOpdsPasswordAsync(request.Value, cancellationToken));

    private static async Task<IResult> ClearCwaOpdsPasswordAsync(
        CwaSettingsService service, CancellationToken cancellationToken) =>
        ToCwaResult(await service.ClearOpdsPasswordAsync(cancellationToken));

    private static async Task<IResult> TestCwaConnectionAsync(
        CwaSettingsService service, CancellationToken cancellationToken)
    {
        var result = await service.TestConnectionAsync(cancellationToken);
        return Results.Ok(new PublishingConnectionTestResponse(
            result.Status!.LastTestSucceeded ?? false, result.Status.LastTestMessage ?? string.Empty));
    }

    private static IResult ToCwaResult(CwaCommandResult result) => result.Outcome switch
    {
        PublishingCommandOutcome.Invalid => Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["cwa"] = [result.Error ?? "That change is not allowed."]
        }),
        _ => Results.Ok(ToCwaResponse(result.Status!))
    };

    private static CwaSettingsResponse ToCwaResponse(CwaStatus status) => new(
        status.IsEnabled,
        status.TransportMode.ToString(),
        status.LocalIngestPath,
        status.SftpHost,
        status.SftpPort,
        status.SftpUsername,
        status.SftpIngestPath,
        status.HasSftpPrivateKey,
        status.SftpPrivateKeyHint,
        status.SftpPrivateKeySetAtUtc,
        status.HasSftpPassphrase,
        status.SftpPassphraseHint,
        status.SftpPassphraseSetAtUtc,
        status.OpdsBaseUrl,
        status.OpdsUsername,
        status.HasOpdsPassword,
        status.OpdsPasswordHint,
        status.OpdsPasswordSetAtUtc,
        status.LastTestedAtUtc,
        status.LastTestSucceeded,
        status.LastTestMessage);
}
