using System.Net;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Accounts;
using FamilyLibrarian.Application.Communications;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Communications;

namespace FamilyLibrarian.Application.Acquisition;

/// <summary>
/// One pass of HUMAN-ACQ-1's Matrix notify/authorize alert worker: reaps lapsed
/// claims, closes finished alerts, opens new ones for providers that genuinely
/// need a person, and sends/edits the Matrix messages that carry the magic
/// links. Driven by a hosted service the same way <c>OutboundCommunicationDispatcher</c>
/// and <c>MatrixInboundSyncCoordinator</c> are — see
/// <c>.ai_docs/human-acq-1-matrix-authorize-plan.md</c> §3-4 (WP7) for the full
/// end-to-end flow this implements.
/// </summary>
public sealed class ProviderInteractionAlertService(
    IProviderAcquisitionJobStore jobs,
    IProviderInteractionClaimStore claims,
    ProviderInteractionClaimService claimService,
    IProviderInteractionAlertStore alerts,
    IExternalProviderStore providers,
    IRequestRepository requests,
    IUserAccountStore accounts,
    IMatrixSettingsStore matrixSettingsStore,
    IUserMatrixDestinationLookup matrixDestinations,
    ICredentialProtector protector,
    IMatrixClient matrixClient,
    ISecureTokenGenerator tokenGenerator,
    ProviderInteractionService interactionService,
    ProviderInteractionAlertOptions options,
    IClock clock)
{
    public async Task RunPassAsync(CancellationToken cancellationToken)
    {
        if (!options.IsEnabled)
        {
            return;
        }

        var matrixSettings = await matrixSettingsStore.FindAsync(cancellationToken);
        if (matrixSettings is not { IsEnabled: true, HasAccessToken: true, HomeserverUrl: not null })
        {
            return;
        }

        var now = clock.UtcNow;
        var waitingJobs = await jobs.ListWaitingForInteractionAsync(cancellationToken);
        var waitingJobsById = waitingJobs.ToDictionary(job => job.Id);
        var openClaims = await claims.ListAsync(cancellationToken);
        var openAlerts = (await alerts.ListOpenAsync(cancellationToken)).ToList();

        var claimsByJob = await MaintainClaimsAsync(waitingJobsById, openClaims, openAlerts, now, cancellationToken);
        await RetryStartsAsync(waitingJobsById, claimsByJob, cancellationToken);
        await ReconcileInAppClaimsAsync(openAlerts, claimsByJob, now, cancellationToken);
        await CloseAlertsAsync(openAlerts, waitingJobs, claimsByJob, now, cancellationToken);

        var newAlerts = await OpenAlertsAsync(waitingJobs, claimsByJob, openAlerts, now, cancellationToken);

        var workingSet = openAlerts.Concat(newAlerts).ToList();
        await DeliverAsync(workingSet, waitingJobs, claimsByJob, matrixSettings, now, cancellationToken);
        await EditAsync(workingSet, matrixSettings, cancellationToken);
    }

    /// <summary>
    /// Removes a claim whose job is no longer Waiting; removes (and Supersedes the
    /// owning alert of) a claim whose lease has lapsed. Returns the surviving
    /// claims keyed by job id.
    /// </summary>
    private async Task<Dictionary<Guid, ProviderInteractionClaim>> MaintainClaimsAsync(
        Dictionary<Guid, ProviderAcquisitionJob> waitingJobsById, IReadOnlyList<ProviderInteractionClaim> openClaims,
        List<ProviderInteractionAlert> openAlerts, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var survivors = new Dictionary<Guid, ProviderInteractionClaim>();
        var changed = false;

        foreach (var claim in openClaims)
        {
            if (!waitingJobsById.ContainsKey(claim.JobId))
            {
                claims.Remove(claim);
                changed = true;
                continue;
            }

            if (!claimService.IsLeaseLive(claim))
            {
                claims.Remove(claim);
                changed = true;

                var owningAlert = openAlerts.FirstOrDefault(
                    alert => alert.State == ProviderInteractionAlertState.Claimed && alert.ClaimedJobId == claim.JobId);
                if (owningAlert is not null)
                {
                    owningAlert.Supersede("lease-lapsed", now);
                    CloseRecipients(owningAlert, now);
                }

                continue;
            }

            survivors[claim.JobId] = claim;
        }

        if (changed)
        {
            await claims.SaveChangesAsync(cancellationToken);
        }

        return survivors;
    }

    /// <summary>A claimed, lease-live job whose interaction session was never (re)started gets one retry per pass.</summary>
    private async Task RetryStartsAsync(
        Dictionary<Guid, ProviderAcquisitionJob> waitingJobsById, Dictionary<Guid, ProviderInteractionClaim> claimsByJob,
        CancellationToken cancellationToken)
    {
        foreach (var claim in claimsByJob.Values)
        {
            if (waitingJobsById.TryGetValue(claim.JobId, out var job) && job.InteractionViewSessionStartedAtUtc is null)
            {
                // Best-effort: the next pass retries regardless of outcome.
                await interactionService.StartAsync(claim.JobId, claim.ClaimedByUserId, cancellationToken);
            }
        }
    }

    private async Task ReconcileInAppClaimsAsync(
        List<ProviderInteractionAlert> openAlerts, Dictionary<Guid, ProviderInteractionClaim> claimsByJob,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var changed = false;
        foreach (var alert in openAlerts)
        {
            var claim = claimsByJob.Values
                .Where(candidate => candidate.ExternalProviderId == alert.ExternalProviderId &&
                    candidate.Channel != ProviderInteractionClaimChannel.MatrixLink)
                .OrderBy(candidate => candidate.ClaimedAtUtc)
                .FirstOrDefault();
            if (claim is null)
            {
                continue;
            }

            if (alert.State == ProviderInteractionAlertState.Open)
            {
                var holder = await accounts.FindAsync(claim.ClaimedByUserId, cancellationToken);
                alert.Claim(claim.JobId, claim.ClaimedByUserId, holder?.DisplayName ?? "another administrator", now);
                CloseRecipients(alert, now);
                changed = true;
            }
            else if (alert.State == ProviderInteractionAlertState.Claimed &&
                alert.ClaimedJobId == claim.JobId && alert.ClaimedByUserId != claim.ClaimedByUserId)
            {
                var holder = await accounts.FindAsync(claim.ClaimedByUserId, cancellationToken);
                alert.TransferClaim(claim.ClaimedByUserId, holder?.DisplayName ?? "another administrator", now);
                changed = true;
            }
        }

        if (changed)
        {
            await alerts.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task CloseAlertsAsync(
        List<ProviderInteractionAlert> openAlerts, IReadOnlyList<ProviderAcquisitionJob> waitingJobs,
        Dictionary<Guid, ProviderInteractionClaim> claimsByJob, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var changed = false;

        foreach (var alert in openAlerts)
        {
            if (alert.State == ProviderInteractionAlertState.Claimed && alert.ClaimedJobId is { } claimedJobId &&
                !waitingJobs.Any(job => job.Id == claimedJobId))
            {
                var job = await jobs.FindAsync(claimedJobId, cancellationToken);
                var reason = job?.LifecycleState switch
                {
                    ProviderAcquisitionJobLifecycleState.Cancelled => "cancelled",
                    ProviderAcquisitionJobLifecycleState.Failed => "failed",
                    _ => "verified"
                };
                alert.Resolve(reason, now);
                CloseRecipients(alert, now);
                changed = true;
                continue;
            }

            if (alert.State == ProviderInteractionAlertState.Open &&
                !waitingJobs.Any(job => job.ExternalProviderId == alert.ExternalProviderId && NeedsAPerson(job, claimsByJob)))
            {
                alert.Resolve("no-longer-needed", now);
                CloseRecipients(alert, now);
                changed = true;
                continue;
            }

            if (alert.IsOpen && now - alert.CreatedAtUtc > options.AlertMaxAge)
            {
                alert.Expire(now);
                CloseRecipients(alert, now);
                changed = true;
            }
        }

        if (changed)
        {
            await alerts.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<List<ProviderInteractionAlert>> OpenAlertsAsync(
        IReadOnlyList<ProviderAcquisitionJob> waitingJobs, Dictionary<Guid, ProviderInteractionClaim> claimsByJob,
        List<ProviderInteractionAlert> openAlerts, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var stillOpenProviderIds = openAlerts.Where(alert => alert.IsOpen)
            .Select(alert => alert.ExternalProviderId).ToHashSet();
        var created = new List<ProviderInteractionAlert>();

        foreach (var providerId in waitingJobs.Select(job => job.ExternalProviderId).Distinct())
        {
            if (stillOpenProviderIds.Contains(providerId))
            {
                continue;
            }

            var recentlySuperseded = await alerts.FindMostRecentlyClosedForProviderAsync(providerId, cancellationToken);
            var skipSendDelay = recentlySuperseded is { CloseReason: "lease-lapsed", ClosedAtUtc: { } closedAt } &&
                now - closedAt < options.AlertMaxAge;

            var eligible = waitingJobs.Where(job =>
                job.ExternalProviderId == providerId && NeedsAPerson(job, claimsByJob) &&
                (skipSendDelay || now - (job.WaitingSinceUtc ?? job.CreatedAtUtc) >= options.SendDelay)).ToArray();
            if (eligible.Length == 0)
            {
                continue;
            }

            if (await jobs.HasLeftWaitingSinceAsync(providerId, now - options.Quiescence, cancellationToken))
            {
                continue; // the provider is actively draining its queue -- stay quiet.
            }

            var provider = await providers.FindAsync(providerId, cancellationToken);
            if (provider is null)
            {
                continue;
            }

            var alert = new ProviderInteractionAlert(providerId, provider.ProviderId, provider.DisplayName, now);
            var admins = await accounts.ListActiveAdminsAsync(cancellationToken);
            var recipientAdded = false;
            foreach (var admin in admins)
            {
                var roomId = await matrixDestinations.GetVerifiedRoomIdAsync(admin.Id, cancellationToken);
                if (roomId is null)
                {
                    continue;
                }

                alert.AddRecipient(admin.Id);
                recipientAdded = true;
            }

            if (!recipientAdded)
            {
                continue; // No verified admin -- nothing to send, so no alert is created.
            }

            alerts.Add(alert);
            if (!await alerts.TrySaveChangesAsync(cancellationToken))
            {
                // Lost the race against a concurrent pass to the partial unique
                // index (HUMAN-ACQ-1 D5) -- another alert already exists for this
                // provider. Skip; this pass's work for this provider is done.
                continue;
            }

            created.Add(alert);
        }

        return created;
    }

    private async Task DeliverAsync(
        List<ProviderInteractionAlert> workingSet, IReadOnlyList<ProviderAcquisitionJob> waitingJobs,
        Dictionary<Guid, ProviderInteractionClaim> claimsByJob, MatrixSettings matrixSettings,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var alert in workingSet)
        {
            foreach (var recipient in alert.Recipients.ToArray())
            {
                var deliverable = recipient.DeliveryState is ProviderInteractionRecipientDeliveryState.Pending or
                    ProviderInteractionRecipientDeliveryState.HeldQuietHours ||
                    (recipient.DeliveryState == ProviderInteractionRecipientDeliveryState.Failed &&
                        recipient.SendAttempts < options.MaxSendAttempts);
                if (!deliverable)
                {
                    continue;
                }

                var quietHours = await accounts.GetQuietHoursAsync(recipient.UserId, cancellationToken);
                if (quietHours?.IsQuietAt(now) == true)
                {
                    recipient.Hold();
                    await alerts.SaveChangesAsync(cancellationToken);
                    continue;
                }

                var roomId = await matrixDestinations.GetVerifiedRoomIdAsync(recipient.UserId, cancellationToken);
                if (roomId is null)
                {
                    recipient.RecordSendFailure("The recipient no longer has a verified Matrix link.");
                    await alerts.SaveChangesAsync(cancellationToken);
                    continue;
                }

                var accessToken = protector.Unprotect(
                    CommunicationSecretPurposes.MatrixAccessToken, matrixSettings.ProtectedAccessToken!,
                    matrixSettings.AccessTokenFormatVersion);
                if (accessToken is null)
                {
                    recipient.RecordSendFailure("The stored Matrix access token could not be decrypted.");
                    await alerts.SaveChangesAsync(cancellationToken);
                    continue;
                }

                // A not-yet-sent recipient always gets the "Open" verify-link
                // message: CloseAlertsAsync/MaintainClaimsAsync above already
                // Skip every still-Pending/HeldQuietHours recipient of an alert
                // they close or supersede within this same pass, so a recipient
                // reaching here still belongs to a genuinely Open alert -- except
                // for the rare cross-request race where a real claim lands mid-pass,
                // which the next pass's edit step self-corrects.
                var token = tokenGenerator.CreateToken();
                var (plain, html) = await BuildMessageAsync(
                    AlertMessageState.Open, alert, waitingJobs, claimsByJob, token, cancellationToken);

                var sendResult = await matrixClient.SendRichMessageAsync(
                    matrixSettings, accessToken, roomId, plain, html, cancellationToken);
                if (sendResult.Succeeded)
                {
                    recipient.RecordSendSuccess(tokenGenerator.Hash(token), roomId, sendResult.EventId, now);
                    recipient.RecordRenderedState(AlertMessageState.Open.ToString());
                }
                else
                {
                    recipient.RecordSendFailure(sendResult.Error);
                }

                await alerts.SaveChangesAsync(cancellationToken);
            }
        }
    }

    private async Task EditAsync(
        List<ProviderInteractionAlert> workingSet, MatrixSettings matrixSettings,
        CancellationToken cancellationToken)
    {
        foreach (var alert in workingSet)
        {
            foreach (var recipient in alert.Recipients.ToArray())
            {
                if (recipient.DeliveryState != ProviderInteractionRecipientDeliveryState.Sent ||
                    string.IsNullOrEmpty(recipient.EventId) || string.IsNullOrEmpty(recipient.RoomId))
                {
                    continue;
                }

                var state = DescribeState(alert, recipient);
                if (recipient.RenderedState == state.ToString())
                {
                    continue;
                }

                var accessToken = protector.Unprotect(
                    CommunicationSecretPurposes.MatrixAccessToken, matrixSettings.ProtectedAccessToken!,
                    matrixSettings.AccessTokenFormatVersion);
                if (accessToken is null)
                {
                    continue;
                }

                var (plain, html) = await BuildMessageAsync(state, alert, [], [], token: null, cancellationToken);
                var editResult = await matrixClient.EditMessageAsync(
                    matrixSettings, accessToken, recipient.RoomId!, recipient.EventId!, plain, html, cancellationToken);
                if (editResult.Succeeded)
                {
                    recipient.RecordRenderedState(state.ToString());
                    await alerts.SaveChangesAsync(cancellationToken);
                }
            }
        }
    }

    private static void CloseRecipients(ProviderInteractionAlert alert, DateTimeOffset now)
    {
        foreach (var recipient in alert.Recipients)
        {
            recipient.MarkSkipped();
            recipient.RevokeToken(now);
        }
    }

    /// <summary>"Needs a person": waiting, unexpired, and not currently held by a lease-live claim.</summary>
    private bool NeedsAPerson(ProviderAcquisitionJob job, Dictionary<Guid, ProviderInteractionClaim> claimsByJob) =>
        (job.InteractionExpiresAtUtc is not { } expires || expires > clock.UtcNow) &&
        !claimsByJob.ContainsKey(job.Id);

    private static AlertMessageState DescribeState(ProviderInteractionAlert alert, ProviderInteractionAlertRecipient recipient) =>
        alert.State switch
        {
            ProviderInteractionAlertState.Open => AlertMessageState.Open,
            ProviderInteractionAlertState.Claimed when alert.ClaimedByUserId == recipient.UserId => AlertMessageState.ClaimedSelf,
            ProviderInteractionAlertState.Claimed => AlertMessageState.ClaimedOther,
            ProviderInteractionAlertState.Resolved when alert.CloseReason == "verified" => AlertMessageState.ResolvedVerified,
            ProviderInteractionAlertState.Resolved when alert.CloseReason == "no-longer-needed" => AlertMessageState.ResolvedNoLongerNeeded,
            ProviderInteractionAlertState.Superseded => AlertMessageState.Superseded,
            _ => AlertMessageState.Ended // Resolved(cancelled/failed), Expired
        };

    private async Task<(string Plain, string Html)> BuildMessageAsync(
        AlertMessageState state, ProviderInteractionAlert alert, IReadOnlyList<ProviderAcquisitionJob> waitingJobs,
        Dictionary<Guid, ProviderInteractionClaim> claimsByJob, string? token, CancellationToken cancellationToken)
    {
        switch (state)
        {
            case AlertMessageState.Open:
                var candidates = waitingJobs
                    .Where(job => job.ExternalProviderId == alert.ExternalProviderId && NeedsAPerson(job, claimsByJob))
                    .OrderBy(job => job.WaitingSinceUtc ?? DateTimeOffset.MaxValue)
                    .ToArray();
                var first = candidates.FirstOrDefault();
                var extra = Math.Max(0, candidates.Length - 1);
                string? title = null;
                if (first is not null)
                {
                    var requestView = await requests.FindAdminViewAsync(first.RequestId, cancellationToken);
                    title = requestView?.Request.WorkTitle;
                }

                var link = $"{options.PublicOrigin}/interaction#{token}";
                var titleText = title is null ? "a book" : $"\"{title}\"";
                var moreText = extra > 0 ? $" and {extra} more" : "";
                return (
                    $"A book download needs a quick human check ({alert.ProviderDisplayName}). Waiting: {titleText}{moreText}. Verify now: {link}",
                    $"📚 A book download needs a quick human check ({Encode(alert.ProviderDisplayName)}). Waiting: {Encode(titleText)}{Encode(moreText)}. " +
                    $"<a href=\"{Encode(link)}\">Verify now</a> — this link works once.");

            case AlertMessageState.ClaimedOther:
                var claimer = alert.ClaimedByDisplayName ?? "Another administrator";
                return (
                    $"{claimer} is handling this verification. Nothing to do.",
                    $"✋ {Encode(claimer)} is handling this verification. Nothing to do.");

            case AlertMessageState.ClaimedSelf:
                return ("You're handling this verification.", "✋ You're handling this verification.");

            case AlertMessageState.ResolvedVerified:
                return ("Verified. Downloads are continuing.", "✅ Verified. Downloads are continuing.");

            case AlertMessageState.ResolvedNoLongerNeeded:
                return ("No longer needed.", "✅ No longer needed.");

            case AlertMessageState.Superseded:
                return (
                    "This link expired. A new one has been sent.",
                    "⌛ This link expired. A new one has been sent.");

            default:
                return ("This verification request has ended.", "⌛ This verification request has ended.");
        }
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}

/// <summary>Which of HUMAN-ACQ-1's fixed message variants a recipient's Matrix message should currently show.</summary>
public enum AlertMessageState
{
    Open,
    ClaimedSelf,
    ClaimedOther,
    ResolvedVerified,
    ResolvedNoLongerNeeded,
    Superseded,
    Ended
}
