using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Contracts.Providers;
using FamilyLibrarian.Domain.Acquisition;

namespace FamilyLibrarian.Web.Endpoints;

/// <summary>
/// A registered external provider is an admin-added row, not part of the
/// hardcoded <c>ProviderRegistry</c> allowlist, so it gets its own CRUD surface
/// rather than stretching the metadata-provider routes.
/// </summary>
internal static class ExternalProviderEndpoints
{
    public static void MapExternalProviderEndpoints(this IEndpointRouteBuilder app)
    {
        var adminExternalProviders = app.MapGroup("/api/v1/admin/external-providers")
            .RequireAuthorization("Admin")
            .AddEndpointFilter<AntiforgeryEndpointFilter>();

        adminExternalProviders.MapGet("/", ListExternalProvidersAsync);
        adminExternalProviders.MapPost("/", CreateExternalProviderAsync);
        adminExternalProviders.MapPut("/{id:guid}/details", SetExternalProviderDetailsAsync);
        adminExternalProviders.MapPut("/{id:guid}/enabled", SetExternalProviderEnabledAsync);
        adminExternalProviders.MapPut("/{id:guid}/api-key", SetExternalProviderApiKeyAsync);
        adminExternalProviders.MapDelete("/{id:guid}/api-key", ClearExternalProviderApiKeyAsync);
        adminExternalProviders.MapPost("/{id:guid}/test", TestExternalProviderAsync);
        adminExternalProviders.MapPut("/{id:guid}/egress-policy-override", SetExternalProviderEgressPolicyOverrideAsync);
        adminExternalProviders.MapDelete("/{id:guid}", RemoveExternalProviderAsync);
    }

    private static async Task<IResult> ListExternalProvidersAsync(
        ExternalProviderAdminService service, CancellationToken cancellationToken) =>
        Results.Ok((await service.ListAsync(cancellationToken)).Select(ToExternalProviderResponse).ToArray());

    private static async Task<IResult> CreateExternalProviderAsync(
        CreateExternalProviderRequest request, ExternalProviderAdminService service, CancellationToken cancellationToken) =>
        ToExternalProviderResult(
            await service.CreateAsync(request.ProviderId, request.DisplayName, request.BaseUrl, cancellationToken));

    private static async Task<IResult> SetExternalProviderDetailsAsync(
        Guid id, SetExternalProviderDetailsRequest request, ExternalProviderAdminService service,
        CancellationToken cancellationToken) =>
        ToExternalProviderResult(await service.SetDetailsAsync(id, request.DisplayName, request.BaseUrl, cancellationToken));

    private static async Task<IResult> SetExternalProviderEnabledAsync(
        Guid id, SetExternalProviderEnabledRequest request, ExternalProviderAdminService service,
        CancellationToken cancellationToken) =>
        ToExternalProviderResult(await service.SetEnabledAsync(id, request.Enabled, cancellationToken));

    private static async Task<IResult> SetExternalProviderApiKeyAsync(
        Guid id, SetExternalProviderApiKeyRequest request, ExternalProviderAdminService service,
        CancellationToken cancellationToken) =>
        ToExternalProviderResult(await service.SetApiKeyAsync(id, request.ApiKey, cancellationToken));

    private static async Task<IResult> ClearExternalProviderApiKeyAsync(
        Guid id, ExternalProviderAdminService service, CancellationToken cancellationToken) =>
        ToExternalProviderResult(await service.ClearApiKeyAsync(id, cancellationToken));

    private static async Task<IResult> TestExternalProviderAsync(
        Guid id, ExternalProviderAdminService service, CancellationToken cancellationToken) =>
        ToExternalProviderResult(await service.TestConnectionAsync(id, cancellationToken));

    private static async Task<IResult> SetExternalProviderEgressPolicyOverrideAsync(
        Guid id, SetExternalProviderEgressPolicyOverrideRequest request, ExternalProviderAdminService service,
        CancellationToken cancellationToken)
    {
        EgressPolicy? policy = null;
        if (!string.IsNullOrWhiteSpace(request.EgressPolicy))
        {
            if (!Enum.TryParse<EgressPolicy>(request.EgressPolicy, ignoreCase: true, out var parsed))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["externalProvider"] = ["That is not a known egress policy."]
                });
            }

            policy = parsed;
        }

        return ToExternalProviderResult(await service.SetEgressPolicyOverrideAsync(id, policy, cancellationToken));
    }

    private static async Task<IResult> RemoveExternalProviderAsync(
        Guid id, ExternalProviderAdminService service, CancellationToken cancellationToken)
    {
        var result = await service.RemoveAsync(id, cancellationToken);
        return result.Outcome == ExternalProviderCommandOutcome.Success
            ? Results.NoContent()
            : Results.ValidationProblem(new Dictionary<string, string[]> { ["externalProvider"] = [result.Error!] });
    }

    private static IResult ToExternalProviderResult(ExternalProviderCommandResult result) => result.Outcome switch
    {
        ExternalProviderCommandOutcome.Invalid => Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["externalProvider"] = [result.Error ?? "That change is not allowed."]
        }),
        _ => Results.Ok(ToExternalProviderResponse(result.Status!))
    };

    private static ExternalProviderResponse ToExternalProviderResponse(ExternalProviderStatus status) => new(
        status.Id,
        status.ProviderId,
        status.DisplayName,
        status.BaseUrl,
        status.IsEnabled,
        status.HasApiKey,
        status.ApiKeyHint,
        status.ApiKeySetAtUtc,
        status.CachedProtocolVersion,
        status.CachedCapabilities,
        status.CachedEgressPolicy,
        status.EgressPolicyOverride,
        status.EffectiveEgressPolicy,
        status.LastTestedAtUtc,
        status.LastTestSucceeded,
        status.LastTestMessage);
}
