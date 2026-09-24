using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Providers;
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
    ICredentialProtector protector,
    RemoteViewSessionRegistry viewSessions,
    IAuditWriter audit,
    IClock clock)
{
    public async Task<IReadOnlyList<ProviderInteractionView>> ListAsync(CancellationToken cancellationToken)
    {
        var waiting = await jobs.ListWaitingForInteractionAsync(cancellationToken);
        var registered = await providers.ListAsync(cancellationToken);
        var controls = registered.ToDictionary(provider => provider.Id, ProviderInteractionFeatures.SupportsInteractionControl);
        var views = registered.ToDictionary(provider => provider.Id, ProviderInteractionFeatures.SupportsInteractionView);

        return waiting.Select(job => ToView(
            job, controls.GetValueOrDefault(job.ExternalProviderId), views.GetValueOrDefault(job.ExternalProviderId)))
            .ToArray();
    }

    public Task<ProviderInteractionCommandResult> StartAsync(Guid jobId, CancellationToken cancellationToken) =>
        ControlAsync(jobId, ProviderInteractionCommand.Start, cancellationToken);

    public Task<ProviderInteractionCommandResult> UseFallbackAsync(Guid jobId, CancellationToken cancellationToken) =>
        ControlAsync(jobId, ProviderInteractionCommand.UseFallback, cancellationToken);

    public Task<ProviderInteractionCommandResult> CancelAsync(Guid jobId, CancellationToken cancellationToken) =>
        ControlAsync(jobId, ProviderInteractionCommand.Cancel, cancellationToken);

    private async Task<ProviderInteractionCommandResult> ControlAsync(
        Guid jobId, ProviderInteractionCommand command, CancellationToken cancellationToken)
    {
        var job = await jobs.FindAsync(jobId, cancellationToken);
        if (job is null)
        {
            return ProviderInteractionCommandResult.NotFound;
        }

        if (job.LifecycleState != ProviderAcquisitionJobLifecycleState.Waiting ||
            string.IsNullOrWhiteSpace(job.InteractionType) ||
            string.IsNullOrWhiteSpace(job.ProviderJobId))
        {
            return ProviderInteractionCommandResult.NotWaiting;
        }

        if (command != ProviderInteractionCommand.Cancel &&
            job.InteractionExpiresAtUtc is { } expiresAt && expiresAt <= clock.UtcNow)
        {
            await audit.WriteAsync(
                AuditActions.ProviderInteractionExpired, AuditSubjectTypes.ProviderInteraction, job.Id.ToString(),
                new { job.Id, job.RequestId, job.RequestFormatId, job.ProviderId, Command = command.ToString() },
                cancellationToken);
            return ProviderInteractionCommandResult.Expired;
        }

        var provider = await providers.FindAsync(job.ExternalProviderId, cancellationToken);
        if (provider is null)
        {
            return ProviderInteractionCommandResult.ProviderUnavailable;
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
                await audit.WriteAsync(
                    AuditActions.ProviderInteractionCancelled, AuditSubjectTypes.ProviderInteraction, job.Id.ToString(),
                    new { job.Id, job.RequestId, job.RequestFormatId, job.ProviderId }, cancellationToken);
                return ProviderInteractionCommandResult.Success;
            }

            if (!ProviderInteractionFeatures.SupportsInteractionControl(provider))
            {
                return ProviderInteractionCommandResult.Unsupported;
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
            return ProviderInteractionCommandResult.Success;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or NotSupportedException)
        {
            return ProviderInteractionCommandResult.ProviderUnavailable;
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

    private ProviderInteractionView ToView(ProviderAcquisitionJob job, bool supportsControl, bool supportsView)
    {
        var isExpired = job.InteractionExpiresAtUtc is { } expiresAt && expiresAt <= clock.UtcNow;
        var canViewNow = supportsView && !isExpired && job.InteractionViewSessionStartedAtUtc is not null;

        return new(
            job.Id, job.RequestId, job.RequestFormatId, job.ProviderId, job.InteractionType!, job.InteractionMessage,
            job.InteractionExpiresAtUtc, job.InteractionResumeSupported ?? false,
            isExpired, supportsControl, supportsControl, true, canViewNow);
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
    bool CanViewNow);

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
    ProviderUnavailable
}
