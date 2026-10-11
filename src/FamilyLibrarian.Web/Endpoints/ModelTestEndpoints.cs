using FamilyLibrarian.Application.ModelTesting;
using FamilyLibrarian.Contracts.ModelTesting;

namespace FamilyLibrarian.Web.Endpoints;

/// <summary>
/// Administrator-only model connectivity and capability test (alpha.6 M3-5). Configuration, plus one manual "run" action.
/// The services behind it check the administrator role again themselves; the route policy is not the only guard.
/// </summary>
internal static class ModelTestEndpoints
{
    public static void MapModelTestEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/admin/model-test")
            .RequireAuthorization("Admin")
            .AddEndpointFilter<AntiforgeryEndpointFilter>();

        group.MapGet("/", GetAsync);
        group.MapPut("/", SaveAsync);
        group.MapPut("/enabled", SetEnabledAsync);
        group.MapPut("/api-key", SetApiKeyAsync);
        group.MapDelete("/api-key", ClearApiKeyAsync);
        group.MapPost("/run", RunAsync);
    }

    private static async Task<IResult> GetAsync(ModelTestSettingsService service, CancellationToken cancellationToken) =>
        ToResult(await service.GetAsync(cancellationToken));

    private static async Task<IResult> SaveAsync(
        SaveModelTestSettingsRequest request, ModelTestSettingsService service, CancellationToken cancellationToken) =>
        ToResult(await service.SaveAsync(
            new SaveModelTestSettings(
                request.EndpointUrl, request.PrimaryModel, request.ComparisonModel, request.CloudModel, request.AllowCloud,
                request.TimeoutSeconds, request.MaxOutputTokens),
            cancellationToken));

    private static async Task<IResult> SetEnabledAsync(
        SetModelTestEnabledRequest request, ModelTestSettingsService service, CancellationToken cancellationToken) =>
        ToResult(await service.SetEnabledAsync(request.Enabled, cancellationToken));

    private static async Task<IResult> SetApiKeyAsync(
        SetModelTestApiKeyRequest request, ModelTestSettingsService service, CancellationToken cancellationToken) =>
        ToResult(await service.SetApiKeyAsync(request.ApiKey, cancellationToken));

    private static async Task<IResult> ClearApiKeyAsync(ModelTestSettingsService service, CancellationToken cancellationToken) =>
        ToResult(await service.ClearApiKeyAsync(cancellationToken));

    private static async Task<IResult> RunAsync(
        RunModelTestRequest request, ModelCapabilityTestService service, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<ModelTestTarget>(request.Target, ignoreCase: true, out var target) || !Enum.IsDefined(target))
        {
            return Invalid("target", "Choose Primary, Comparison or Cloud.");
        }

        List<ModelCheck>? checks = null;
        if (request.Checks is { Count: > 0 })
        {
            checks = [];
            foreach (var name in request.Checks)
            {
                if (!Enum.TryParse<ModelCheck>(name, ignoreCase: true, out var check) || !Enum.IsDefined(check))
                {
                    return Invalid("checks", $"'{name}' is not a known check.");
                }

                checks.Add(check);
            }
        }

        var result = await service.RunAsync(new ModelTestRunRequest(target, checks, request.ConfirmCloud), cancellationToken);
        return result.Outcome switch
        {
            ModelTestRunOutcome.Completed => Results.Ok(ToResponse(result.Report!)),
            ModelTestRunOutcome.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
            ModelTestRunOutcome.Busy => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "A test is already running", detail: result.Error),
            _ => Invalid("run", result.Error ?? "That test could not be run.")
        };
    }

    private static IResult ToResult(ModelTestSettingsResult result)
    {
        if (result.Forbidden)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        return result.Succeeded
            ? Results.Ok(new ModelTestSettingsResponse(
                result.Settings!.IsEnabled, result.Settings.EndpointUrl, result.Settings.HasApiKey, result.Settings.ApiKeySetAtUtc,
                result.Settings.PrimaryModel, result.Settings.ComparisonModel, result.Settings.CloudModel, result.Settings.AllowCloud,
                result.Settings.TimeoutSeconds, result.Settings.MaxOutputTokens))
            : Invalid("modelTest", result.Error!);
    }

    private static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    private static ModelTestReportResponse ToResponse(ModelTestReport report) => new(
        report.Target.ToString(), report.IsCloud, report.ConfiguredModel, report.ReportedModels,
        report.Results.Select(result => new ModelCheckResultResponse(
            result.Check.ToString(), result.Status.ToString(), result.Detail, result.Calls, result.LatencyMilliseconds,
            result.PromptTokens, result.CompletionTokens, result.TotalTokens, result.Sample)).ToArray(),
        report.TotalCalls, report.TotalTokens, report.TotalLatencyMilliseconds);
}
