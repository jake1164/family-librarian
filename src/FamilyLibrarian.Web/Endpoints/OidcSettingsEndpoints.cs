using FamilyLibrarian.Application.Accounts;
using FamilyLibrarian.Contracts.Authentication;

namespace FamilyLibrarian.Web.Endpoints;

/// <summary>
/// OIDC is configured entirely through the admin UI, no environment variables —
/// the same shape as the CWA/Audiobookshelf settings groups.
/// </summary>
internal static class OidcSettingsEndpoints
{
    public static void MapOidcSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var adminOidc = app.MapGroup("/api/v1/admin/authentication/oidc")
            .RequireAuthorization("Admin")
            .AddEndpointFilter<AntiforgeryEndpointFilter>();

        adminOidc.MapGet("/", GetOidcSettingsAsync);
        adminOidc.MapPut("/", SetOidcSettingsAsync);
        adminOidc.MapPut("/enabled", SetOidcEnabledAsync);
        adminOidc.MapPut("/client-secret", SetOidcClientSecretAsync);
        adminOidc.MapDelete("/client-secret", ClearOidcClientSecretAsync);
        adminOidc.MapPost("/test", TestOidcConnectionAsync);
        adminOidc.MapPut("/local-login-disabled", SetOidcLocalLoginDisabledAsync);
    }

    private static async Task<IResult> GetOidcSettingsAsync(
        OidcSettingsService service, CancellationToken cancellationToken) =>
        Results.Ok(ToOidcResponse(await service.GetStatusAsync(cancellationToken)));

    private static async Task<IResult> SetOidcSettingsAsync(
        SetOidcSettingsRequest request, OidcSettingsService service, CancellationToken cancellationToken) =>
        ToOidcResult(await service.SetSettingsAsync(
            request.DisplayName, request.Authority, request.ClientId, request.Scopes, request.MatchClaimName,
            request.AdminClaimName, request.AdminClaimValues, request.AutoCreateAccounts, cancellationToken));

    private static async Task<IResult> SetOidcEnabledAsync(
        SetOidcEnabledRequest request, OidcSettingsService service, CancellationToken cancellationToken) =>
        ToOidcResult(await service.SetEnabledAsync(request.Enabled, cancellationToken));

    private static async Task<IResult> SetOidcClientSecretAsync(
        SetOidcClientSecretRequest request, OidcSettingsService service, CancellationToken cancellationToken) =>
        ToOidcResult(await service.SetClientSecretAsync(request.ClientSecret, cancellationToken));

    private static async Task<IResult> ClearOidcClientSecretAsync(
        OidcSettingsService service, CancellationToken cancellationToken) =>
        ToOidcResult(await service.ClearClientSecretAsync(cancellationToken));

    private static async Task<IResult> SetOidcLocalLoginDisabledAsync(
        SetOidcLocalLoginDisabledRequest request, OidcSettingsService service, CancellationToken cancellationToken) =>
        ToOidcResult(await service.SetLocalLoginDisabledAsync(request.Disabled, cancellationToken));

    private static async Task<IResult> TestOidcConnectionAsync(
        OidcSettingsService service, CancellationToken cancellationToken)
    {
        var result = await service.TestConnectionAsync(cancellationToken);
        return Results.Ok(new OidcConnectionTestResponse(
            result.Status?.LastTestSucceeded ?? false,
            result.Status?.LastTestMessage ?? "The connection test could not be run.",
            result.DiscoveryEndpoints?.AuthorizationEndpoint,
            result.DiscoveryEndpoints?.TokenEndpoint,
            result.DiscoveryEndpoints?.UserinfoEndpoint,
            result.DiscoveryEndpoints?.JwksUri,
            result.DiscoveryEndpoints?.EndSessionEndpoint));
    }

    private static IResult ToOidcResult(OidcCommandResult result) => result.Outcome switch
    {
        OidcCommandOutcome.Invalid => Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["oidc"] = [result.Error ?? "That change is not allowed."]
        }),
        _ => Results.Ok(ToOidcResponse(result.Status!))
    };

    private static OidcSettingsResponse ToOidcResponse(OidcStatus status) => new(
        status.IsEnabled,
        status.DisplayName,
        status.Authority,
        status.ClientId,
        status.HasClientSecret,
        status.ClientSecretHint,
        status.ClientSecretSetAtUtc,
        status.Scopes,
        status.MatchClaimName,
        status.AdminClaimName,
        status.AdminClaimValues,
        status.AutoCreateAccounts,
        status.LocalLoginDisabled,
        status.LastTestedAtUtc,
        status.LastTestSucceeded,
        status.LastTestMessage);
}
