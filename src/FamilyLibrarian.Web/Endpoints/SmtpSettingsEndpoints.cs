using FamilyLibrarian.Application.Communications;
using FamilyLibrarian.Domain.Communications;
using FamilyLibrarian.Contracts.Communications;

namespace FamilyLibrarian.Web.Endpoints;

/// <summary>Admin-only configuration and probe routes for outbound SMTP.</summary>
internal static class SmtpSettingsEndpoints
{
    public static void MapSmtpSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var smtp = app.MapGroup("/api/v1/admin/communications/smtp")
            .RequireAuthorization("Admin")
            .AddEndpointFilter<AntiforgeryEndpointFilter>();

        smtp.MapGet("/", GetAsync);
        smtp.MapPut("/", SetSettingsAsync);
        smtp.MapPut("/enabled", SetEnabledAsync);
        smtp.MapPut("/password", SetPasswordAsync);
        smtp.MapDelete("/password", ClearPasswordAsync);
        smtp.MapPost("/test", SendTestAsync);
    }

    private static async Task<IResult> GetAsync(SmtpSettingsService service, CancellationToken cancellationToken) =>
        Results.Ok(ToResponse(await service.GetStatusAsync(cancellationToken)));

    private static async Task<IResult> SetSettingsAsync(
        SetSmtpSettingsRequest request, SmtpSettingsService service, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<SmtpSecurityMode>(request.SecurityMode, ignoreCase: true, out var securityMode) ||
            !Enum.IsDefined(securityMode))
        {
            return Invalid("securityMode", "The SMTP security mode is not valid.");
        }

        return ToResult(await service.SetSettingsAsync(
            request.Host, request.Port, securityMode, request.Username, request.FromAddress, request.FromName, cancellationToken));
    }

    private static async Task<IResult> SetEnabledAsync(
        SetSmtpEnabledRequest request, SmtpSettingsService service, CancellationToken cancellationToken) =>
        ToResult(await service.SetEnabledAsync(request.Enabled, cancellationToken));

    private static async Task<IResult> SetPasswordAsync(
        SetSmtpPasswordRequest request, SmtpSettingsService service, CancellationToken cancellationToken) =>
        ToResult(await service.SetPasswordAsync(request.Password, cancellationToken));

    private static async Task<IResult> ClearPasswordAsync(
        SmtpSettingsService service, CancellationToken cancellationToken) =>
        ToResult(await service.ClearPasswordAsync(cancellationToken));

    private static async Task<IResult> SendTestAsync(
        SendSmtpTestRequest request, SmtpSettingsService service, CancellationToken cancellationToken)
    {
        var result = await service.SendTestAsync(request.RecipientAddress, cancellationToken);
        return result.Succeeded
            ? Results.Ok(new SmtpTestResponse(result.Outcome!.Succeeded, result.Outcome.Message))
            : Invalid("smtp", result.Error!);
    }

    private static IResult ToResult(SmtpCommandResult result) => result.Succeeded
        ? Results.Ok(ToResponse(result.Status!))
        : Invalid("smtp", result.Error!);

    private static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    private static SmtpSettingsResponse ToResponse(SmtpStatus status) => new(
        status.IsEnabled,
        status.Host,
        status.Port,
        status.SecurityMode.ToString(),
        status.Username,
        status.HasPassword,
        status.PasswordSetAtUtc,
        status.FromAddress,
        status.FromName,
        status.LastTestedAtUtc,
        status.LastTestSucceeded,
        status.LastTestMessage);
}
