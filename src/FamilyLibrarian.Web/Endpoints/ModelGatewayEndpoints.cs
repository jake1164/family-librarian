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
        group.MapPost("/connection/resolve", ResolveConnectionAsync);
        group.MapGet("/usage", UsageAsync);
        group.MapGet("/failures", FailuresAsync);
        group.MapGet("/models", ListModelsAsync);
        group.MapPut("/", SaveAsync);
        group.MapPut("/enabled", SetEnabledAsync);
        group.MapPut("/api-key", SetApiKeyAsync);
        group.MapDelete("/api-key", ClearApiKeyAsync);
    }

    private static async Task<IResult> GetAsync(
        ModelGatewaySettingsService service, ModelConnectionService connections, CancellationToken cancellationToken)
    {
        // Merges an older model-test connection into the shared one when that is unambiguous, and reports a disagreement.
        var reconciled = await connections.ReconcileAsync(cancellationToken);
        return ToResult(await service.GetAsync(cancellationToken), reconciled.Notice);
    }

    private static async Task<IResult> ResolveConnectionAsync(
        ResolveModelConnectionRequest request, ModelGatewaySettingsService service, ModelConnectionService connections,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<ModelConnectionChoice>(request.Keep, ignoreCase: true, out var choice) || !Enum.IsDefined(choice))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["keep"] = ["Choose Shared or LegacyTest."] });
        }

        var resolved = await connections.ResolveAsync(choice, cancellationToken);
        if (resolved.Forbidden)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        return resolved.Succeeded
            ? ToResult(await service.GetAsync(cancellationToken), resolved.Notice)
            : Results.ValidationProblem(new Dictionary<string, string[]> { ["modelGateway"] = [resolved.Error!] });
    }

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
        return ToResult(await service.SaveAsync(new SaveModelGatewaySettings(request.EndpointUrl, request.AllowCloud, roles, limits, request.AllowCloudExcerpts, request.AllowModelHoldRelease), cancellationToken));
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
                row.Role.ToString(), row.Budget.ToString(), row.Outcome.ToString(), row.IsCloud, row.Calls, row.ReportedTokens, row.CallsWithoutUsage,
                row.Model, row.FailureReason?.ToString(), row.WorkflowEffects is not { } effects ? null :
                    [.. Enum.GetValues<ModelWorkflowEffect>().Where(effect => effect != ModelWorkflowEffect.None && effect != ModelWorkflowEffect.All &&
                        (effects & (int)effect) != 0).Select(effect => effect.ToString())]))],
            [.. (summary.Recommendations ?? []).Select(row => new ModelRecommendationRowResponse(
                row.Role.ToString(), row.Model, row.IsCloud, SafeVerdict(row.Role, row.Verdict), row.Findings))]));
    }

    private static string SafeVerdict(ModelRole role, string verdict) => (role, verdict) switch
    {
        (ModelRole.CandidateAmbiguityAssessment or ModelRole.ArtifactValidationAssessment, "Consistent" or "consistent") => "Consistent",
        (ModelRole.CandidateAmbiguityAssessment or ModelRole.ArtifactValidationAssessment, "Inconsistent" or "inconsistent") => "Inconsistent",
        (_, "Uncertain" or "uncertain") => "Uncertain",
        (ModelRole.SearchGuidance, "Search" or "GiveUp") => verdict,
        (ModelRole.CandidateTriage, "keep" or "veto" or "prefer" or "author_variant") => verdict,
        (ModelRole.ArtifactValidationDecision, "hold" or "no_objection" or "release" or "contains_requested") => verdict,
        _ => "Other"
    };

    private static async Task<IResult> FailuresAsync(
        string? role, string? category, int? page, int? days, DateTimeOffset? until,
        ModelGatewaySettingsService settings, IModelUsageLedger ledger, IClock clock, CancellationToken cancellationToken)
    {
        if ((await settings.GetAsync(cancellationToken)).Forbidden)
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        ModelRole? selected = null;
        ModelCallDiagnosticFilter? filter = null;
        if (!string.IsNullOrEmpty(category))
        {
            if (!ModelCallCategories.IsDiagnosticCategory(category))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["category"] = ["Choose RejectedReply, ProviderError, Cancelled or Unknown."] });
            var exclude = category is ModelCallCategories.Unknown or ModelCallCategories.Cancelled;
            var outcomes = category == ModelCallCategories.Cancelled ? [] : Enum.GetValues<ModelCallOutcome>()
                .Where(outcome => exclude ? ModelCallCategories.Classify(outcome.ToString(), null) != ModelCallCategories.Unknown
                    : ModelCallCategories.Classify(outcome.ToString(), null) == category).ToArray();
            filter = new ModelCallDiagnosticFilter(outcomes, exclude, category == ModelCallCategories.Cancelled);
        }
        if (!string.IsNullOrEmpty(role))
        {
            if (!Enum.TryParse<ModelRole>(role, ignoreCase: false, out var parsed) || !Enum.IsDefined(parsed) || parsed.ToString() != role)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["role"] = ["Choose a known AI feature."] });
            selected = parsed;
        }
        var number = page ?? 1;
        var period = days ?? 1;
        var end = (until ?? clock.UtcNow).ToUniversalTime();
        if (number is < 1 or > 2000 || period is < 1 or > 90 || end > clock.UtcNow.AddMinutes(1) || end < clock.UtcNow.AddDays(-90))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["range"] = ["Choose page 1–2000 and 1–90 days within retained history."] });
        var since = end.AddDays(-period);
        if (since < clock.UtcNow.AddDays(-90)) since = clock.UtcNow.AddDays(-90);
        var result = await ledger.ListFailuresAsync(since, end, selected, number, cancellationToken, filter);
        return Results.Ok(new ModelCallFailuresResponse(since, end, number, result.HasMore,
            [.. result.Rows.Select(row => new ModelCallFailureResponse(row.Id, row.Role.ToString(), row.Model, row.IsCloud,
                row.Outcome.ToString(), row.FailureReason?.ToString(), ModelFailureDiagnostics.Explain(row.Outcome, row.FailureReason),
                row.OccurredAtUtc, row.LatencyMilliseconds, row.MediaAssetId,
                row.Artifact is not { } artifact ? null : new ModelCallArtifactResponse(artifact.AssetId, artifact.WorkId,
                    SafeFormat(artifact.Format), artifact.BundleId, artifact.TrackNumber, artifact.TrackCount)))]));
    }

    private static string SafeFormat(string format) => format.TrimStart('.').ToUpperInvariant() switch
    {
        "EPUB" => "EPUB", "KEPUB" => "KEPUB", "PDF" => "PDF", "MP3" => "MP3", "M4B" => "M4B", "OGG" => "OGG",
        "M4A" => "M4A", "FLAC" => "FLAC", "OPUS" => "OPUS", "AZW3" => "AZW3", "MOBI" => "MOBI", _ => "Other"
    };

    private static async Task<IResult> ListModelsAsync(ModelCatalogService catalog, CancellationToken cancellationToken)
    {
        var result = await catalog.ListAsync(cancellationToken);
        return result.Forbidden
            ? Results.StatusCode(StatusCodes.Status403Forbidden)
            : Results.Ok(new ModelListResponse(result.Models, result.Problem));
    }

    private static async Task<IResult> SetEnabledAsync(
        SetModelGatewayEnabledRequest request, ModelGatewaySettingsService service, CancellationToken cancellationToken) =>
        ToResult(await service.SetEnabledAsync(request.Enabled, cancellationToken));

    private static async Task<IResult> SetApiKeyAsync(
        SetModelGatewayApiKeyRequest request, ModelGatewaySettingsService service, CancellationToken cancellationToken) =>
        ToResult(await service.SetApiKeyAsync(request.ApiKey, cancellationToken));

    private static async Task<IResult> ClearApiKeyAsync(ModelGatewaySettingsService service, CancellationToken cancellationToken) =>
        ToResult(await service.ClearApiKeyAsync(cancellationToken));

    private static IResult ToResult(ModelGatewaySettingsResult result, ModelConnectionNotice? notice = null)
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
            ToDto(settings.Limits), settings.AllowCloudExcerpts,
            notice is null ? null : new ModelConnectionNoticeDto(
                notice.HasConflict, notice.SharedEndpointUrl, notice.SharedHasApiKey, notice.LegacyTestEndpointUrl, notice.LegacyTestHasApiKey, notice.Explanation),
            settings.AllowModelHoldRelease));
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
