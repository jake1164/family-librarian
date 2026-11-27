using FamilyLibrarian.Application.Setup;
using FamilyLibrarian.Contracts.Setup;
using FamilyLibrarian.Domain.Setup;

namespace FamilyLibrarian.Web.Endpoints;

/// <summary>
/// First-time setup. A signed-in member reads and records their own progress; only an administrator edits the guidance
/// every member is shown.
/// </summary>
internal static class SetupEndpoints
{
    public static void MapSetupEndpoints(this IEndpointRouteBuilder app)
    {
        var mine = app.MapGroup("/api/v1/me/setup")
            .RequireAuthorization()
            .AddEndpointFilter<AntiforgeryEndpointFilter>();

        mine.MapGet("/", GetMineAsync);
        mine.MapPost("/seen", MarkSeenAsync);
        mine.MapPost("/steps/{step}/completed", CompleteAsync);
        mine.MapPost("/skipped", SkipAsync);

        var guidance = app.MapGroup("/api/v1/admin/setup-guidance")
            .RequireAuthorization("Admin")
            .AddEndpointFilter<AntiforgeryEndpointFilter>();

        guidance.MapGet("/", GetGuidanceAsync);
        guidance.MapPut("/kindle", SetKindleGuidanceAsync);
        guidance.MapPut("/messaging", SetMessagingGuidanceAsync);
        guidance.MapPut("/messaging/element-help", SetShowElementHelpAsync);
    }

    private static async Task<IResult> GetMineAsync(UserSetupService service, CancellationToken cancellationToken) =>
        await service.GetAsync(cancellationToken) is { } overview
            ? Results.Ok(ToResponse(overview))
            : Results.Unauthorized();

    private static async Task<IResult> MarkSeenAsync(UserSetupService service, CancellationToken cancellationToken) =>
        ToProgressResult(await service.MarkWizardSeenAsync(cancellationToken));

    private static async Task<IResult> CompleteAsync(
        string step, UserSetupService service, CancellationToken cancellationToken) =>
        TryParseStep(step, out var parsed)
            ? ToProgressResult(await service.CompleteAsync(parsed, cancellationToken))
            : Invalid("step", "That is not a setup step.");

    private static async Task<IResult> SkipAsync(
        SkipSetupStepsRequest request, UserSetupService service, CancellationToken cancellationToken)
    {
        var steps = SetupSteps.None;
        foreach (var name in request.Steps ?? [])
        {
            if (!TryParseStep(name, out var parsed))
            {
                return Invalid("steps", "That is not a setup step.");
            }

            steps |= parsed;
        }

        return ToProgressResult(await service.SkipAsync(steps, cancellationToken));
    }

    private static async Task<IResult> GetGuidanceAsync(
        SetupGuidanceService service, CancellationToken cancellationToken) =>
        Results.Ok(ToResponse(await service.GetAsync(cancellationToken)));

    private static async Task<IResult> SetKindleGuidanceAsync(
        SetKindleGuidanceRequest request, SetupGuidanceService service, CancellationToken cancellationToken) =>
        ToResult(await service.SetKindleAsync(
            request.KindleSenderAddress, request.AmazonStore, cancellationToken));

    private static async Task<IResult> SetMessagingGuidanceAsync(
        SetMessagingGuidanceRequest request, SetupGuidanceService service, CancellationToken cancellationToken) =>
        ToResult(await service.SetMessagingAsync(
            request.ServerName, request.SignInInstructions, request.HelpUrl, request.WebAppUrl, cancellationToken));

    private static async Task<IResult> SetShowElementHelpAsync(
        SetShowElementHelpRequest request, SetupGuidanceService service, CancellationToken cancellationToken) =>
        ToResult(await service.SetShowElementHelpAsync(request.Enabled, cancellationToken));

    // Only a single named step: a number or a comma-separated list would also parse as flags.
    private static bool TryParseStep(string? name, out SetupSteps step)
    {
        step = name?.ToUpperInvariant() switch
        {
            "FORMATS" => SetupSteps.Formats,
            "KINDLE" => SetupSteps.Kindle,
            "MESSAGING" => SetupSteps.Messaging,
            _ => SetupSteps.None
        };
        return step != SetupSteps.None;
    }

    private static IResult ToProgressResult(bool saved) =>
        saved ? Results.NoContent() : Results.Conflict(new { message = "Your setup progress could not be saved." });

    private static IResult ToResult(SetupGuidanceResult result) => result switch
    {
        { Succeeded: true } => Results.Ok(ToResponse(result.Status!)),
        { IsConflict: true } => Results.Conflict(new { message = result.Error }),
        _ => Invalid("guidance", result.Error!)
    };

    private static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    private static UserSetupResponse ToResponse(UserSetupOverview overview) => new(
        overview.WizardSeen,
        overview.ShouldOpenWizard,
        overview.Steps.Select(step => new UserSetupStepResponse(
            step.Step.ToString(), step.IsAvailable, step.IsSet, step.IsCompleted, step.IsSkipped)).ToArray(),
        overview.ReminderSteps.Select(step => step.ToString()).ToArray(),
        overview.Kindle is { } kindle
            ? new KindleGuidanceResponse(kindle.SenderAddress, kindle.FindAddressUrl, kindle.ManageContentUrl)
            : null,
        overview.Messaging is { } messaging
            ? new MessagingGuidanceResponse(
                messaging.ServerName, messaging.BotUserId, messaging.SignInInstructions, messaging.HelpUrl,
                messaging.WebAppUrl, messaging.ShowElementHelp, messaging.ChatEnabled)
            : null);

    private static SetupGuidanceSettingsResponse ToResponse(SetupGuidanceStatus status) => new(
        status.KindleSenderAddress,
        status.AmazonStoreDomain,
        status.EffectiveAmazonStoreDomain,
        status.FindKindleAddressUrl,
        status.ManageContentUrl,
        status.MessagingServerName,
        status.SuggestedServerName,
        status.MessagingSignInInstructions,
        status.MessagingHelpUrl,
        status.MessagingWebAppUrl,
        status.ShowElementHelp);
}
