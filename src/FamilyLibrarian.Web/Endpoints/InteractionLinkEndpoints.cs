using System.Security.Claims;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Contracts.Acquisition;
using FamilyLibrarian.Domain.Acquisition;
using Microsoft.AspNetCore.Authentication;

namespace FamilyLibrarian.Web.Endpoints;

/// <summary>
/// The anonymous magic-link claim endpoint and the two grant-protected routes it
/// signs the browser into (HUMAN-ACQ-1 D11/D12). See
/// <c>.ai_docs/human-acq-1-matrix-authorize-plan.md</c> WP6.
/// </summary>
internal static class InteractionLinkEndpoints
{
    public const string RateLimitPolicy = "interaction-link-claim";

    /// <summary>
    /// A second, unpartitioned ceiling registered alongside <see cref="RateLimitPolicy"/>
    /// — see its registration in <c>Program.cs</c>, mirroring <c>InvitationEndpoints</c>.
    /// </summary>
    public const string GlobalRateLimitPolicy = "interaction-link-claim-global";

    public static void MapInteractionLinkEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/interaction-links");

        // Anonymous by necessity: the token itself is the only credential this
        // caller has. No antiforgery requirement -- there are no ambient
        // credentials to forge with, same reasoning as invitation redemption.
        // Rate limited instead, against a 256-bit-token guessing attempt.
        group.MapPost("/claim", ClaimAsync)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicy)
            .RequireRateLimiting(GlobalRateLimitPolicy);

        group.MapGet("/jobs/{jobId:guid}/status", GetStatusAsync)
            .RequireAuthorization(InteractionGrantDefaults.PolicyName);

        group.MapGet("/jobs/{jobId:guid}/view", HandleViewAsync)
            .RequireAuthorization(InteractionGrantDefaults.PolicyName);
    }

    private static async Task<IResult> ClaimAsync(
        ClaimInteractionLinkRequest request,
        HttpContext context,
        ProviderInteractionLinkService linkService,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var outcome = await linkService.ClaimAsync(request.Token, cancellationToken);
        switch (outcome.Kind)
        {
            case InteractionLinkClaimOutcomeKind.Invalid:
                // Identical body for unknown/used/revoked/ineligible tokens --
                // no oracle for which reason applied.
                return Results.NotFound();
            case InteractionLinkClaimOutcomeKind.Expired:
                return Results.Json(new { reason = "expired" }, statusCode: StatusCodes.Status410Gone);
            case InteractionLinkClaimOutcomeKind.Done:
                return Results.Conflict(new { reason = "done" });
            case InteractionLinkClaimOutcomeKind.NothingWaiting:
                return Results.Conflict(new { reason = "nothing-waiting" });
            case InteractionLinkClaimOutcomeKind.ClaimedByOther:
                return Results.Conflict(new { reason = "claimed", claimedBy = outcome.ClaimedByDisplayName });
        }

        // The claim is kept either way (recorded on the alert and in the claims
        // table) -- 200 regardless of outcome.ProviderUnavailable, always with
        // the jobId the page needs to start polling /status. When the
        // provider's own interaction/start call failed, that status simply
        // reads "starting" for longer than usual: the alert worker retries the
        // start on its next pass, no separate HTTP-level signal is needed.
        await SignInGrantAsync(context, outcome, clock, cancellationToken);
        return Results.Ok(new ClaimInteractionLinkResponse(
            outcome.JobId!.Value, outcome.WorkTitle, outcome.ProviderDisplayName!,
            GrantExpiresAtUtc(outcome, clock)));
    }

    private static async Task SignInGrantAsync(
        HttpContext context, InteractionLinkClaimResult outcome, IClock clock, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var identity = new ClaimsIdentity(InteractionGrantDefaults.AuthenticationScheme);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, outcome.UserId!.Value.ToString()));
        identity.AddClaim(new Claim(InteractionGrantDefaults.JobIdClaimType, outcome.JobId!.Value.ToString()));
        identity.AddClaim(new Claim(InteractionGrantDefaults.AlertIdClaimType, outcome.AlertId!.Value.ToString()));

        await context.SignInAsync(
            InteractionGrantDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { ExpiresUtc = GrantExpiresAtUtc(outcome, clock), IsPersistent = false });
    }

    private static DateTimeOffset GrantExpiresAtUtc(InteractionLinkClaimResult outcome, IClock clock)
    {
        var maxLease = clock.UtcNow.AddMinutes(30);
        return outcome.InteractionExpiresAtUtc is { } interactionExpiresAtUtc && interactionExpiresAtUtc < maxLease
            ? interactionExpiresAtUtc
            : maxLease;
    }

    /// <summary>
    /// Read-only: never mutates the job or claim. "ready" mirrors
    /// <see cref="ProviderRemoteViewBrokerService.EvaluateAsync"/> exactly, so the
    /// page opens the view socket only once the broker would actually accept it.
    /// </summary>
    private static async Task<IResult> GetStatusAsync(
        Guid jobId,
        ClaimsPrincipal user,
        ProviderRemoteViewBrokerService broker,
        ProviderInteractionClaimService claimService,
        IProviderAcquisitionJobStore jobs,
        CancellationToken cancellationToken)
    {
        if (!TryGetGrantJobId(user, out var grantJobId) || grantJobId != jobId)
        {
            return Results.Json(new { message = "This grant does not apply to that job." }, statusCode: StatusCodes.Status403Forbidden);
        }

        var userId = GetGrantUserId(user);
        if (!await claimService.IsHolderAsync(jobId, userId, cancellationToken))
        {
            return Results.Ok(new InteractionLinkStatusResponse(
                "released", "This session has ended. If it's still needed, a new link will be sent."));
        }

        var job = await jobs.FindAsync(jobId, cancellationToken);
        if (job is null)
        {
            return Results.Ok(new InteractionLinkStatusResponse("cancelled", null));
        }

        var state = job.LifecycleState switch
        {
            ProviderAcquisitionJobLifecycleState.Completed => "done",
            ProviderAcquisitionJobLifecycleState.Cancelled or ProviderAcquisitionJobLifecycleState.Failed => "cancelled",
            _ => await DescribeWaitingStateAsync(jobId, broker, cancellationToken)
        };

        return Results.Ok(new InteractionLinkStatusResponse(state, null));
    }

    private static async Task<string> DescribeWaitingStateAsync(
        Guid jobId, ProviderRemoteViewBrokerService broker, CancellationToken cancellationToken) =>
        await broker.EvaluateAsync(jobId, cancellationToken) switch
        {
            RemoteViewEligibility.Eligible => "ready",
            RemoteViewEligibility.Expired => "expired",
            RemoteViewEligibility.NotFound => "cancelled",
            _ => "starting"
        };

    private static async Task<IResult> HandleViewAsync(
        Guid jobId,
        HttpContext context,
        ProviderRemoteViewBrokerService broker,
        ProviderInteractionClaimService claimService,
        IHostApplicationLifetime lifetime,
        CancellationToken cancellationToken)
    {
        if (!TryGetGrantJobId(context.User, out var grantJobId) || grantJobId != jobId)
        {
            return Results.Json(new { message = "This grant does not apply to that job." }, statusCode: StatusCodes.Status403Forbidden);
        }

        var userId = GetGrantUserId(context.User);
        if (!await claimService.IsHolderAsync(jobId, userId, cancellationToken))
        {
            return Results.Conflict(new { message = "This claim is no longer active." });
        }

        return await RemoteViewSocketHandler.HandleAsync(
            jobId, context, broker, lifetime, cancellationToken,
            ct => claimService.IsHolderAsync(jobId, userId, ct));
    }

    private static bool TryGetGrantJobId(ClaimsPrincipal user, out Guid jobId) =>
        Guid.TryParse(user.FindFirstValue(InteractionGrantDefaults.JobIdClaimType), out jobId);

    private static Guid GetGrantUserId(ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
