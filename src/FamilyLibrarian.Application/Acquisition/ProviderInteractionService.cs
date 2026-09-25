using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Accounts;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Audit;

namespace FamilyLibrarian.Application.Acquisition;

/// <summary>
/// Administrator control plane for a provider's durable human-interaction
/// wait. This type never returns a provider URL or browser credential: the
/// later remote-view broker is responsible for the browser-facing leg.
/// </summary>
public sealed class ProviderInteractionService(
    IProviderAcquisitionJobStore jobs,
    IExternalProviderStore providers,
    IExternalProviderClient client,
    IRequestRepository requests,
    ICredentialProtector protector,
    RemoteViewSessionRegistry viewSessions,
    ProviderInteractionClaimService claimService,
    IProviderInteractionClaimStore claimStore,
    IUserAccountStore accounts,
    IAuditWriter audit,
    IClock clock)
{
    /// <summary>
    /// <paramref name="forUserId"/> is the viewer, used only to set
    /// <see cref="ProviderInteractionView.IsClaimedByCurrentUser"/> — pass
    /// <see langword="null"/> when there is no meaningful "current user" (e.g. a
    /// background caller).
    /// </summary>
    public async Task<IReadOnlyList<ProviderInteractionView>> ListAsync(Guid? forUserId, CancellationToken cancellationToken)
    {
        var waiting = await jobs.ListWaitingForInteractionAsync(cancellationToken);
        var registered = await providers.ListAsync(cancellationToken);
        var controls = registered.ToDictionary(provider => provider.Id, ProviderInteractionFeatures.SupportsInteractionControl);
        var views = registered.ToDictionary(provider => provider.Id, ProviderInteractionFeatures.SupportsInteractionView);
        var claimsByJob = (await claimStore.ListAsync(cancellationToken)).ToDictionary(claim => claim.JobId);

        // Sequential, not fanned out: every lookup below shares one
        // AppDbContext, which EF Core does not allow concurrent operations
        // on (same reasoning as AdminRequestEndpoints.GetAttentionAsync).
        // The waiting queue is small by construction -- it only ever holds
        // jobs actively parked on an administrator -- so this stays cheap.
        var result = new List<ProviderInteractionView>(waiting.Count);
        foreach (var job in waiting)
        {
            // Best-effort: a request deleted after its job started must not
            // hide the interaction itself, just its book/requester context.
            var requestView = await requests.FindAdminViewAsync(job.RequestId, cancellationToken);
            var claim = claimsByJob.GetValueOrDefault(job.Id);
            result.Add(await ToViewAsync(
                job, controls.GetValueOrDefault(job.ExternalProviderId), views.GetValueOrDefault(job.ExternalProviderId),
                requestView, claim, forUserId, cancellationToken));
        }

        return result;
    }

    /// <summary>Used by the request-detail page to show one request's own waiting interaction, if any.</summary>
    public async Task<ProviderInteractionView?> FindForRequestAsync(
        Guid requestId, Guid? forUserId, CancellationToken cancellationToken) =>
        (await ListAsync(forUserId, cancellationToken)).FirstOrDefault(interaction => interaction.RequestId == requestId);

    /// <summary>
    /// Starting a verification session also counts as a claim (HUMAN-ACQ-1 D7).
    /// <see cref="ProviderInteractionCommandOutcome.Result"/> is
    /// <see cref="ProviderInteractionCommandResult.ClaimedByAnother"/> when someone
    /// else already holds a live claim; the caller should offer "Take over" rather
    /// than retry.
    /// </summary>
    public Task<ProviderInteractionCommandOutcome> StartAsync(Guid jobId, Guid actingUserId, CancellationToken cancellationToken) =>
        ControlAsync(jobId, ProviderInteractionCommand.Start, actingUserId, cancellationToken);

    public Task<ProviderInteractionCommandOutcome> UseFallbackAsync(Guid jobId, CancellationToken cancellationToken) =>
        ControlAsync(jobId, ProviderInteractionCommand.UseFallback, actingUserId: null, cancellationToken);

    /// <summary>Cancel is deliberately not claim-gated: any admin may cancel a stuck interaction.</summary>
    public Task<ProviderInteractionCommandOutcome> CancelAsync(Guid jobId, CancellationToken cancellationToken) =>
        ControlAsync(jobId, ProviderInteractionCommand.Cancel, actingUserId: null, cancellationToken);

    private async Task<ProviderInteractionCommandOutcome> ControlAsync(
        Guid jobId, ProviderInteractionCommand command, Guid? actingUserId, CancellationToken cancellationToken)
    {
        var job = await jobs.FindAsync(jobId, cancellationToken);
        if (job is null)
        {
            return ProviderInteractionCommandOutcome.Of(ProviderInteractionCommandResult.NotFound);
        }

        if (job.LifecycleState != ProviderAcquisitionJobLifecycleState.Waiting ||
            string.IsNullOrWhiteSpace(job.InteractionType) ||
            string.IsNullOrWhiteSpace(job.ProviderJobId))
        {
            return ProviderInteractionCommandOutcome.Of(ProviderInteractionCommandResult.NotWaiting);
        }

        if (command != ProviderInteractionCommand.Cancel &&
            job.InteractionExpiresAtUtc is { } expiresAt && expiresAt <= clock.UtcNow)
        {
            await audit.WriteAsync(
                AuditActions.ProviderInteractionExpired, AuditSubjectTypes.ProviderInteraction, job.Id.ToString(),
                new { job.Id, job.RequestId, job.RequestFormatId, job.ProviderId, Command = command.ToString() },
                cancellationToken);
            return ProviderInteractionCommandOutcome.Of(ProviderInteractionCommandResult.Expired);
        }

        var provider = await providers.FindAsync(job.ExternalProviderId, cancellationToken);
        if (provider is null)
        {
            return ProviderInteractionCommandOutcome.Of(ProviderInteractionCommandResult.ProviderUnavailable);
        }

        if (command == ProviderInteractionCommand.Start && actingUserId is { } userId)
        {
            var claim = await claimService.TryClaimForUserAsync(
                jobId, userId, ProviderInteractionClaimChannel.InApp, alertId: null, cancellationToken);
            if (claim.Kind == ClaimOutcomeKind.HeldByOther)
            {
                return ProviderInteractionCommandOutcome.ClaimedByAnother(claim.HeldByDisplayName!);
            }
        }

        var apiKey = provider.HasApiKey
            ? protector.Unprotect(ExternalProviderSecretPurposes.ApiKey, provider.ProtectedApiKey!, provider.ApiKeyFormatVersion)
            : null;

        try
        {
            if (command == ProviderInteractionCommand.Cancel)
            {
                await client.CancelAcquireAsync(provider.BaseUrl, apiKey, job.ProviderJobId, cancellationToken);
                job.ApplyStatus(
                    ProviderAcquisitionJobLifecycleState.Cancelled, null, null, null, null, null, null,
                    null, null, null, null, nextPollAtUtc: null, clock.UtcNow);
                await jobs.SaveChangesAsync(cancellationToken);
                // Tear down a live remote-view session immediately rather than
                // waiting for its own next lifecycle-check tick to notice the
                // job left Waiting.
                viewSessions.RequestClose(job.Id);
                await claimService.ReleaseForJobAsync(job.Id, cancellationToken);
                await audit.WriteAsync(
                    AuditActions.ProviderInteractionCancelled, AuditSubjectTypes.ProviderInteraction, job.Id.ToString(),
                    new { job.Id, job.RequestId, job.RequestFormatId, job.ProviderId }, cancellationToken);
                return ProviderInteractionCommandOutcome.Of(ProviderInteractionCommandResult.Success);
            }

            if (!ProviderInteractionFeatures.SupportsInteractionControl(provider))
            {
                return ProviderInteractionCommandOutcome.Of(ProviderInteractionCommandResult.Unsupported);
            }

            var status = command == ProviderInteractionCommand.Start
                ? await client.StartInteractionAsync(provider.BaseUrl, apiKey, job.ProviderJobId, cancellationToken)
                : await client.UseAcquireFallbackAsync(provider.BaseUrl, apiKey, job.ProviderJobId, cancellationToken);

            ApplyStatus(job, status);
            if (command == ProviderInteractionCommand.Start &&
                job.LifecycleState == ProviderAcquisitionJobLifecycleState.Waiting)
            {
                job.RecordInteractionSessionStarted(clock.UtcNow);
            }

            await jobs.SaveChangesAsync(cancellationToken);
            await audit.WriteAsync(
                command == ProviderInteractionCommand.Start
                    ? AuditActions.ProviderInteractionStarted
                    : AuditActions.ProviderInteractionFallbackSelected,
                AuditSubjectTypes.ProviderInteraction,
                job.Id.ToString(),
                new { job.Id, job.RequestId, job.RequestFormatId, job.ProviderId }, cancellationToken);
            return ProviderInteractionCommandOutcome.Of(ProviderInteractionCommandResult.Success);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or NotSupportedException)
        {
            return ProviderInteractionCommandOutcome.Of(ProviderInteractionCommandResult.ProviderUnavailable);
        }
    }

    private void ApplyStatus(ProviderAcquisitionJob job, ExternalProviderJobStatus status) =>
        job.ApplyStatus(
            status.State, status.Phase, status.Interaction?.Type, status.Interaction?.Message,
            status.Interaction?.ExpiresAtUtc, status.Interaction?.ResumeSupported, status.Interaction?.ActionUrl,
            status.Progress?.Percent, status.Progress?.BytesCompleted, status.Progress?.BytesTotal, status.Progress?.Message,
            status.State is ProviderAcquisitionJobLifecycleState.Completed or ProviderAcquisitionJobLifecycleState.Cancelled
                ? null
                : clock.UtcNow.AddSeconds(status.PollAfterSeconds ?? 2),
            clock.UtcNow);

    private async Task<ProviderInteractionView> ToViewAsync(
        ProviderAcquisitionJob job, bool supportsControl, bool supportsView, AdminBookRequestView? requestView,
        ProviderInteractionClaim? claim, Guid? forUserId, CancellationToken cancellationToken)
    {
        var isExpired = job.InteractionExpiresAtUtc is { } expiresAt && expiresAt <= clock.UtcNow;
        var canViewNow = supportsView && !isExpired && job.InteractionViewSessionStartedAtUtc is not null;

        var claimIsLive = claim is not null && claimService.IsLeaseLive(claim);
        var claimedByDisplayName = claimIsLive
            ? (await accounts.FindAsync(claim!.ClaimedByUserId, cancellationToken))?.DisplayName ?? "another administrator"
            : null;
        var isClaimedByCurrentUser = claimIsLive && forUserId is { } userId && claim!.ClaimedByUserId == userId;

        return new(
            job.Id, job.RequestId, job.RequestFormatId, job.ProviderId, job.InteractionType!, job.InteractionMessage,
            job.InteractionExpiresAtUtc, job.InteractionResumeSupported ?? false,
            isExpired, supportsControl, supportsControl, true, canViewNow,
            requestView?.Request.WorkTitle, requestView?.Request.Authors, requestView?.RequesterDisplayName,
            claimedByDisplayName, isClaimedByCurrentUser);
    }

}

public sealed record ProviderInteractionView(
    Guid ProviderAcquisitionJobId,
    Guid RequestId,
    Guid RequestFormatId,
    string ProviderId,
    string Type,
    string? Message,
    DateTimeOffset? ExpiresAtUtc,
    bool ResumeSupported,
    bool IsExpired,
    bool CanStart,
    bool CanUseFallback,
    bool CanCancel,
    bool CanViewNow,
    string? WorkTitle,
    IReadOnlyList<string>? Authors,
    string? RequesterDisplayName,
    string? ClaimedByDisplayName,
    bool IsClaimedByCurrentUser);

public enum ProviderInteractionCommand
{
    Start,
    UseFallback,
    Cancel
}

public enum ProviderInteractionCommandResult
{
    Success,
    NotFound,
    NotWaiting,
    Expired,
    Unsupported,
    ProviderUnavailable,
    /// <summary>Another administrator holds a live claim (HUMAN-ACQ-1 D7). See <see cref="ProviderInteractionCommandOutcome.ClaimedByDisplayName"/>.</summary>
    ClaimedByAnother
}

public sealed record ProviderInteractionCommandOutcome(ProviderInteractionCommandResult Result, string? ClaimedByDisplayName = null)
{
    public static ProviderInteractionCommandOutcome Of(ProviderInteractionCommandResult result) => new(result);

    public static ProviderInteractionCommandOutcome ClaimedByAnother(string displayName) =>
        new(ProviderInteractionCommandResult.ClaimedByAnother, displayName);
}
