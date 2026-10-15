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
    ExternalProviderOutputPolicy configuredOutputPolicy,
    IAssetStagingStore stagingStore,
    ManualImportPolicy importPolicy,
    ICredentialProtector protector,
    IAuditWriter audit,
    IClock clock,
    IAutomaticFulfillmentSignal? fulfillmentSignal = null,
    AudiobookPartSetFailureService? partSetFailures = null,
    ISecurityEvaluationRepository? assets = null)
{
    private const int BatchSize = 25;
    private static readonly TimeSpan DiskSpaceRecheckDelay = TimeSpan.FromMinutes(15);

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.##} GiB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.##} MiB",
        _ => $"{bytes:N0} bytes"
    };

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

        if (HasOutlivedItsTimeLimit(job) is { } limit)
        {
            // A provider that never finishes (on the lab: an NZB its indexer would not hand over, retried every few
            // seconds for a day) must not hold the request hostage. Ask it to stop, best effort, then record a failure
            // (which also releases the job) so the retry loop moves on to the next copy or a librarian.
            if (job.ProviderJobId is { } providerJobId)
            {
                try
                {
                    await client.CancelAcquireAsync(provider.BaseUrl, apiKey, providerJobId, cancellationToken);
                }
                catch (Exception exception) when (exception is HttpRequestException or ExternalProviderProtocolException or TimeoutException)
                {
                    // The provider may be the very thing that is broken; the local failure is still recorded.
                }
            }

            await RecordFailureAsync(job, "PROVIDER_TIMEOUT",
                $"The provider did not finish this copy within {limit.TotalHours:0} hours.",
                false, null, null, wasWaitingForInteraction, cancellationToken);
            return;
        }

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

                // A replay after a lost response may already be cancelled.
                // RecordSubmission preserves a nonterminal local state until
                // this bookkeeping completes; no extra status request is needed.
                if (submission.State == ProviderAcquisitionJobLifecycleState.Cancelled)
                {
                    await RecordFailureAsync(job, "PROVIDER_CANCELLED", "The provider cancelled the acquisition.",
                        false, null, null, wasWaitingForInteraction, cancellationToken);
                    return;
                }
            }
            catch (ExternalProviderSubmissionConflictException exception)
            {
                await RecordFailureAsync(job, "IDEMPOTENCY_CONFLICT", exception.Message, false, null, null,
                    wasWaitingForInteraction, cancellationToken);
                return;
            }
            catch (ExternalProviderProtocolException exception)
            {
                await RecordFailureAsync(job, "PROVIDER_PROTOCOL_ERROR", exception.Message, false, null, null,
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
        catch (ExternalProviderProtocolException exception)
        {
            await RecordFailureAsync(job, "PROVIDER_PROTOCOL_ERROR", exception.Message, false, null, null,
                wasWaitingForInteraction, cancellationToken);
            return;
        }

        if (status.State == ProviderAcquisitionJobLifecycleState.Failed)
        {
            await RecordFailureAsync(
                job, status.Error?.Code, status.Error?.Message, status.Error?.Retryable,
                status.Error?.RetryAfterSeconds, status.Error?.DetailsJson, wasWaitingForInteraction, cancellationToken);
            return;
        }

        // The provider itself ended the job. That is not the local admin-cancel
        // flow (which never reaches this poller): nothing was delivered, so it
        // must count as a failed attempt, or the format silently reverts to
        // "Requested" while its last attempt still reads "Submitted".
        if (status.State == ProviderAcquisitionJobLifecycleState.Cancelled)
        {
            await RecordFailureAsync(
                job, status.Error?.Code ?? "PROVIDER_CANCELLED",
                status.Error?.Message ?? "The provider cancelled the acquisition.",
                status.Error?.Retryable ?? false, status.Error?.RetryAfterSeconds, status.Error?.DetailsJson,
                wasWaitingForInteraction, cancellationToken);
            return;
        }

        if (status.State != ProviderAcquisitionJobLifecycleState.Completed)
        {
            RecordPauseTransition(job, status, job.Phase);
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
            await attempts.SaveChangesAsync(cancellationToken);
            return;
        }

        var previousPhase = job.Phase;
        await CompleteAsync(job, provider, apiKey, status, wasWaitingForInteraction, cancellationToken);
        if (job.LifecycleState == ProviderAcquisitionJobLifecycleState.Completed)
            RecordPauseTransition(job, status, previousPhase);
        await attempts.SaveChangesAsync(cancellationToken);
    }

    private void RecordPauseTransition(ProviderAcquisitionJob job, ExternalProviderJobStatus status, string? previousPhase)
    {
        var wasPaused = ProviderJobPause.IsPaused(previousPhase);
        var isPaused = (status.State is ProviderAcquisitionJobLifecycleState.Running or ProviderAcquisitionJobLifecycleState.Queued) &&
            ProviderJobPause.IsPaused(status.Phase);
        if (wasPaused == isPaused)
            return;
        // Waiting for a human is a different workflow, not a resumed transfer.
        if (!isPaused && status.State is not (ProviderAcquisitionJobLifecycleState.Running or
                ProviderAcquisitionJobLifecycleState.Queued or ProviderAcquisitionJobLifecycleState.Completed))
            return;
        attempts.Add(new ProviderAttempt(job.RequestId, job.RequestFormatId, job.ProviderId,
            isPaused ? ProviderAttemptOutcome.Paused : ProviderAttemptOutcome.Resumed,
            isPaused ? (string.IsNullOrWhiteSpace(status.Progress?.Message)
                    ? "The provider paused acquisition; check the provider." : status.Progress.Message)
                : "The provider's acquisition pause has cleared.",
            clock.UtcNow, nextEligibleCheckAtUtc: null, providerAcquisitionJobId: job.Id));
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

            await EvaluateJobAssetsAsync(job, alreadyStagedAssetIds, cancellationToken);
            if (job.LifecycleState == ProviderAcquisitionJobLifecycleState.Failed)
                return;
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
        var outputPolicy = configuredOutputPolicy.ForMediaType(format.MediaType);
        IReadOnlyList<ExternalProviderOutput> selected;
        var declaredTotal = 0L;
        try
        {
            selected = ExternalProviderOutputSelector.Select(outputs, format.MediaType,
                importPolicy, outputPolicy);
            // A part of an automatic set is exactly one file: the set's total
            // track count was fixed when it was started. Refuse before
            // downloading anything rather than after.
            if (job.IsPartSetMember && selected.Count != 1)
                throw new InvalidExternalProviderOutputException(
                    $"Part {job.PartNumber} of {job.PartTotal} reported {selected.Count} usable files; " +
                    "a part of an automatic set must be a single file.");
            if (outputs.Any(output =>
                    ExternalProviderOutputSelector.NormalizeFilename(output.Filename) is { } filename &&
                    (filename.Length > outputPolicy.MaxFilenameLength || filename.Any(char.IsControl))))
                throw new InvalidExternalProviderOutputException("The provider returned an invalid filename.");
            foreach (var output in outputs)
            {
                if (output.SizeBytes is < 0)
                    throw new InvalidExternalProviderOutputException(
                        $"The provider reported an invalid size ({output.SizeBytes} bytes) for output '{output.OutputId}'.");
                if (output.SizeBytes > outputPolicy.MaxFileBytes)
                    throw new InvalidExternalProviderOutputException(
                        $"Output '{output.OutputId}' is {FormatBytes(output.SizeBytes.Value)}, over the " +
                        $"{FormatBytes(outputPolicy.MaxFileBytes)} per-file limit for {format.MediaType} downloads.");
                if (output.SizeBytes.HasValue)
                {
                    declaredTotal = checked(declaredTotal + output.SizeBytes.Value);
                    if (declaredTotal > outputPolicy.MaxJobBytes)
                        throw new InvalidExternalProviderOutputException(
                            $"The provider outputs total at least {FormatBytes(declaredTotal)}, over the " +
                            $"{FormatBytes(outputPolicy.MaxJobBytes)} per-download limit for {format.MediaType} downloads.");
                }
            }
        }
        catch (InvalidExternalProviderOutputException exception)
        {
            await RecordFailureAsync(job, "CONTENT_UNAVAILABLE", exception.Message, false, null, null,
                wasWaitingForInteraction, cancellationToken);
            return;
        }
        catch (OverflowException)
        {
            await RecordFailureAsync(job, "CONTENT_UNAVAILABLE", "The provider reported output sizes too large to total.",
                false, null, null, wasWaitingForInteraction, cancellationToken);
            return;
        }

        // Not the provider's fault and not specific to this copy, so it neither
        // fails the attempt nor advances to the next candidate (which needs the
        // same space): the job waits and is re-checked once space is freed.
        if (outputPolicy.MinFreeDiskBytes > 0 && stagingStore.GetAvailableFreeBytes() is { } freeBytes &&
            freeBytes - outputPolicy.MinFreeDiskBytes < declaredTotal)
        {
            var message = $"Waiting for disk space: this download needs about {FormatBytes(declaredTotal)} plus " +
                $"{FormatBytes(outputPolicy.MinFreeDiskBytes)} headroom, but only {FormatBytes(freeBytes)} is free.";
            job.ApplyStatus(
                job.LifecycleState, job.Phase, job.InteractionType, job.InteractionMessage, job.InteractionExpiresAtUtc,
                job.InteractionResumeSupported, job.InteractionActionUrl, job.ProgressPercent, job.ProgressBytesCompleted,
                job.ProgressBytesTotal, message, clock.UtcNow.Add(DiskSpaceRecheckDelay), clock.UtcNow);
            await jobs.SaveChangesAsync(cancellationToken);
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
                AuditActions.ExternalProviderAcquisitionStaged, job, cancellationToken,
                job.PartSetId is { } partSetId
                    ? new AudiobookPartSetSlot(partSetId, job.PartNumber!.Value, job.PartTotal!.Value)
                    : null);
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
            await RecordFailureAsync(job, "CONTENT_UNAVAILABLE", "The provider reported output sizes too large to total.",
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

        var judged = await EvaluateJobAssetsAsync(job, stageResult.MediaAssetIds, cancellationToken);
        if (job.LifecycleState == ProviderAcquisitionJobLifecycleState.Failed)
            return;

        job.ApplyStatus(
            status.State, status.Phase, null, null, null, null, null, null, null, null, null,
            nextPollAtUtc: null, clock.UtcNow);
        await jobs.SaveChangesAsync(cancellationToken);

        // The download worked but the file did not: verification held it (the wrong book, the wrong language) or a
        // security/structure check rejected it. For the retry loop that is a failed copy exactly like a failed
        // download. Without this the format kept its "Submitted" attempt with no next check, so the request was
        // neither retried nor sent to review. A part of a set is judged once, with the whole set, by the part that
        // completed it: before that `judged` is null. A held set used to be judged and then left there, so its request
        // waited for ever on a "Submitted" row (lab: both parts of an Onyx Storm set were held and nothing moved on).
        var unusable = judged is null ? null : await FindUnusableFileReasonAsync(judged, cancellationToken);
        if (unusable is null && job.PartSetId is null)
        {
            // The ledger's last word on this copy. Without it the "Submitted" row stayed the latest one and the
            // activity page showed a download that had long since reached the library as "In progress".
            attempts.Add(new ProviderAttempt(
                job.RequestId, job.RequestFormatId, job.ProviderId, ProviderAttemptOutcome.Acquired,
                "Downloaded. The copy was received and passed on for checking and publishing.", clock.UtcNow,
                nextEligibleCheckAtUtc: null, providerAcquisitionJobId: job.Id));
            await attempts.SaveChangesAsync(cancellationToken);
        }

        if (job.IsAutomaticAcquisition && unusable is not null)
        {
            var now = clock.UtcNow;
            var automatic = await RecordAutomaticCandidateFailureAsync(job, unusable, cancellationToken);
            attempts.Add(new ProviderAttempt(
                job.RequestId, job.RequestFormatId, job.ProviderId,
                automatic.AdvancesToNextCandidate ? ProviderAttemptOutcome.Retrying : ProviderAttemptOutcome.Failed,
                DescribeAutomaticFailure(unusable, automatic), now,
                nextEligibleCheckAtUtc: automatic.AdvancesToNextCandidate ? now : null, providerAcquisitionJobId: job.Id));
            await attempts.SaveChangesAsync(cancellationToken);
            if (automatic.AdvancesToNextCandidate)
            {
                await MarkRuledOutAsync(judged!, now, cancellationToken);
                fulfillmentSignal?.Request();
            }
        }

        if (wasWaitingForInteraction)
        {
            await audit.WriteAsync(
                AuditActions.ProviderInteractionCompleted, AuditSubjectTypes.ProviderInteraction, job.Id.ToString(),
                new { job.Id, job.RequestId, job.RequestFormatId, job.ProviderId }, cancellationToken);
        }

    }

    /// <summary>
    /// Releases a failed job at the provider (protocol v2 DELETE), best effort. Only completed jobs used to be released,
    /// so every failed download's files stayed on the provider's disk: on the lab, all four leftover folders (3.3 GB)
    /// belonged to failed jobs. A failure here leaves the provider's own retention to clean up.
    /// </summary>
    private async Task ReleaseAtProviderAsync(ProviderAcquisitionJob job, CancellationToken cancellationToken)
    {
        if (job.ProviderJobId is not { } providerJobId)
        {
            return;
        }

        try
        {
            var provider = await externalProviders.FindAsync(job.ExternalProviderId, cancellationToken);
            if (provider is null)
            {
                return;
            }

            var apiKey = provider.HasApiKey
                ? protector.Unprotect(ExternalProviderSecretPurposes.ApiKey, provider.ProtectedApiKey!, provider.ApiKeyFormatVersion)
                : null;
            await client.TryDeleteAcquireAsync(provider.BaseUrl, apiKey, providerJobId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Best effort: the failure itself is still recorded.
        }
    }

    private async Task RecordSourceFailureAsync(
        ProviderAcquisitionJob job, ProviderFailureKind kind, string? errorMessage, int? retryAfterSeconds,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var reason = string.IsNullOrWhiteSpace(errorMessage) ? "no reason was given" : errorMessage.Trim();
        ProviderAttemptOutcome outcome;
        DateTimeOffset nextCheck;
        string summary;
        if (kind == ProviderFailureKind.Throttled)
        {
            var wait = ProviderFailureClassifier.ThrottleDelay(retryAfterSeconds);
            outcome = ProviderAttemptOutcome.Paused;
            nextCheck = now + wait;
            summary = $"The source asked Family Librarian to slow down ({reason}). The copy is kept; it will be tried again in " +
                $"about {Math.Max(1, (int)Math.Round(wait.TotalMinutes))} minutes.";
        }
        else
        {
            outcome = ProviderAttemptOutcome.Failed;
            nextCheck = now + ProviderFailureClassifier.SourceProblemRecheckDelay;
            summary = $"The source could not download this copy: {reason}. This is a source setting or access problem, not a bad " +
                "copy, so the copy is kept and will be tried again after the source is fixed (checked again in about " +
                $"{(int)ProviderFailureClassifier.SourceProblemRecheckDelay.TotalHours} hours).";
        }

        attempts.Add(new ProviderAttempt(
            job.RequestId, job.RequestFormatId, job.ProviderId, outcome, summary, now, nextEligibleCheckAtUtc: nextCheck,
            providerAcquisitionJobId: job.Id));
        await jobs.SaveChangesAsync(cancellationToken);
        await attempts.SaveChangesAsync(cancellationToken);
    }

    /// <summary>How long a provider job may stay queued, or keep running, before it is treated as failed.</summary>
    internal static readonly TimeSpan MaxQueuedAge = TimeSpan.FromHours(12);

    internal static readonly TimeSpan MaxRunningAge = TimeSpan.FromHours(48);

    /// <summary>
    /// The limit a still-active job has exceeded, measured from when it was created, or null. A job waiting on a person
    /// (a verification challenge) has no limit here: that wait is the administrator's, not the provider's.
    /// </summary>
    private TimeSpan? HasOutlivedItsTimeLimit(ProviderAcquisitionJob job)
    {
        var limit = job.LifecycleState switch
        {
            ProviderAcquisitionJobLifecycleState.Queued => MaxQueuedAge,
            ProviderAcquisitionJobLifecycleState.Running => MaxRunningAge,
            _ => (TimeSpan?)null
        };
        return limit is { } value && clock.UtcNow - job.CreatedAtUtc > value ? value : null;
    }

    /// <summary>Why a staged file cannot be used, or null when every file is trusted or still awaiting a decision.</summary>
    private async Task<string?> FindUnusableFileReasonAsync(IReadOnlyList<Guid> assetIds, CancellationToken cancellationToken)
    {
        if (assets is null)
        {
            return null;
        }

        foreach (var assetId in assetIds)
        {
            var asset = await assets.FindAssetAsync(assetId, cancellationToken);
            switch (asset?.StorageState)
            {
                case MediaAssetStorageState.Unmatched:
                    return asset.IdentityMismatchReason ?? "The fetched copy did not match the requested book.";
                case MediaAssetStorageState.Rejected or MediaAssetStorageState.Destroyed:
                    return asset.ScanFailureReason ?? "The fetched copy failed a security or structure check.";
            }
        }

        return null;
    }

    /// <summary>
    /// Marks a held copy the loop has moved past, so the security queue shows it as settled rather than as an identity
    /// review nobody needs to do. An exhausted budget leaves the mark off: the request then goes to a librarian, and
    /// that copy is one of the things to look at.
    /// </summary>
    private async Task MarkRuledOutAsync(IReadOnlyList<Guid> assetIds, DateTimeOffset atUtc, CancellationToken cancellationToken)
    {
        if (assets is null)
        {
            return;
        }

        foreach (var assetId in assetIds)
        {
            var asset = await assets.FindAssetAsync(assetId, cancellationToken);
            if (asset?.StorageState is MediaAssetStorageState.Unmatched or MediaAssetStorageState.Rejected or MediaAssetStorageState.Quarantine)
            {
                asset.MarkRuledOut(atUtc);
            }
        }

        await assets.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Rules the job's candidate out and reports whether the provider's attempt budget allows another copy.</summary>
    private async Task<ExternalFailureOutcome> RecordAutomaticCandidateFailureAsync(
        ProviderAcquisitionJob job, string reason, CancellationToken cancellationToken)
    {
        // The attempt budget belongs to the provider that was asked, so it is read from the provider row rather than
        // assumed. A provider that has since been deleted falls back to the registration default, which still bounds
        // the loop.
        var failedProvider = await externalProviders.FindAsync(job.ExternalProviderId, cancellationToken);
        return await automaticFulfillment.RecordExternalAcquisitionFailureAsync(
            job.RequestId,
            job.RequestFormatId,
            job.ProviderId,
            job.CandidateReference,
            job.CandidateFingerprint,
            failedProvider?.AutomaticAttemptLimit ?? Domain.Providers.ExternalProvider.DefaultAutomaticAttemptLimit,
            reason,
            cancellationToken);
    }

    private static string DescribeAutomaticFailure(string reason, ExternalFailureOutcome automatic) =>
        automatic.FailuresSoFar <= 0
            ? reason
            : automatic.AdvancesToNextCandidate
                ? AutomaticAttemptNarrative.Advancing(reason, automatic.FailuresSoFar, automatic.AttemptLimit)
                : AutomaticAttemptNarrative.Exhausted(reason, automatic.FailuresSoFar, automatic.AttemptLimit);

    /// <summary>
    /// An ordinary job's assets are evaluated and approved at once. A part of
    /// an automatic audiobook set is only scanned on arrival: nothing is
    /// approved until every part has been staged, then the whole set is
    /// evaluated together, so a bad part can never leave a trusted orphan and
    /// the book publishes only when every part passes.
    /// </summary>
    /// <returns>
    /// The files that were judged and now have a verdict: this job's own files, or for a set, every part's files once
    /// the last part has arrived. Null while a set is still waiting for parts, or when the job failed instead.
    /// </returns>
    private async Task<IReadOnlyList<Guid>?> EvaluateJobAssetsAsync(
        ProviderAcquisitionJob job, IReadOnlyList<Guid> assetIds, CancellationToken cancellationToken)
    {
        if (job.PartSetId is not { } setId)
        {
            await EvaluateAssetsAsync(assetIds, cancellationToken);
            return assetIds;
        }

        foreach (var assetId in assetIds)
        {
            var result = await securityPipeline.EvaluateWithoutApprovalAsync(assetId, cancellationToken);
            if (result is { Outcome: Security.SecurityEvaluationOutcome.Success, Status: Domain.Security.SecurityEvaluationStatus.Failed })
            {
                await RecordFailureAsync(
                    job, "SET_PART_FAILED", "The file failed its security checks.",
                    retryable: false, retryAfterSeconds: null, detailsJson: null,
                    wasWaitingForInteraction: false, cancellationToken);
                return null;
            }
        }

        var members = await jobs.ListByPartSetAsync(setId, cancellationToken);
        var stagedByPart = members
            .OrderBy(member => member.PartNumber)
            .Select(member => member.Outputs.Where(output => output.MediaAssetId.HasValue)
                .Select(output => output.MediaAssetId!.Value).ToArray())
            .ToArray();
        if (members.Count != job.PartTotal || stagedByPart.Any(ids => ids.Length == 0))
        {
            // Other parts are still on their way; the last one to arrive
            // evaluates the whole set.
            return null;
        }

        var setAssets = stagedByPart.SelectMany(ids => ids).ToArray();
        await securityPipeline.EvaluateBundleAsync(setAssets, cancellationToken);
        return setAssets;
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
        await ReleaseAtProviderAsync(job, cancellationToken);

        // One part of an automatic set failing fails the whole set: the other
        // parts stop, anything staged is destroyed and a librarian is asked.
        // Deliberately outside the candidate-retry loop below -- advancing to a
        // different single record would not be a substitute for a missing part.
        if (job.IsPartSetMember && partSetFailures is not null)
        {
            await partSetFailures.FailAsync(job, errorMessage ?? "The acquisition failed.", cancellationToken);
            attempts.Add(new ProviderAttempt(
                job.RequestId, job.RequestFormatId, job.ProviderId, ProviderAttemptOutcome.Failed,
                $"Part {job.PartNumber} of {job.PartTotal} of an audiobook set failed: " +
                $"{errorMessage ?? "The acquisition failed."} The whole set was abandoned and the request needs a librarian.",
                now, nextEligibleCheckAtUtc: null, providerAcquisitionJobId: job.Id));
            await jobs.SaveChangesAsync(cancellationToken);
            await attempts.SaveChangesAsync(cancellationToken);
            return;
        }

        // The source refusing or throttling the download says nothing against the copy, so it is neither ruled out
        // nor counted against the attempt budget (lab: an indexer's bot filter refused the provider's client and the only
        // English copy was discarded for good). The provider is simply asked again later.
        var failureKind = job.IsAutomaticAcquisition ? ProviderFailureClassifier.Classify(errorCode) : ProviderFailureKind.CopyProblem;
        if (failureKind != ProviderFailureKind.CopyProblem)
        {
            await RecordSourceFailureAsync(job, failureKind, errorMessage, retryAfterSeconds, now, cancellationToken);
            return;
        }

        var advancedToNextCandidate = false;
        ExternalFailureOutcome? automaticOutcome = null;
        if (job.IsAutomaticAcquisition && (!candidateChanged || alreadyRetriedStaleCandidate))
        {
            var reason = candidateChanged
                ? "The provider reported that this copy changed twice. A librarian must refresh the source before another automatic attempt."
                : errorMessage ?? "The automatic copy could not be acquired.";
            automaticOutcome = await RecordAutomaticCandidateFailureAsync(job, reason, cancellationToken);
            advancedToNextCandidate = automaticOutcome.AdvancesToNextCandidate;
        }

        // A step that moves on to the next candidate is progress, not an
        // error: it is recorded as Retrying so it neither reads as a failure
        // nor counts as an operational issue. Only a spent budget (or a
        // failure outside the automatic loop) is a Failed row.
        var outcome = advancedToNextCandidate ? ProviderAttemptOutcome.Retrying : ProviderAttemptOutcome.Failed;
        string summary;
        if (candidateChanged && !alreadyRetriedStaleCandidate)
        {
            summary = $"CANDIDATE_CHANGED: {errorMessage ?? "The candidate changed upstream."}";
        }
        else if (automaticOutcome is { } automatic && automatic.FailuresSoFar > 0)
        {
            summary = DescribeAutomaticFailure(errorMessage ?? "The acquisition failed.", automatic);
        }
        else
        {
            summary = errorMessage ?? "The acquisition failed.";
        }

        attempts.Add(new ProviderAttempt(
            job.RequestId, job.RequestFormatId, job.ProviderId, outcome, summary, now,
            // Either kind of advance wants a prompt recheck: the next attempt
            // is a different record, not a repeat of what just failed.
            nextEligibleCheckAtUtc: (candidateChanged && !alreadyRetriedStaleCandidate) || advancedToNextCandidate
                ? now
                : null,
            providerAcquisitionJobId: job.Id));
        await jobs.SaveChangesAsync(cancellationToken);
        await attempts.SaveChangesAsync(cancellationToken);

        // Wake the fulfillment worker so the next candidate starts now rather
        // than at its next two-minute sweep.
        if (advancedToNextCandidate)
        {
            fulfillmentSignal?.Request();
        }

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
