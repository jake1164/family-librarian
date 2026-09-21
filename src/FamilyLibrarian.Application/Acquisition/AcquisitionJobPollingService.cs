using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Audit;
using FamilyLibrarian.Domain.Requests;

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
    IProviderAttemptRepository attempts,
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
            await RecordFailureAsync(
                job, "PROVIDER_INTERNAL_ERROR", "The provider registration no longer exists.",
                retryable: false, retryAfterSeconds: null, detailsJson: null, cancellationToken);
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
            await RecordFailureAsync(
                job, status.Error?.Code, status.Error?.Message, status.Error?.Retryable,
                status.Error?.RetryAfterSeconds, status.Error?.DetailsJson, cancellationToken);
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
        var request = await requests.FindRequestForAdminAsync(job.RequestId, cancellationToken);
        var format = request?.Formats.FirstOrDefault(candidate => candidate.Id == job.RequestFormatId);
        if (request is null || format is null)
        {
            await RecordFailureAsync(
                job, "PROVIDER_INTERNAL_ERROR", "The originating request or format no longer exists.",
                retryable: false, retryAfterSeconds: null, detailsJson: null, cancellationToken);
            return;
        }

        var outputs = await client.ListOutputsAsync(
            provider.BaseUrl, apiKey, job.ProviderJobId!, resolution.Route!, cancellationToken);
        var primary = SelectPrimaryOutput(outputs, format.MediaType);

        if (primary is null)
        {
            await RecordFailureAsync(
                job, "CONTENT_UNAVAILABLE",
                $"The provider reported completion but returned no fetchable {format.MediaType} file output.",
                retryable: false, retryAfterSeconds: null, detailsJson: null, cancellationToken);
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

        if (stageResult.Outcome != ManualImportOutcome.Success)
        {
            // The provider genuinely finished and handed back a file, but
            // Family Librarian's own staging pipeline rejected it (wrong file
            // type for this format, a duplicate, an oversized file, ...).
            // That is a real, actionable failure -- recording it as Completed
            // here would bury it exactly like the bug this replaces: it drops
            // out of every admin-facing progress query the same way a
            // never-attempted request does (RequestFormatProgress only shows
            // a non-terminal job), leaving no trace anything was ever tried.
            await RecordFailureAsync(
                job, "STAGING_REJECTED", stageResult.Error ?? "The fetched file could not be staged.",
                retryable: false, retryAfterSeconds: null, detailsJson: null, cancellationToken);
            return;
        }

        job.ApplyStatus(
            status.State, status.Phase, null, null, null, null, null, null, null, null, null,
            nextPollAtUtc: null, clock.UtcNow);
        await jobs.SaveChangesAsync(cancellationToken);

        foreach (var assetId in stageResult.MediaAssetIds)
        {
            await securityPipeline.EvaluateAsync(assetId, cancellationToken);
        }
    }

    /// <summary>
    /// Protocol v2 §8a's output <c>role</c> is an open string, but "ebook"
    /// and "audio-part" are explicitly documented as media-type-specific
    /// (docs/04-external-provider-http-protocol.md §8a). A provider tagging
    /// its only file output with the wrong one of those for what was actually
    /// requested (e.g. handing back an ebook file, correctly labeled "ebook",
    /// for an Audiobook acquisition) must not be accepted as this format's
    /// primary output just because the role string matches something —
    /// staging it as this format's own file fails the extension check with no
    /// visible trace once <see cref="CompleteAsync"/> stopped swallowing that
    /// as a silent success.
    /// </summary>
    private static ExternalProviderOutput? SelectPrimaryOutput(
        IReadOnlyList<ExternalProviderOutput> outputs, RequestMediaType mediaType)
    {
        var eligible = outputs.Where(output => output.Role switch
        {
            "ebook" => mediaType == RequestMediaType.Ebook,
            "audio-part" => mediaType == RequestMediaType.Audiobook,
            _ => true
        }).ToArray();

        return eligible.FirstOrDefault(output => output.Role is "primary" or "ebook" or "audio-part")
            ?? eligible.FirstOrDefault(output => output.Kind == ProviderOutputKind.File);
    }

    /// <summary>
    /// Every terminal job failure discovered during background polling used
    /// to be recorded only on the job row itself -- invisible to an admin
    /// unless they queried the database directly, unlike the manual "get
    /// free copy" endpoint's own failures, which already land in the
    /// "Provider activity" ledger via <see cref="ProviderAttempt"/>. This is
    /// the one place every background failure now funnels through, so the
    /// two paths report failures the same way.
    /// </summary>
    private async Task RecordFailureAsync(
        ProviderAcquisitionJob job,
        string? errorCode,
        string? errorMessage,
        bool? retryable,
        int? retryAfterSeconds,
        string? detailsJson,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        job.RecordFailure(errorCode, errorMessage, retryable, retryAfterSeconds, detailsJson, now);
        attempts.Add(new ProviderAttempt(
            job.RequestId, job.RequestFormatId, job.ProviderId, ProviderAttemptOutcome.Failed,
            errorMessage ?? "The acquisition failed.", now, nextEligibleCheckAtUtc: null));
        await jobs.SaveChangesAsync(cancellationToken);
        await attempts.SaveChangesAsync(cancellationToken);
    }

    private void Reschedule(ProviderAcquisitionJob job, TimeSpan delay) =>
        job.ApplyStatus(
            job.LifecycleState, job.Phase, job.InteractionType, job.InteractionMessage, job.InteractionExpiresAtUtc,
            job.InteractionResumeSupported, job.InteractionActionUrl, job.ProgressPercent, job.ProgressBytesCompleted,
            job.ProgressBytesTotal, job.ProgressMessage, clock.UtcNow.Add(delay), clock.UtcNow);
}
