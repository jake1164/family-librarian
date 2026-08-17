using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Accounts;
using FamilyLibrarian.Contracts.Accounts;
using FamilyLibrarian.Domain.Accounts;

namespace FamilyLibrarian.Web.Endpoints;

internal static class InvitationEndpoints
{
    public const string RateLimitPolicy = "invitation-redemption";

    /// <summary>The client route that redeems an invitation.</summary>
    public const string RedeemPath = "/invite";

    public static void MapInvitationEndpoints(this IEndpointRouteBuilder app)
    {
        // Invitation redemption. Anonymous by necessity — the invitee has no account
        // yet — so it carries no anti-forgery requirement (there are no ambient
        // credentials to forge with) and is rate limited instead.
        app.MapGet("/api/v1/invitations/preview", PreviewInvitationAsync)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicy);
        app.MapPost("/api/v1/invitations/redeem", RedeemInvitationAsync)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicy);

        var adminInvitations = app.MapGroup("/api/v1/admin/invitations")
            .RequireAuthorization("Admin")
            .AddEndpointFilter<AntiforgeryEndpointFilter>();

        adminInvitations.MapGet("/", ListInvitationsAsync);
        adminInvitations.MapPost("/", CreateInvitationAsync);
        adminInvitations.MapPost("/{invitationId:guid}/revoke", RevokeInvitationAsync);
        adminInvitations.MapPost("/{invitationId:guid}/regenerate", RegenerateInvitationAsync);
    }

    /// <summary>
    /// Builds the link an administrator hands to the invitee.
    /// </summary>
    /// <remarks>
    /// The token travels in the URL <em>fragment</em>, not the path or query.
    /// Browsers never send a fragment to the server, so the token stays out of
    /// access logs, proxy logs, and <c>Referer</c> headers on the page the
    /// invitee lands on — while the WebAssembly client can still read it. The
    /// token only reaches the host in the body of the redemption POST.
    /// </remarks>
    public static string BuildRedeemUrl(HttpContext httpContext, string token)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var request = httpContext.Request;
        var origin = $"{request.Scheme}://{request.Host}{request.PathBase}";
        return $"{origin}{RedeemPath}#{token}";
    }

    private static async Task<IResult> ListInvitationsAsync(
        InvitationService invitationService,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var invitations = await invitationService.ListAsync(cancellationToken);
        var now = clock.UtcNow;

        return Results.Ok(new InvitationListResponse(
            invitations.Select(invitation => ToInvitationResponse(invitation, now)).ToArray()));
    }

    private static async Task<IResult> CreateInvitationAsync(
        CreateInvitationRequest request,
        InvitationService invitationService,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
        ToInvitationResult(
            await invitationService.CreateAsync(request.Email, request.AsAdmin, cancellationToken),
            httpContext);

    private static async Task<IResult> RegenerateInvitationAsync(
        Guid invitationId,
        InvitationService invitationService,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
        ToInvitationResult(
            await invitationService.RegenerateAsync(invitationId, cancellationToken),
            httpContext);

    private static IResult ToInvitationResult(CreateInvitationResult result, HttpContext httpContext)
    {
        if (!result.Succeeded)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["email"] = [result.Error ?? "That invitation could not be created."]
            });
        }

        var invitation = result.Invitation!;
        return Results.Ok(new CreatedInvitationResponse(
            invitation.Id,
            invitation.Email,
            invitation.Role,
            invitation.ExpiresAtUtc,
            result.Token!,
            BuildRedeemUrl(httpContext, result.Token!)));
    }

    private static async Task<IResult> RevokeInvitationAsync(
        Guid invitationId,
        InvitationService invitationService,
        CancellationToken cancellationToken)
    {
        var result = await invitationService.RevokeAsync(invitationId, cancellationToken);

        return result.Outcome switch
        {
            InvitationCommandOutcome.Success => Results.NoContent(),
            InvitationCommandOutcome.NotFound => Results.NotFound(),
            _ => Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["invitation"] = [result.Error ?? "That invitation could not be withdrawn."]
            })
        };
    }

    private static async Task<IResult> PreviewInvitationAsync(
        string? token,
        InvitationService invitationService,
        CancellationToken cancellationToken)
    {
        var preview = await invitationService.PreviewAsync(token ?? string.Empty, cancellationToken);

        // A missing token and an unusable one answer the same way, so this endpoint
        // cannot be used to test whether a guessed token exists.
        return preview is null
            ? Results.NotFound()
            : Results.Ok(new InvitationPreviewResponse(
                preview.Email,
                preview.CanBeRedeemed,
                preview.State));
    }

    private static async Task<IResult> RedeemInvitationAsync(
        RedeemInvitationRequest request,
        InvitationService invitationService,
        CancellationToken cancellationToken)
    {
        var result = await invitationService.RedeemAsync(
            request.Token,
            request.DisplayName,
            request.Password,
            cancellationToken);

        return result.Outcome switch
        {
            RedeemInvitationOutcome.Success => Results.NoContent(),
            RedeemInvitationOutcome.InvalidInvitation => Results.ValidationProblem(
                new Dictionary<string, string[]>
                {
                    ["token"] = ["This invitation link is not valid. Ask for a new one."]
                }),
            _ => Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["password"] = [result.Error ?? "That account could not be created."]
            })
        };
    }

    private static InvitationResponse ToInvitationResponse(Invitation invitation, DateTimeOffset now) => new(
        invitation.Id,
        invitation.Email,
        invitation.Role,
        invitation.DescribeState(now),
        invitation.CanBeRedeemedAt(now),
        invitation.CreatedAtUtc,
        invitation.ExpiresAtUtc,
        invitation.RedeemedAtUtc,
        invitation.RevokedAtUtc);
}
