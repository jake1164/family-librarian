using System.Net;
using System.Text.Json;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Catalog;
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
    IProviderAttemptRepository attempts,
    AutomaticRequestFulfillmentService automaticFulfillment,
    AcquisitionStagingService staging,
    AutomatedSecurityPipeline securityPipeline,
    ExternalProviderOutputPolicy outputPolicy,
    ManualImportPolicy importPolicy,
    ICredentialProtector protector,
    IAuditWriter audit,
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
        // Captured before any status mutation below, purely to decide whether
        // a terminal outcome discovered on this pass also closes out a human
        // verification -- distinct from and narrower than "this job just
        // finished," which happens on every ordinary poll.
        var wasWaitingForInteraction = job.LifecycleState == ProviderAcquisitionJobLifecycleState.Waiting &&
            !string.IsNullOrWhiteSpace(job.InteractionType);

        var provider = await externalProviders.FindAsync(job.ExternalProviderId, cancellationToken);
        if (provider is null)
        {
            await RecordFailureAsync(
                job, "PROVIDER_INTERNAL_ERROR", "The provider registration no longer exists.",
                retryable: false, retryAfterSeconds: null, detailsJson: null, wasWaitingForInteraction, cancellationToken);
            return;
        }

        var apiKey = provider.HasApiKey
            ? protector.Unprotect(
                ExternalProviderSecretPurposes.ApiKey, provider.ProtectedApiKey!, provider.ApiKeyFormatVersion)
            : null;

        if (job.ProviderJobId is null)
        {
            var request = await requests.FindRequestForAdminAsync(job.RequestId, cancellationToken);
            var format = request?.Formats.FirstOrDefault(candidate => candidate.Id == job.RequestFormatId);
            if (format is null)
            {
                await RecordFailureAsync(job, "PROVIDER_INTERNAL_ERROR", "The originating request or format no longer exists.",
                    false, null, null, wasWaitingForInteraction, cancellationToken);
                return;
            }

            try
            {
                var submission = await client.SubmitAcquireAsync(provider.BaseUrl, apiKey,
                    new ExternalAcquireRequest(job.AcquireRequestId, job.CandidateReference, job.CandidateRevision,
                        job.AcquireToken, format.MediaType), job.IdempotencyKey, cancellationToken);
                if (submission.Outcome == ProviderAcquireOutcome.CandidateChanged)
                {
                    await RecordFailureAsync(job, "CANDIDATE_CHANGED", "The candidate changed before acquisition.",
                        false, null, null, wasWaitingForInteraction, cancellationToken);
                    return;
                }
                try
                {
                    job.RecordSubmission(submission.JobId!, submission.State!.Value,
                        clock.UtcNow.AddSeconds(submission.PollAfterSeconds ?? 2), clock.UtcNow);
                }
                catch (InvalidOperationException exception)
                {
                    await RecordFailureAsync(job, "IDEMPOTENCY_CONFLICT", exception.Message, false, null, null,
                        wasWaitingForInteraction, cancellationToken);
                    return;
                }
            }
            catch (ExternalProviderSubmissionConflictException exception)
            {
                await RecordFailureAsync(job, "IDEMPOTENCY_CONFLICT", exception.Message, false, null, null,
                    wasWaitingForInteraction, cancellationToken);
                return;
            }
            catch (Exception exception) when (exception is HttpRequestException or TimeoutException or TaskCanceledException)
            {
                Reschedule(job, TimeSpan.FromSeconds(30));
            }
            await jobs.SaveChangesAsync(cancellationToken);
            return;
        }

        ExternalProviderJobStatus status;
        try
        {
            status = await client.GetAcquireStatusAsync(
                provider.BaseUrl, apiKey, job.ProviderJobId!, cancellationToken);
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
                status.Error?.RetryAfterSeconds, status.Error?.DetailsJson, wasWaitingForInteraction, cancellationToken);
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

        await CompleteAsync(job, provider, apiKey, status, wasWaitingForInteraction, cancellationToken);
    }

    private async Task CompleteAsync(
        ProviderAcquisitionJob job,
        Domain.Providers.ExternalProvider provider,
        string? apiKey,
        ExternalProviderJobStatus status,
        bool wasWaitingForInteraction,
        CancellationToken cancellationToken)
    {
        var request = await requests.FindRequestForAdminAsync(job.RequestId, cancellationToken);
        var format = request?.Formats.FirstOrDefault(candidate => candidate.Id == job.RequestFormatId);
        if (request is null || format is null)
        {
            await RecordFailureAsync(
                job, "PROVIDER_INTERNAL_ERROR", "The originating request or format no longer exists.",
                retryable: false, retryAfterSeconds: null, detailsJson: null, wasWaitingForInteraction, cancellationToken);
            return;
        }

        // Staging persists the local job ID and every output-to-asset link in
        // the same database save. If the process stopped after staging or
        // remote DELETE, resume from those durable links without asking the
        // provider for an output list that may already have been purged.
        var alreadyStagedAssetIds = job.Outputs.Where(output => output.MediaAssetId.HasValue)
            .Select(output => output.MediaAssetId!.Value).ToArray();
        if (job.LocalAcquisitionJobId is not null && alreadyStagedAssetIds.Length > 0)
        {
            if (!await client.TryDeleteAcquireAsync(provider.BaseUrl, apiKey, job.ProviderJobId!, cancellationToken))
            {
                Reschedule(job, TimeSpan.FromMinutes(5));
                await jobs.SaveChangesAsync(cancellationToken);
                return;
            }

            await EvaluateAssetsAsync(alreadyStagedAssetIds, cancellationToken);
            job.ApplyStatus(status.State, status.Phase, null, null, null, null, null, null, null, null, null,
                nextPollAtUtc: null, clock.UtcNow);
            await jobs.SaveChangesAsync(cancellationToken);
            if (wasWaitingForInteraction)
                await audit.WriteAsync(AuditActions.ProviderInteractionCompleted, AuditSubjectTypes.ProviderInteraction,
                    job.Id.ToString(), new { job.Id, job.RequestId, job.RequestFormatId, job.ProviderId }, cancellationToken);
            return;
        }

        IReadOnlyList<ExternalProviderOutput> outputs;
        try
        {
            outputs = await client.ListOutputsAsync(provider.BaseUrl, apiKey, job.ProviderJobId!, cancellationToken);
        }
        catch (HttpRequestException exception) when (exception.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            await RecordFailureAsync(job, "CONTENT_UNAVAILABLE", "The provider's completed outputs are no longer available.",
                false, null, null, wasWaitingForInteraction, cancellationToken);
            return;
        }
        catch (HttpRequestException exception) when (exception.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            await RecordFailureAsync(job, "CONTENT_UNAVAILABLE", "A required provider output is no longer available.",
                false, null, null, wasWaitingForInteraction, cancellationToken);
            return;
        }
        catch (HttpRequestException)
        {
            Reschedule(job, TimeSpan.FromSeconds(30));
            await jobs.SaveChangesAsync(cancellationToken);
            return;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            await RecordFailureAsync(job, "CONTENT_UNAVAILABLE", "The provider returned malformed output metadata.",
                false, null, null, wasWaitingForInteraction, cancellationToken);
            return;
        }
        IReadOnlyList<ExternalProviderOutput> selected;
        try
        {
            selected = ExternalProviderOutputSelector.Select(outputs, format.MediaType,
                importPolicy, outputPolicy);
            var declaredTotal = 0L;
            if (outputs.Any(output =>
                    ExternalProviderOutputSelector.NormalizeFilename(output.Filename) is { } filename &&
                    (filename.Length > outputPolicy.MaxFilenameLength || filename.Any(char.IsControl))))
                throw new InvalidExternalProviderOutputException("The provider returned an invalid filename.");
            foreach (var output in outputs)
            {
                if (output.SizeBytes is < 0 || output.SizeBytes > outputPolicy.MaxFileBytes)
                    throw new InvalidExternalProviderOutputException("The provider output metadata exceeds configured size limits.");
                if (output.SizeBytes.HasValue)
                {
                    declaredTotal = checked(declaredTotal + output.SizeBytes.Value);
                    if (declaredTotal > outputPolicy.MaxJobBytes)
                        throw new InvalidExternalProviderOutputException("The provider output metadata exceeds configured size limits.");
                }
            }
        }
        catch (InvalidExternalProviderOutputException exception)
        {
            await RecordFailureAsync(job, "CONTENT_UNAVAILABLE", exception.Message, false, null, null,
                wasWaitingForInteraction, cancellationToken);
            return;
        }

        foreach (var output in outputs)
        {
            job.AddOutput(
                output.OutputId, output.Kind, output.Role, output.Filename, output.ContentType, output.SizeBytes,
                output.Uri, output.UriScheme, output.ChecksumsJson, output.RetentionExpiresAtUtc, clock.UtcNow, output.Sequence);
        }

        ManualImportResult stageResult;
        var budget = new ExternalProviderOutputTransferBudget(outputPolicy.MaxJobBytes);
        async IAsyncEnumerable<DirectAcquisitionFile> ReadSelectedOutputs(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            foreach (var output in selected)
            {
                var artifact = await client.GetOutputAsync(provider.BaseUrl, apiKey, job.ProviderJobId!, output.OutputId, token);
                var listedFilename = ExternalProviderOutputSelector.NormalizeFilename(output.Filename);
                var responseFilename = ExternalProviderOutputSelector.NormalizeFilename(artifact.Filename);
                var listedExtension = Path.GetExtension(listedFilename);
                var responseExtension = Path.GetExtension(responseFilename);
                if (!string.IsNullOrEmpty(listedExtension) && !string.IsNullOrEmpty(responseExtension) &&
                    !listedExtension.Equals(responseExtension, StringComparison.OrdinalIgnoreCase))
                {
                    await artifact.Content.DisposeAsync();
                    throw new InvalidExternalProviderOutputException("The provider output filename did not match its response filename.");
                }
                var filename = listedFilename.Length == 0 ? responseFilename : listedFilename;
                if (filename.Length == 0 || filename.Length > outputPolicy.MaxFilenameLength || filename.Any(char.IsControl))
                {
                    await artifact.Content.DisposeAsync();
                    throw new InvalidExternalProviderOutputException("The provider returned an invalid filename.");
                }
                var stream = new ExternalProviderOutputStream(artifact.Content, output, budget, outputPolicy);
                yield return new DirectAcquisitionFile(stream, filename, output.OutputId, outputPolicy.MaxFileBytes,
                    _ => { stream.ValidateComplete(output); return Task.CompletedTask; });
            }
        }
        try
        {
            stageResult = await staging.StageExternalBundleAsync(request, format,
                ReadSelectedOutputs(cancellationToken), provider.ProviderId,
                AuditActions.ExternalProviderAcquisitionStaged, job, cancellationToken);
        }
        catch (TimeoutException)
        {
            Reschedule(job, TimeSpan.FromSeconds(30));
            await jobs.SaveChangesAsync(cancellationToken);
            return;
        }
        catch (HttpRequestException)
        {
            Reschedule(job, TimeSpan.FromSeconds(30));
            await jobs.SaveChangesAsync(cancellationToken);
            return;
        }
        catch (InvalidExternalProviderOutputException exception)
        {
            await RecordFailureAsync(job, "CONTENT_UNAVAILABLE", exception.Message, false, null, null,
                wasWaitingForInteraction, cancellationToken);
            return;
        }
        catch (JsonException)
        {
            await RecordFailureAsync(job, "CONTENT_UNAVAILABLE", "The provider returned malformed output metadata.",
                false, null, null, wasWaitingForInteraction, cancellationToken);
            return;
        }
        catch (OverflowException)
        {
            await RecordFailureAsync(job, "CONTENT_UNAVAILABLE", "The provider output metadata exceeds configured size limits.",
                false, null, null, wasWaitingForInteraction, cancellationToken);
            return;
        }

        if (stageResult.Outcome == ManualImportOutcome.WaitingForSecurityScanner)
        {
            Reschedule(job, TimeSpan.FromMinutes(2));
            await jobs.SaveChangesAsync(cancellationToken);
            return;
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
                retryable: false, retryAfterSeconds: null, detailsJson: null, wasWaitingForInteraction, cancellationToken);
            return;
        }

        if (!await client.TryDeleteAcquireAsync(provider.BaseUrl, apiKey, job.ProviderJobId!, cancellationToken))
        {
            Reschedule(job, TimeSpan.FromMinutes(5));
            await jobs.SaveChangesAsync(cancellationToken);
            return;
        }

        await EvaluateAssetsAsync(stageResult.MediaAssetIds, cancellationToken);

        job.ApplyStatus(
            status.State, status.Phase, null, null, null, null, null, null, null, null, null,
            nextPollAtUtc: null, clock.UtcNow);
        await jobs.SaveChangesAsync(cancellationToken);

        if (wasWaitingForInteraction)
        {
            await audit.WriteAsync(
                AuditActions.ProviderInteractionCompleted, AuditSubjectTypes.ProviderInteraction, job.Id.ToString(),
                new { job.Id, job.RequestId, job.RequestFormatId, job.ProviderId }, cancellationToken);
        }

    }

    private async Task EvaluateAssetsAsync(IReadOnlyList<Guid> assetIds, CancellationToken cancellationToken)
    {
        if (assetIds.Count == 1)
            await securityPipeline.EvaluateAsync(assetIds[0], cancellationToken);
        else
            await securityPipeline.EvaluateBundleAsync(assetIds, cancellationToken);
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
        bool wasWaitingForInteraction,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var candidateChanged = string.Equals(errorCode, "CANDIDATE_CHANGED", StringComparison.Ordinal);
        var alreadyRetriedStaleCandidate = candidateChanged &&
            (await attempts.ListForRequestAsync(job.RequestId, cancellationToken)).Any(attempt =>
                attempt.RequestFormatId == job.RequestFormatId &&
                string.Equals(attempt.ProviderId, job.ProviderId, StringComparison.OrdinalIgnoreCase) &&
                attempt.Summary.StartsWith("CANDIDATE_CHANGED:", StringComparison.Ordinal));
        job.RecordFailure(errorCode, errorMessage, retryable, retryAfterSeconds, detailsJson, now);
        attempts.Add(new ProviderAttempt(
            job.RequestId, job.RequestFormatId, job.ProviderId, ProviderAttemptOutcome.Failed,
            candidateChanged ? $"CANDIDATE_CHANGED: {errorMessage ?? "The candidate changed upstream."}" :
                errorMessage ?? "The acquisition failed.", now,
            nextEligibleCheckAtUtc: candidateChanged && !alreadyRetriedStaleCandidate ? now : null));
        await jobs.SaveChangesAsync(cancellationToken);
        await attempts.SaveChangesAsync(cancellationToken);

        if (job.IsAutomaticAcquisition && (!candidateChanged || alreadyRetriedStaleCandidate))
            await automaticFulfillment.RecordExternalAcquisitionFailureAsync(
                job.RequestId,
                candidateChanged
                    ? "The provider reported that this copy changed twice. A librarian must refresh the source before another automatic attempt."
                    : errorMessage ?? "The automatic copy could not be acquired.",
                cancellationToken);

        if (wasWaitingForInteraction)
        {
            await audit.WriteAsync(
                AuditActions.ProviderInteractionFailed, AuditSubjectTypes.ProviderInteraction, job.Id.ToString(),
                new { job.Id, job.RequestId, job.RequestFormatId, job.ProviderId, ErrorCode = errorCode },
                cancellationToken);
        }
    }

    private void Reschedule(ProviderAcquisitionJob job, TimeSpan delay) =>
        job.ApplyStatus(
            job.LifecycleState, job.Phase, job.InteractionType, job.InteractionMessage, job.InteractionExpiresAtUtc,
            job.InteractionResumeSupported, job.InteractionActionUrl, job.ProgressPercent, job.ProgressBytesCompleted,
            job.ProgressBytesTotal, job.ProgressMessage, clock.UtcNow.Add(delay), clock.UtcNow);
}
