using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Audit;

namespace FamilyLibrarian.Application.Acquisition;

/// <summary>
/// Fetches a file from a bundled free-source provider (e.g. Project Gutenberg) or a
/// registered external provider, and stages it exactly like a manual upload.
/// </summary>
/// <remarks>
/// The option is re-derived server-side from the provider's own search
/// rather than trusting anything client-supplied: the browser only ever
/// sends <c>providerId</c>/<c>providerResultId</c>, never a download URL —
/// re-resolving by the request's own Work/media type is what keeps a
/// tampered or stale client request from making the host fetch an arbitrary
/// address. A <c>providerId</c> that doesn't match any compile-time
/// (DI-registered) provider falls back to a lookup among admin-registered
/// <see cref="Domain.Providers.ExternalProvider"/> rows — the two are otherwise
/// handled identically once a file is in hand.
/// </remarks>
public sealed class DirectAcquisitionService(
    IRequestRepository requests,
    IEnumerable<IDirectAcquisitionProvider> providers,
    IExternalProviderStore externalProviders,
    IExternalProviderClient externalProviderClient,
    IProviderAcquisitionJobStore providerAcquisitionJobs,
    ExternalCandidateAvailabilityChecker externalCandidateChecker,
    ICredentialProtector protector,
    IWorkLookup workLookup,
    AcquisitionStagingService staging,
    IClock clock,
    ActiveAcquisitionTracker activityTracker)
{
    /// <param name="confirmLowConfidenceMatch">
    /// Required once an external provider's result is only
    /// <see cref="BookMatchBasis.TitleAuthor"/> (or unconfirmed/language-excluded)
    /// -- a reviewable fallback, not a verified identifier -- see
    /// <see cref="ExternalProviderMatchVerifier"/>. No file is fetched until
    /// the caller confirms. Never checked for a compile-time
    /// (DI-registered) <see cref="IDirectAcquisitionProvider"/> such as
    /// Gutenberg -- those are FL-vetted, not arbitrary third-party code.
    /// </param>
    /// <param name="allowDownloadTimeDrmValidation">
    /// Internal scheduled-recheck path only. It permits a Safe, strict-match
    /// candidate whose *metadata* DRM state is unknown to submit one provider
    /// job, on the condition that the provider validates the downloaded bytes
    /// before publishing an output. It never waives a collection, sample,
    /// Possible-format, encrypted, or low-confidence identity concern.
    /// </param>
    public async Task<ManualImportResult> AcquireAsync(
        Guid requestId,
        Guid requestFormatId,
        string providerId,
        string providerResultId,
        CancellationToken cancellationToken,
        bool confirmLowConfidenceMatch = false,
        bool allowDownloadTimeDrmValidation = false,
        bool isAutomaticAcquisition = false)
    {
        var request = await requests.FindRequestForAdminAsync(requestId, cancellationToken);
        if (request is null)
        {
            return ManualImportResult.Invalid("That request does not exist.");
        }

        var format = request.Formats.FirstOrDefault(format => format.Id == requestFormatId);
        if (format is null)
        {
            return ManualImportResult.Invalid("That format is not part of this request.");
        }

        var provider = providers.FirstOrDefault(
            provider => string.Equals(provider.Id, providerId, StringComparison.OrdinalIgnoreCase));

        var work = await workLookup.FindAsync(request.WorkId, cancellationToken);

        if (provider is not null)
        {
            using var activity = activityTracker.Begin(requestId, requestFormatId, providerId,
                "Checking provider", work?.Title);
            var options = await provider.FindDirectAcquisitionsAsync(request.WorkId, format.MediaType, cancellationToken);
            var option = options.FirstOrDefault(option =>
                string.Equals(option.ProviderResultId, providerResultId, StringComparison.Ordinal));
            if (option is null)
            {
                return ManualImportResult.Invalid("That option is no longer available.");
            }

            IReadOnlyList<DirectAcquisitionFile> files;
            IProgressReportingDirectAcquisitionProvider? progressProvider = null;
            var progressContext = new DirectAcquisitionRequestContext(requestId, requestFormatId);
            try
            {
                if (provider is IProgressReportingDirectAcquisitionProvider reportingProvider)
                {
                    progressProvider = reportingProvider;
                    activity.SetStage("Downloading");
                    files = await progressProvider.FetchWithProgressAsync(
                        option,
                        progressContext,
                        progress => activity.ReportTransferProgress(
                            progress.BytesReceived, progress.TotalBytes, progress.Stage, progress.IsTransferBaseline),
                        cancellationToken);
                }
                else
                {
                    activity.SetStage("Downloading and preparing files");
                    files = await provider.FetchAsync(option, cancellationToken);
                }
            }
            catch (ResumableDownloadInterruptedException)
            {
                return ManualImportResult.TransferInterrupted();
            }
            catch (HttpRequestException exception)
            {
                return ManualImportResult.Invalid($"The file could not be fetched: {exception.Message}");
            }
            catch (InvalidOperationException exception)
            {
                // A bundled provider (e.g. Gutenberg) can reject its own
                // candidate before any download starts — most commonly an
                // audiobook bundle over the configured track limit. Report it
                // the same clean way the automatic poller does instead of
                // letting it surface as an unhandled failure.
                return ManualImportResult.Invalid($"The file could not be processed: {exception.Message}");
            }
            catch (IOException exception)
            {
                return ManualImportResult.Invalid($"The file could not be processed: {exception.Message}");
            }

            if (files.Count == 0)
            {
                if (progressProvider is not null) await progressProvider.FinishAcquisitionAsync(progressContext);
                return ManualImportResult.Invalid("The provider returned no file for that option.");
            }

            activity.SetStage("Processing files");

            ManualImportResult stagedResult;
            try
            {
                if (files.Count == 1)
                {
                    await using var content = files[0].Content;
                    stagedResult = await staging.StageAsync(
                        request, format, content, files[0].Filename, providerId, AuditActions.DirectAcquisitionStaged,
                        candidateTitle: work?.Title, candidateAuthor: work?.PrimaryAuthor, cancellationToken);
                }
                else
                {
                    stagedResult = await staging.StageBundleAsync(
                        request, format, files, providerId, AuditActions.DirectAcquisitionStaged,
                        candidateTitle: work?.Title, candidateAuthor: work?.PrimaryAuthor, cancellationToken);
                }
            }
            finally
            {
                if (files.Count > 1)
                {
                    foreach (var file in files)
                        await file.Content.DisposeAsync();
                }
                // A caller cancellation can represent host shutdown; preserve a
                // completed ZIP so the next attempt can reuse it after restart.
                if (progressProvider is not null && !cancellationToken.IsCancellationRequested)
                    await progressProvider.FinishAcquisitionAsync(progressContext);
            }

            return stagedResult;
        }

        var externalProvider = await externalProviders.FindByProviderIdAsync(providerId, cancellationToken);
        if (externalProvider is null || !externalProvider.IsEnabled)
        {
            return ManualImportResult.Invalid("That provider is not available.");
        }

        if (ExternalCandidateAvailabilityChecker.IsKnownSearchUnavailable(externalProvider))
        {
            return ManualImportResult.Invalid(
                $"{externalProvider.DisplayName}'s search capability was last reported unavailable " +
                $"(checked {externalProvider.LastTestedAtUtc:u}). Re-test the provider in Admin → External " +
                "Providers, or wait for its next scheduled recheck.");
        }

        var identity = new BookIdentity(
            work?.Title ?? string.Empty, work?.PrimaryAuthor, work?.Isbn13s ?? [],
            work?.Authors, work?.Series, work?.Language, work?.PublicationYear, work?.Publisher,
            work?.AlternateTitles);
        // Re-derivation must see the same ruled-out set the caller saw, or it
        // reaches a different conclusion than the decision it is carrying out.
        // Exactly one candidate keeps the confirmed strict basis that permits
        // an unattended fetch; if a candidate ruled out by an earlier failed
        // attempt is still in the contest here, it wins that basis again and
        // the candidate actually being fetched is demoted to "low confidence",
        // refusing a fetch the retry loop had already decided on.
        var exclusions = Providers.ExternalCandidateExclusions.From(
            request.DeclinedCandidates, providerId, format.Id, exceptResultId: providerResultId);

        IReadOnlyList<FulfillmentOption> externalOptions;
        try
        {
            externalOptions = await externalCandidateChecker.FindForProviderAsync(
                externalProvider, identity, format.MediaType, cancellationToken, exclusions);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return ManualImportResult.Invalid($"The provider could not be reached: {exception.Message}");
        }

        var externalOption = externalOptions.FirstOrDefault(
            candidateOption => string.Equals(candidateOption.ProviderResultId, providerResultId, StringComparison.Ordinal));
        if (externalOption is null)
        {
            return ManualImportResult.Invalid("That option is no longer available.");
        }

        if (externalOption.MatchBasis is not (BookMatchBasis.Identifier or BookMatchBasis.StrictTitleAuthor or BookMatchBasis.StrictTitle) &&
            !confirmLowConfidenceMatch)
        {
            return ManualImportResult.LowConfidenceMatchConfirmationRequired();
        }

        // Independent of match confidence -- a verified identifier match can
        // still point at the wrong release (a collection, a sample, an
        // abridged edition). Checked separately so the message tells the
        // admin what's actually wrong rather than reusing the identity-match
        // explanation for an unrelated concern.
        if (externalOption.RequiresReleaseConfirmation && !confirmLowConfidenceMatch &&
            !(allowDownloadTimeDrmValidation && IsUnknownDrmOnlyConcern(externalOption)))
        {
            return ManualImportResult.ReleaseConfirmationRequired(externalOption.ReleaseConcern);
        }

        var apiKey = externalProvider.HasApiKey
            ? protector.Unprotect(
                ExternalProviderSecretPurposes.ApiKey, externalProvider.ProtectedApiKey!, externalProvider.ApiKeyFormatVersion)
            : null;

        // Protocol v2 (docs/04-external-provider-http-protocol.md §8): submit
        // and return immediately with a durable job, rather than blocking
        // this request on however long the provider's acquisition actually
        // takes. AcquisitionJobPollingService drives the job to completion
        // and stages it once the provider reports "completed".
        if (ExternalCandidateAvailabilityChecker.IsKnownAcquireUnavailable(externalProvider))
        {
            return ManualImportResult.Invalid(
                $"{externalProvider.DisplayName}'s acquire capability was last reported unavailable " +
                $"(checked {externalProvider.LastTestedAtUtc:u}), even though search found this candidate. " +
                "Re-test the provider in Admin → External Providers, or wait for its next scheduled recheck.");
        }

        var idempotencyKey = Guid.NewGuid().ToString("N");
        var acquireRequestId = Guid.NewGuid();
        var acquireRequest = new ExternalAcquireRequest(
            acquireRequestId, externalOption.ProviderResultId, externalOption.CandidateRevision, externalOption.AcquireToken,
            format.MediaType);
        var now = clock.UtcNow;
        var job = new ProviderAcquisitionJob(
            request.Id,
            format.Id,
            externalProvider.Id,
            externalProvider.ProviderId,
            externalProvider.CachedInstanceId,
            idempotencyKey,
            externalOption.ProviderResultId,
            externalOption.CandidateRevision,
            externalOption.AcquireToken,
            now,
            acquireRequestId,
            isAutomaticAcquisition,
            Providers.ExternalReleaseFingerprint.Compute(externalOption.ReleaseName, externalOption.SizeBytes),
            // The automatic path never reaches here without one of these two
            // bases (see the gate above autoEligible in
            // AutomaticRequestFulfillmentService); a manual acquisition with
            // neither needed confirmLowConfidenceMatch to pass the check
            // above, i.e. a librarian overriding an unconfirmed match.
            identityPreConfirmed: externalOption.MatchBasis
                is BookMatchBasis.Identifier or BookMatchBasis.StrictTitleAuthor or BookMatchBasis.StrictTitle);
        providerAcquisitionJobs.Add(job);
        await providerAcquisitionJobs.SaveChangesAsync(cancellationToken);

        // Persist the stable request ID and idempotency key before POST. If the
        // host stops after the provider accepts but before FL receives a reply,
        // the poller can safely replay the same request after restart.
        ExternalProviderAcquireSubmission submission;
        try
        {
            submission = await externalProviderClient.SubmitAcquireAsync(
                externalProvider.BaseUrl, apiKey, acquireRequest, idempotencyKey, cancellationToken);
        }
        catch (ExternalProviderSubmissionConflictException exception)
        {
            job.RecordFailure("IDEMPOTENCY_CONFLICT", exception.Message, false, null, null, clock.UtcNow);
            await providerAcquisitionJobs.SaveChangesAsync(cancellationToken);
            return ManualImportResult.Invalid("The provider could not confirm the original acquisition request. Review provider activity before retrying.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutException or TaskCanceledException)
        {
            job.Reschedule(now.AddSeconds(30), clock.UtcNow);
            await providerAcquisitionJobs.SaveChangesAsync(cancellationToken);
            return ManualImportResult.AcquisitionInProgress(job.Id);
        }

        if (submission.Outcome == ProviderAcquireOutcome.CandidateChanged)
        {
            job.RecordFailure("CANDIDATE_CHANGED", "That candidate has changed upstream since it was found.",
                false, null, null, clock.UtcNow);
            await providerAcquisitionJobs.SaveChangesAsync(cancellationToken);
            return ManualImportResult.Invalid(
                "That candidate has changed upstream. Search again for a fresh result.");
        }

        job.RecordSubmission(
            submission.JobId!,
            submission.State!.Value,
            now.AddSeconds(submission.PollAfterSeconds ?? 2),
            now);

        await providerAcquisitionJobs.SaveChangesAsync(cancellationToken);

        return ManualImportResult.AcquisitionInProgress(job.Id);
    }

    private static bool IsUnknownDrmOnlyConcern(FulfillmentOption option) =>
        option.DrmStatus == "unknown" &&
        string.Equals(
            option.ReleaseConcern,
            ExternalReleasePolicy.UnknownDrmConfirmationReason,
            StringComparison.Ordinal);
}
