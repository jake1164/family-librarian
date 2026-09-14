using FamilyLibrarian.Application.Communications;
using FamilyLibrarian.Contracts.Communications;

namespace FamilyLibrarian.Web.Endpoints;

/// <summary>A household member's own Matrix identity link (COMM-1 §C) -- distinct from admin provider config.</summary>
internal static class MatrixIdentityLinkEndpoints
{
    public static void MapMatrixIdentityLinkEndpoints(this IEndpointRouteBuilder app)
    {
        var link = app.MapGroup("/api/v1/me/communications/matrix")
            .RequireAuthorization()
            .AddEndpointFilter<AntiforgeryEndpointFilter>();

        link.MapGet("/", GetAsync);
        link.MapPost("/", RequestLinkAsync);
        link.MapDelete("/", UnlinkAsync);
    }

    private static async Task<IResult> GetAsync(MatrixIdentityLinkService service, CancellationToken cancellationToken)
    {
        var status = await service.GetStatusAsync(cancellationToken);
        return status is null ? Results.Unauthorized() : Results.Ok(ToResponse(status));
    }

    private static async Task<IResult> RequestLinkAsync(
        RequestMatrixLinkRequest request, MatrixIdentityLinkService service, CancellationToken cancellationToken) =>
        ToResult(await service.RequestLinkAsync(request.MatrixUserId, cancellationToken));

    private static async Task<IResult> UnlinkAsync(
        MatrixIdentityLinkService service, CancellationToken cancellationToken) =>
        ToResult(await service.UnlinkAsync(cancellationToken));

    private static IResult ToResult(MatrixLinkResult result) => result.Succeeded
        ? Results.Ok(ToResponse(result.Status!))
        : Invalid(result.Error!);

    private static IResult Invalid(string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { ["matrixUserId"] = [message] });

    private static MatrixLinkStatusResponse ToResponse(MatrixLinkStatus status) =>
        new(status.IsVerified, status.MatrixUserId, status.AwaitingVerification);
}
