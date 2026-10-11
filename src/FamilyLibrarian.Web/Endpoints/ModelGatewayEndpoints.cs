using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.ModelGateway;
using FamilyLibrarian.Contracts.ModelGateway;
using FamilyLibrarian.Domain.ModelGateway;

namespace FamilyLibrarian.Web.Endpoints;

/// <summary>
/// Administrator-only configuration of the production model gateway (alpha.6 M4-0). There is deliberately no route that
/// calls a model: the gateway is used only by server-side services. The services behind these routes check the
/// administrator role again themselves.
/// </summary>
internal static class ModelGatewayEndpoints
{
    public static void MapModelGatewayEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/admin/model-gateway")
            .RequireAuthorization("Admin")
            .AddEndpointFilter<AntiforgeryEndpointFilter>();

        group.MapGet("/", GetAsync);
        group.MapGet("/usage", UsageAsync);
        group.MapPut("/", SaveAsync);
        group.MapPut("/enabled", SetEnabledAsync);
        group.MapPut("/api-key", SetApiKeyAsync);
        group.MapDelete("/api-key", ClearApiKeyAsync);
    }

    private static async Task<IResult> GetAsync(ModelGatewaySettingsService service, CancellationToken cancellationToken) =>
        ToResult(await service.GetAsync(cancellationToken));

    private static async Task<IResult> SaveAsync(
        SaveModelGatewaySettingsRequest request, ModelGatewaySettingsService service, CancellationToken cancellationToken)
    {
        var roles = new List<ModelRoleCommand>();
        foreach (var role in request.Roles ?? [])
        {
            if (!Enum.TryParse<ModelRole>(role.Role, ignoreCase: true, out var parsedRole) || !Enum.IsDefined(parsedRole) ||
                !Enum.TryParse<ModelPlacement>(role.Placement, ignoreCase: true, out var placement) || !Enum.IsDefined(placement) ||
                !Enum.TryParse<ModelReasoningMode>(string.IsNullOrWhiteSpace(role.Reasoning) ? nameof(ModelReasoningMode.Inherit) : role.Reasoning, ignoreCase: true, out var reasoning) || !Enum.IsDefined(reasoning))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["roles"] = [$"'{role.Role}' with placement '{role.Placement}' and reasoning '{role.Reasoning}' is not a known role setting."] });
            }

            roles.Add(new ModelRoleCommand(parsedRole, role.IsEnabled, placement, role.LocalModel, role.CloudModel, reasoning));
        }

        var limits = request.Limits is null ? null : ToLimits(request.Limits);
        return ToResult(await service.SaveAsync(new SaveModelGatewaySettings(request.EndpointUrl, request.AllowCloud, roles, limits, request.AllowCloudExcerpts), cancellationToken));
    }

    private static async Task<IResult> UsageAsync(
        ModelGatewaySettingsService settings, IModelUsageLedger ledger, IClock clock, CancellationToken cancellationToken)
    {
        // The settings service is the administrator check; usage is only shown to someone it would let read the settings.
        if ((await settings.GetAsync(cancellationToken)).Forbidden)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var summary = await ledger.SummarizeAsync(clock.UtcNow.AddDays(-1), cancellationToken);
        return Results.Ok(new ModelUsageResponse(
            summary.Since,
            [.. summary.Rows.Select(row => new ModelUsageRowResponse(
                row.Role.ToString(), row.Budget.ToString(), row.Outcome.ToString(), row.IsCloud, row.Calls, row.ReportedTokens, row.CallsWithoutUsage))]));
    }

    private static async Task<IResult> SetEnabledAsync(
        SetModelGatewayEnabledRequest request, ModelGatewaySettingsService service, CancellationToken cancellationToken) =>
        ToResult(await service.SetEnabledAsync(request.Enabled, cancellationToken));

    private static async Task<IResult> SetApiKeyAsync(
        SetModelGatewayApiKeyRequest request, ModelGatewaySettingsService service, CancellationToken cancellationToken) =>
        ToResult(await service.SetApiKeyAsync(request.ApiKey, cancellationToken));

    private static async Task<IResult> ClearApiKeyAsync(ModelGatewaySettingsService service, CancellationToken cancellationToken) =>
        ToResult(await service.ClearApiKeyAsync(cancellationToken));

    private static IResult ToResult(ModelGatewaySettingsResult result)
    {
        if (result.Forbidden)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        if (!result.Succeeded)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["modelGateway"] = [result.Error!] });
        }

        var settings = result.Settings!;
        return Results.Ok(new ModelGatewaySettingsResponse(
            settings.IsEnabled, settings.EndpointUrl, settings.HasApiKey, settings.ApiKeySetAtUtc, settings.AllowCloud,
            [.. settings.Roles.Select(role => new ModelRoleDto(role.Role.ToString(), role.IsEnabled, role.Placement.ToString(), role.LocalModel, role.CloudModel, role.Reasoning.ToString()))],
            ToDto(settings.Limits), settings.AllowCloudExcerpts));
    }

    private static ModelLimitsDto ToDto(ModelExecutionLimits limits) => new(
        limits.TimeoutSeconds, limits.MaxInputCharacters, limits.MaxOutputTokens, limits.MaxConcurrentCalls,
        ToDto(limits.Interactive), ToDto(limits.Background), limits.CloudDailyRequests, limits.CloudDailyTokens);

    private static ModelBudgetDto ToDto(ModelBudget budget) =>
        new(budget.UserDailyRequests, budget.UserDailyTokens, budget.InstallationDailyRequests, budget.InstallationDailyTokens);

    private static ModelExecutionLimits ToLimits(ModelLimitsDto dto) => new(
        dto.TimeoutSeconds, dto.MaxInputCharacters, dto.MaxOutputTokens, dto.MaxConcurrentCalls,
        ToBudget(dto.Interactive), ToBudget(dto.Background), dto.CloudDailyRequests, dto.CloudDailyTokens);

    private static ModelBudget ToBudget(ModelBudgetDto? dto) =>
        dto is null ? new ModelBudget(-1, -1, -1, -1) : new ModelBudget(dto.UserDailyRequests, dto.UserDailyTokens, dto.InstallationDailyRequests, dto.InstallationDailyTokens);
}
