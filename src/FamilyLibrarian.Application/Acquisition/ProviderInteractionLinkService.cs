using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Accounts;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Domain.Accounts;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Audit;

namespace FamilyLibrarian.Application.Acquisition;

/// <summary>
/// Consumes a HUMAN-ACQ-1 Matrix magic-link token (D2/D11): the token itself is
/// the caller's only credential — there is no ambient signed-in identity, because
/// the anonymous <c>/interaction</c> page has none. Every recipient row already
/// names the exact user the token was issued to; that stored
/// <see cref="ProviderInteractionAlertRecipient.UserId"/>, never anything the
/// caller asserts, is who gets claimed on and signed into the grant.
/// </summary>
public sealed class ProviderInteractionLinkService(
    IProviderInteractionAlertStore alerts,
    IProviderInteractionClaimStore claims,
    IProviderAcquisitionJobStore jobs,
    IRequestRepository requests,
    ProviderInteractionClaimService claimService,
    ProviderInteractionService interactionService,
    IUserAccountStore accounts,
    ISecureTokenGenerator tokenGenerator,
    IAuditWriter audit,
    IClock clock)
{
    private const int MaxTokenLength = 128;

    public async Task<InteractionLinkClaimResult> ClaimAsync(string? token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > MaxTokenLength)
        {
            return InteractionLinkClaimResult.Invalid();
        }

        var hash = tokenGenerator.Hash(token);
        var recipient = await alerts.FindRecipientByTokenHashAsync(hash, cancellationToken);
        if (recipient is null || !recipient.HasUsableToken)
        {
            return InteractionLinkClaimResult.Invalid();
        }

        return await ClaimRecipientAsync(recipient, recipient.UserId, useFallback: false, cancellationToken);
    }

    /// <summary>
    /// Selects the provider fallback from a reply to the exact Matrix alert
    /// event. Room/event binding is performed by the store lookup; this method
    /// additionally rechecks active-admin status and consumes/revokes the same
    /// one-time recipient capability as the browser link.
    /// </summary>
    public async Task<InteractionLinkClaimResult> UseFallbackFromMatrixReplyAsync(
        Guid linkedUserId, string roomId, string? replyToEventId, CancellationToken cancellationToken)
    {
        if (linkedUserId == Guid.Empty || string.IsNullOrWhiteSpace(roomId) ||
            string.IsNullOrWhiteSpace(replyToEventId) || replyToEventId.Length > ProviderInteractionAlertRecipient.MaxRoomOrEventIdLength)
        {
            return InteractionLinkClaimResult.Invalid();
        }

        var recipient = await alerts.FindRecipientByRoomAndEventIdAsync(roomId, replyToEventId, cancellationToken);
        if (recipient is null || !recipient.HasUsableToken || recipient.UserId != linkedUserId)
        {
            return InteractionLinkClaimResult.Invalid();
        }

        return await ClaimRecipientAsync(recipient, linkedUserId, useFallback: true, cancellationToken);
    }

    private async Task<InteractionLinkClaimResult> ClaimRecipientAsync(
        ProviderInteractionAlertRecipient recipient,
        Guid userId,
        bool useFallback,
        CancellationToken cancellationToken)
    {
        if (!recipient.HasUsableToken || recipient.UserId != userId)
            return InteractionLinkClaimResult.Invalid();

        var alert = recipient.Alert;
        var closedOutcome = TryMapClosedAlert(alert);
        if (closedOutcome is not null)
        {
            return closedOutcome;
        }

        if (alert.State == ProviderInteractionAlertState.Claimed)
        {
            if (alert.ClaimedByUserId == userId)
            {
                // Single-use: this recipient already has (or had) the job through
                // some path -- send them back to the page/app they already have
                // open rather than pretending this is a fresh claim.
                return InteractionLinkClaimResult.Invalid();
            }

            return InteractionLinkClaimResult.ClaimedByOther(alert.ClaimedByDisplayName ?? "another administrator");
        }

        var admin = await accounts.FindAsync(userId, cancellationToken);
        if (admin is null || !admin.IsAdmin || !UserStatuses.CanSignIn(admin.Status))
        {
            return InteractionLinkClaimResult.Invalid();
        }

        // An in-app start may have claimed this provider since the worker's
        // last pass. Treat it as the alert winner immediately, before the
        // worker edits the messages and revokes their links.
        var existingProviderClaim = (await claims.ListAsync(cancellationToken))
            .FirstOrDefault(claim => claim.ExternalProviderId == alert.ExternalProviderId &&
                claim.Channel != ProviderInteractionClaimChannel.MatrixLink && claimService.IsLeaseLive(claim));
        if (existingProviderClaim is not null)
        {
            var holder = await accounts.FindAsync(existingProviderClaim.ClaimedByUserId, cancellationToken);
            return InteractionLinkClaimResult.ClaimedByOther(holder?.DisplayName ?? "another administrator");
        }

        var now = clock.UtcNow;
        var candidates = (await jobs.ListWaitingForInteractionAsync(cancellationToken))
            .Where(job => job.ExternalProviderId == alert.ExternalProviderId &&
                (job.InteractionExpiresAtUtc is not { } expires || expires > now))
            .OrderBy(job => job.WaitingSinceUtc ?? DateTimeOffset.MaxValue)
            .ToArray();

        if (candidates.Length == 0)
        {
            return InteractionLinkClaimResult.NothingWaiting();
        }

        ProviderAcquisitionJob? won = null;
        string? lastHolderName = null;
        foreach (var candidate in candidates)
        {
            var outcome = await claimService.TryClaimForUserAsync(
                candidate.Id, userId, ProviderInteractionClaimChannel.MatrixLink, alert.Id, cancellationToken);
            if (outcome.Kind is ClaimOutcomeKind.Claimed or ClaimOutcomeKind.AlreadyYours)
            {
                won = candidate;
                break;
            }

            lastHolderName = outcome.HeldByDisplayName ?? lastHolderName;
        }

        if (won is null)
        {
            return InteractionLinkClaimResult.ClaimedByOther(lastHolderName ?? "another administrator");
        }

        var alertOutcome = await ApplyClaimToAlertAsync(alert, recipient, won.Id, admin.DisplayName, cancellationToken);
        if (alertOutcome is not null)
        {
            // A different recipient won the provider alert while we were
            // claiming a different job. The provisional claim must not outlive
            // that loss or turn into a second magic-link grant.
            await claimService.ReleaseProvisionalAlertClaimAsync(won.Id, userId, alert.Id, cancellationToken);
            return alertOutcome;
        }

        await audit.WriteAsync(
            AuditActions.ProviderInteractionClaimed, AuditSubjectTypes.ProviderInteraction, won.Id.ToString(),
            new { JobId = won.Id, UserId = userId, Channel = ProviderInteractionClaimChannel.MatrixLink.ToString(), Action = useFallback ? "Fallback" : "Verify" },
            cancellationToken);

        var commandOutcome = useFallback
            ? await interactionService.UseFallbackAsync(won.Id, cancellationToken)
            : await interactionService.StartAsync(won.Id, userId, cancellationToken);
        var requestView = await requests.FindAdminViewAsync(won.RequestId, cancellationToken);

        return InteractionLinkClaimResult.Claimed(
            won.Id, userId, alert.Id, requestView?.Request.WorkTitle, alert.ProviderDisplayName,
            won.InteractionExpiresAtUtc, providerUnavailable: commandOutcome.Result != ProviderInteractionCommandResult.Success);
    }

    /// <summary>
    /// Records the winning claim on the alert: marks this recipient consumed,
    /// revokes every sibling token (single winner, D3), and moves the alert to
    /// Claimed. Retried once if the alert worker updated the same alert
    /// concurrently (its own <c>xmin</c> changed between our read and our write).
    /// </summary>
    private async Task<InteractionLinkClaimResult?> ApplyClaimToAlertAsync(
        ProviderInteractionAlert alert, ProviderInteractionAlertRecipient recipient, Guid wonJobId,
        string claimedByDisplayName, CancellationToken cancellationToken)
    {
        ApplyClaimInMemory(alert, recipient, wonJobId, claimedByDisplayName);

        if (await alerts.TrySaveChangesAsync(cancellationToken))
        {
            return null;
        }

        var fresh = await alerts.FindByIdAsync(alert.Id, cancellationToken) ??
            throw new InvalidOperationException($"Alert {alert.Id} was deleted mid-claim.");
        if (fresh.State != ProviderInteractionAlertState.Open)
        {
            return TryMapClosedAlert(fresh) ??
                InteractionLinkClaimResult.ClaimedByOther(fresh.ClaimedByDisplayName ?? "another administrator");
        }

        var freshRecipient = fresh.Recipients.First(candidate => candidate.Id == recipient.Id);
        if (!freshRecipient.HasUsableToken)
        {
            return InteractionLinkClaimResult.Invalid();
        }

        ApplyClaimInMemory(fresh, freshRecipient, wonJobId, claimedByDisplayName);
        if (!await alerts.TrySaveChangesAsync(cancellationToken))
        {
            var winner = await alerts.FindByIdAsync(alert.Id, cancellationToken);
            return winner is null ? InteractionLinkClaimResult.Invalid() :
                TryMapClosedAlert(winner) ??
                InteractionLinkClaimResult.ClaimedByOther(winner.ClaimedByDisplayName ?? "another administrator");
        }

        return null;
    }

    private void ApplyClaimInMemory(
        ProviderInteractionAlert alert, ProviderInteractionAlertRecipient recipient, Guid wonJobId,
        string claimedByDisplayName)
    {
        var now = clock.UtcNow;
        recipient.MarkConsumed(now);
        foreach (var sibling in alert.Recipients.Where(candidate => candidate.Id != recipient.Id))
        {
            sibling.RevokeToken(now);
        }

        if (alert.State == ProviderInteractionAlertState.Open)
        {
            alert.Claim(wonJobId, recipient.UserId, claimedByDisplayName, now);
        }
    }

    private static InteractionLinkClaimResult? TryMapClosedAlert(ProviderInteractionAlert alert) => alert.State switch
    {
        ProviderInteractionAlertState.Resolved when alert.CloseReason is "cancelled" or "failed" =>
            InteractionLinkClaimResult.Expired(),
        ProviderInteractionAlertState.Resolved => InteractionLinkClaimResult.Done(),
        ProviderInteractionAlertState.Expired or ProviderInteractionAlertState.Superseded =>
            InteractionLinkClaimResult.Expired(),
        _ => null
    };
}

public enum InteractionLinkClaimOutcomeKind
{
    Claimed,
    ClaimedByOther,
    Done,
    NothingWaiting,
    Expired,
    Invalid
}

public sealed record InteractionLinkClaimResult(
    InteractionLinkClaimOutcomeKind Kind,
    Guid? JobId = null,
    Guid? UserId = null,
    Guid? AlertId = null,
    string? WorkTitle = null,
    string? ProviderDisplayName = null,
    DateTimeOffset? InteractionExpiresAtUtc = null,
    string? ClaimedByDisplayName = null,
    bool ProviderUnavailable = false)
{
    public static InteractionLinkClaimResult Claimed(
        Guid jobId, Guid userId, Guid alertId, string? workTitle, string providerDisplayName,
        DateTimeOffset? interactionExpiresAtUtc, bool providerUnavailable) =>
        new(InteractionLinkClaimOutcomeKind.Claimed, jobId, userId, alertId, workTitle, providerDisplayName,
            interactionExpiresAtUtc, ProviderUnavailable: providerUnavailable);

    public static InteractionLinkClaimResult ClaimedByOther(string displayName) =>
        new(InteractionLinkClaimOutcomeKind.ClaimedByOther, ClaimedByDisplayName: displayName);

    public static InteractionLinkClaimResult Done() => new(InteractionLinkClaimOutcomeKind.Done);

    public static InteractionLinkClaimResult NothingWaiting() => new(InteractionLinkClaimOutcomeKind.NothingWaiting);

    public static InteractionLinkClaimResult Expired() => new(InteractionLinkClaimOutcomeKind.Expired);

    public static InteractionLinkClaimResult Invalid() => new(InteractionLinkClaimOutcomeKind.Invalid);
}
