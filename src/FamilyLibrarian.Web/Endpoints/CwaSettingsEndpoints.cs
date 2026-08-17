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
        cwaSettings.MapPut("/sftp-password", SetCwaSftpPasswordAsync);
        cwaSettings.MapDelete("/sftp-password", ClearCwaSftpPasswordAsync);
        cwaSettings.MapPut("/sftp-host-key", TrustCwaSftpHostKeyAsync);
        cwaSettings.MapPut("/opds-password", SetCwaOpdsPasswordAsync);
        cwaSettings.MapDelete("/opds-password", ClearCwaOpdsPasswordAsync);
        cwaSettings.MapPost("/test", TestAllCwaConnectionsAsync);
        cwaSettings.MapPost("/test-ingest", TestCwaIngestConnectionAsync);
        cwaSettings.MapPost("/test-opds", TestCwaOpdsConnectionAsync);
    }

    private static async Task<IResult> GetCwaSettingsAsync(
        CwaSettingsService service, CancellationToken cancellationToken) =>
        Results.Ok(ToCwaResponse(await service.GetStatusAsync(cancellationToken)));

    private static async Task<IResult> SetCwaSettingsAsync(
        SetCwaSettingsRequest request, CwaSettingsService service, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<CwaTransportMode>(request.TransportMode, ignoreCase: true, out var mode) ||
            !Enum.IsDefined(mode) ||
            !Enum.TryParse<CwaSftpAuthenticationMode>(
                request.SftpAuthenticationMode, ignoreCase: true, out var authenticationMode) ||
            !Enum.IsDefined(authenticationMode))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["cwa"] = ["The transport or SFTP authentication mode is not valid."]
            });
        }

        return ToCwaResult(await service.SetSettingsAsync(
            mode, request.LocalIngestPath, request.SftpHost, request.SftpPort, request.SftpUsername,
            request.SftpIngestPath, authenticationMode, request.OpdsBaseUrl, request.OpdsUsername, cancellationToken));
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

    private static async Task<IResult> SetCwaSftpPasswordAsync(
        SetPublishingSecretRequest request, CwaSettingsService service, CancellationToken cancellationToken) =>
        ToCwaResult(await service.SetSftpPasswordAsync(request.Value, cancellationToken));

    private static async Task<IResult> ClearCwaSftpPasswordAsync(
        CwaSettingsService service, CancellationToken cancellationToken) =>
        ToCwaResult(await service.ClearSftpPasswordAsync(cancellationToken));

    private static async Task<IResult> TrustCwaSftpHostKeyAsync(
        TrustSftpHostKeyRequest request, CwaSettingsService service, CancellationToken cancellationToken) =>
        ToCwaResult(await service.TrustSftpHostKeyAsync(request.Fingerprint, cancellationToken));

    private static async Task<IResult> SetCwaOpdsPasswordAsync(
        SetPublishingSecretRequest request, CwaSettingsService service, CancellationToken cancellationToken) =>
        ToCwaResult(await service.SetOpdsPasswordAsync(request.Value, cancellationToken));

    private static async Task<IResult> ClearCwaOpdsPasswordAsync(
        CwaSettingsService service, CancellationToken cancellationToken) =>
        ToCwaResult(await service.ClearOpdsPasswordAsync(cancellationToken));

    private static Task<IResult> TestAllCwaConnectionsAsync(
        CwaSettingsService service, CancellationToken cancellationToken) =>
        TestCwaConnectionAsync(CwaConnectionTestTarget.All, service, cancellationToken);

    private static async Task<IResult> TestCwaIngestConnectionAsync(
        TestCwaIngestRequest request,
        CwaSettingsService service,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<CwaTransportMode>(request.TransportMode, ignoreCase: true, out var mode) ||
            !Enum.IsDefined(mode) ||
            !Enum.TryParse<CwaSftpAuthenticationMode>(
                request.SftpAuthenticationMode, ignoreCase: true, out var authenticationMode) ||
            !Enum.IsDefined(authenticationMode))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["cwa"] = ["The transport or SFTP authentication mode is not valid."]
            });
        }

        var result = await service.TestIngestConnectionAsync(
            new CwaIngestTestConfiguration(
                mode,
                request.LocalIngestPath,
                request.SftpHost,
                request.SftpPort,
                request.SftpUsername,
                request.SftpIngestPath,
                authenticationMode,
                request.SftpPrivateKey,
                request.SftpPassphrase,
                request.SftpPassword,
                request.TrustedSftpHostKeyFingerprint),
            cancellationToken);

        return Results.Ok(new PublishingConnectionTestResponse(
            result.Succeeded,
            result.Message,
            result.RequiresSftpHostKeyTrust,
            result.SftpHostKeyFingerprint));
    }

    private static async Task<IResult> TestCwaOpdsConnectionAsync(
        TestCwaOpdsRequest request,
        CwaSettingsService service,
        CancellationToken cancellationToken)
    {
        var result = await service.TestOpdsConnectionAsync(
            new CwaOpdsTestConfiguration(request.OpdsBaseUrl, request.OpdsUsername, request.OpdsPassword),
            cancellationToken);
        return Results.Ok(new PublishingConnectionTestResponse(result.Succeeded, result.Message));
    }

    private static async Task<IResult> TestCwaConnectionAsync(
        CwaConnectionTestTarget target,
        CwaSettingsService service,
        CancellationToken cancellationToken)
    {
        var result = await service.TestConnectionAsync(target, cancellationToken);
        return Results.Ok(new PublishingConnectionTestResponse(
            result.Outcome.Succeeded,
            result.Outcome.Message,
            result.Outcome.RequiresSftpHostKeyTrust,
            result.Outcome.SftpHostKeyFingerprint));
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
        status.SftpAuthenticationMode.ToString(),
        status.HasSftpPrivateKey,
        status.SftpPrivateKeyHint,
        status.SftpPrivateKeySetAtUtc,
        status.HasSftpPassphrase,
        status.SftpPassphraseHint,
        status.SftpPassphraseSetAtUtc,
        status.HasSftpPassword,
        status.SftpPasswordHint,
        status.SftpPasswordSetAtUtc,
        status.SftpHostKeyFingerprint,
        status.SftpHostKeyTrustedAtUtc,
        status.OpdsBaseUrl,
        status.OpdsUsername,
        status.HasOpdsPassword,
        status.OpdsPasswordHint,
        status.OpdsPasswordSetAtUtc,
        status.LastTestedAtUtc,
        status.LastTestSucceeded,
        status.LastTestMessage);
}
