using FamilyLibrarian.Application.Accounts;
using FamilyLibrarian.Contracts.Accounts;
using FamilyLibrarian.Domain.Accounts;

namespace FamilyLibrarian.Web.Endpoints;

/// <summary>A household member's own default request format -- self-service, not admin-authorized.</summary>
internal static class DefaultRequestFormatEndpoints
{
    public static void MapDefaultRequestFormatEndpoints(this IEndpointRouteBuilder app)
    {
        var preference = app.MapGroup("/api/v1/me/default-request-format")
            .RequireAuthorization()
            .AddEndpointFilter<AntiforgeryEndpointFilter>();

        preference.MapGet("/", GetAsync);
        preference.MapPut("/", SetAsync);
    }

    private static async Task<IResult> GetAsync(
        DefaultRequestFormatService service, CancellationToken cancellationToken) =>
        await service.GetAsync(cancellationToken) is { } format
            ? Results.Ok(new DefaultRequestFormatResponse(format.ToString()))
            : Results.Unauthorized();

    private static async Task<IResult> SetAsync(
        SetDefaultRequestFormatRequest request,
        DefaultRequestFormatService service,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<DefaultRequestFormat>(request.Format, ignoreCase: true, out var format))
        {
            return Invalid("That is not a request format.");
        }

        var result = await service.SetAsync(format, cancellationToken);
        return result.Succeeded
            ? Results.Ok(new DefaultRequestFormatResponse(format.ToString()))
            : Invalid(result.Error!);
    }

    private static IResult Invalid(string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { ["format"] = [message] });
}
