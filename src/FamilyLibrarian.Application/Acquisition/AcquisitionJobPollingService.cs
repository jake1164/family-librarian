using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Audit;

namespace FamilyLibrarian.Application.Acquisition;

/// <summary>
/// Drives every durable <see cref="ProviderAcquisitionJob"/> whose next poll
/// has come due (protocol v2 §8) — the replacement for the old in-request
/// blocking poll loop that used to live inside <c>ExternalProviderClient.AcquireAsync</c>.
/// Restart-safe by construction: a job row persists independently of the
/// process that submitted it, so a fresh pass after a Family Librarian
/// restart picks up exactly where the last one left off.
/// </summary>
public sealed class AcquisitionJobPollingService(
    IProviderAcquisitionJobStore jobs,
    IExternalProviderStore externalProviders,
    IExternalProviderClient client,
    IRequestRepository requests,
    AcquisitionStagingService staging,
    AutomatedSecurityPipeline securityPipeline,
    ICredentialProtector protector,
    PrivateEgressRouteResolver routeResolver,
    IClock clock)
{
    private const int BatchSize = 25;

    public async Task<int> ProcessDueAsync(CancellationToken cancellationToken)
    {
        var due = await jobs.ListDueForPollAsync(clock.UtcNow, BatchSize, cancellationToken);
        foreach (var job in due)
        {
            await ProcessOneAsync(job, cancellationToken);
        }

        return due.Count;
    }

    private async Task ProcessOneAsync(ProviderAcquisitionJob job, CancellationToken cancellationToken)
    {
        var provider = await externalProviders.FindAsync(job.ExternalProviderId, cancellationToken);
        if (provider is null)
        {
            job.RecordFailure(
                "PROVIDER_INTERNAL_ERROR", "The provider registration no longer exists.",
                retryable: false, retryAfterSeconds: null, detailsJson: null, clock.UtcNow);
            await jobs.SaveChangesAsync(cancellationToken);
            return;
        }

        var resolution = routeResolver.Resolve(provider.EffectiveEgressPolicy);
        if (!resolution.IsAllowed)
        {
            // The gateway may recover later -- this is a retry condition, not
            // a permanent failure of the job itself.
            Reschedule(job, TimeSpan.FromMinutes(5));
            await jobs.SaveChangesAsync(cancellationToken);
            return;
        }

        var apiKey = provider.HasApiKey
            ? protector.Unprotect(
                ExternalProviderSecretPurposes.ApiKey, provider.ProtectedApiKey!, provider.ApiKeyFormatVersion)
            : null;

        ExternalProviderJobStatus status;
        try
        {
            status = await client.GetAcquireStatusAsync(
                provider.BaseUrl, apiKey, job.ProviderJobId!, resolution.Route!, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            // Transient -- try again shortly rather than failing the job
            // outright on a single flaky poll.
            Reschedule(job, TimeSpan.FromSeconds(30));
            await jobs.SaveChangesAsync(cancellationToken);
            return;
        }

        if (status.State == ProviderAcquisitionJobLifecycleState.Failed)
        {
            job.RecordFailure(
                status.Error?.Code, status.Error?.Message, status.Error?.Retryable, status.Error?.RetryAfterSeconds,
                status.Error?.DetailsJson, clock.UtcNow);
            await jobs.SaveChangesAsync(cancellationToken);
            return;
        }

        if (status.State != ProviderAcquisitionJobLifecycleState.Completed)
        {
            job.ApplyStatus(
                status.State,
                status.Phase,
                status.Interaction?.Type,
                status.Interaction?.Message,
                status.Interaction?.ExpiresAtUtc,
                status.Interaction?.ResumeSupported,
                status.Interaction?.ActionUrl,
                status.Progress?.Percent,
                status.Progress?.BytesCompleted,
                status.Progress?.BytesTotal,
                status.Progress?.Message,
                clock.UtcNow.AddSeconds(status.PollAfterSeconds ?? 2),
                clock.UtcNow);
            await jobs.SaveChangesAsync(cancellationToken);
            return;
        }

        await CompleteAsync(job, provider, apiKey, resolution, status, cancellationToken);
    }

    private async Task CompleteAsync(
        ProviderAcquisitionJob job,
        Domain.Providers.ExternalProvider provider,
        string? apiKey,
        EgressResolution resolution,
        ExternalProviderJobStatus status,
        CancellationToken cancellationToken)
    {
        var outputs = await client.ListOutputsAsync(
            provider.BaseUrl, apiKey, job.ProviderJobId!, resolution.Route!, cancellationToken);
        var primary = outputs.FirstOrDefault(output => output.Role is "primary" or "ebook")
            ?? outputs.FirstOrDefault(output => output.Kind == ProviderOutputKind.File);

        if (primary is null)
        {
            job.RecordFailure(
                "CONTENT_UNAVAILABLE", "The provider reported completion but returned no fetchable file output.",
                retryable: false, retryAfterSeconds: null, detailsJson: null, clock.UtcNow);
            await jobs.SaveChangesAsync(cancellationToken);
            return;
        }

        var request = await requests.FindRequestForAdminAsync(job.RequestId, cancellationToken);
        var format = request?.Formats.FirstOrDefault(candidate => candidate.Id == job.RequestFormatId);
        if (request is null || format is null)
        {
            job.RecordFailure(
                "PROVIDER_INTERNAL_ERROR", "The originating request or format no longer exists.",
                retryable: false, retryAfterSeconds: null, detailsJson: null, clock.UtcNow);
            await jobs.SaveChangesAsync(cancellationToken);
            return;
        }

        ManualImportResult stageResult;
        var artifact = await client.GetOutputAsync(
            provider.BaseUrl, apiKey, job.ProviderJobId!, primary.OutputId, resolution.Route!, cancellationToken);
        await using (var content = artifact.Content)
        {
            stageResult = await staging.StageAsync(
                request, format, content, artifact.Filename, provider.ProviderId,
                AuditActions.ExternalProviderAcquisitionStaged,
                candidateTitle: null, candidateAuthor: null, cancellationToken,
                egressPolicy: provider.EffectiveEgressPolicy);
        }

        foreach (var output in outputs)
        {
            job.AddOutput(
                output.OutputId, output.Kind, output.Role, output.Filename, output.ContentType, output.SizeBytes,
                output.Uri, output.UriScheme, output.ChecksumsJson, output.RetentionExpiresAtUtc, clock.UtcNow);
        }

        job.ApplyStatus(
            status.State, status.Phase, null, null, null, null, null, null, null, null, null,
            nextPollAtUtc: null, clock.UtcNow);
        await jobs.SaveChangesAsync(cancellationToken);

        if (stageResult.Outcome == ManualImportOutcome.Success)
        {
            foreach (var assetId in stageResult.MediaAssetIds)
            {
                await securityPipeline.EvaluateAsync(assetId, cancellationToken);
            }
        }
    }

    private void Reschedule(ProviderAcquisitionJob job, TimeSpan delay) =>
        job.ApplyStatus(
            job.LifecycleState, job.Phase, job.InteractionType, job.InteractionMessage, job.InteractionExpiresAtUtc,
            job.InteractionResumeSupported, job.InteractionActionUrl, job.ProgressPercent, job.ProgressBytesCompleted,
            job.ProgressBytesTotal, job.ProgressMessage, clock.UtcNow.Add(delay), clock.UtcNow);
}
