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
    PrivateEgressRouteResolver routeResolver,
    ICredentialProtector protector,
    IWorkLookup workLookup,
    AcquisitionStagingService staging,
    IClock clock)
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
    public async Task<ManualImportResult> AcquireAsync(
        Guid requestId,
        Guid requestFormatId,
        string providerId,
        string providerResultId,
        CancellationToken cancellationToken,
        bool confirmLowConfidenceMatch = false)
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
            var options = await provider.FindDirectAcquisitionsAsync(request.WorkId, format.MediaType, cancellationToken);
            var option = options.FirstOrDefault(option =>
                string.Equals(option.ProviderResultId, providerResultId, StringComparison.Ordinal));
            if (option is null)
            {
                return ManualImportResult.Invalid("That option is no longer available.");
            }

            IReadOnlyList<DirectAcquisitionFile> files;
            try
            {
                files = await provider.FetchAsync(option, cancellationToken);
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

            if (files.Count == 0)
            {
                return ManualImportResult.Invalid("The provider returned no file for that option.");
            }

            if (files.Count == 1)
            {
                await using var content = files[0].Content;
                return await staging.StageAsync(
                    request, format, content, files[0].Filename, providerId, AuditActions.DirectAcquisitionStaged,
                    candidateTitle: work?.Title, candidateAuthor: work?.PrimaryAuthor, cancellationToken);
            }

            try
            {
                return await staging.StageBundleAsync(
                    request, format, files, providerId, AuditActions.DirectAcquisitionStaged,
                    candidateTitle: work?.Title, candidateAuthor: work?.PrimaryAuthor, cancellationToken);
            }
            finally
            {
                foreach (var file in files)
                {
                    await file.Content.DisposeAsync();
                }
            }
        }

        var externalProvider = await externalProviders.FindByProviderIdAsync(providerId, cancellationToken);
        if (externalProvider is null || !externalProvider.IsEnabled)
        {
            return ManualImportResult.Invalid("That provider is not available.");
        }

        var resolution = routeResolver.Resolve(externalProvider.EffectiveEgressPolicy);
        if (!resolution.IsAllowed)
        {
            return ManualImportResult.Invalid(resolution.BlockedReason!);
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
            work?.Authors, work?.Series, work?.Language, work?.PublicationYear, work?.Publisher);
        IReadOnlyList<FulfillmentOption> externalOptions;
        try
        {
            externalOptions = await externalCandidateChecker.FindForProviderAsync(
                externalProvider, resolution.Route!, identity, format.MediaType, cancellationToken);
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

        if (externalOption.MatchBasis != BookMatchBasis.Identifier && !confirmLowConfidenceMatch)
        {
            return ManualImportResult.LowConfidenceMatchConfirmationRequired();
        }

        // Independent of match confidence -- a verified identifier match can
        // still point at the wrong release (a collection, a sample, an
        // abridged edition). Checked separately so the message tells the
        // admin what's actually wrong rather than reusing the identity-match
        // explanation for an unrelated concern.
        if (externalOption.RequiresReleaseConfirmation && !confirmLowConfidenceMatch)
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
        var acquireRequest = new ExternalAcquireRequest(
            Guid.NewGuid(), externalOption.ProviderResultId, externalOption.CandidateRevision, externalOption.AcquireToken,
            format.MediaType);

        ExternalProviderAcquireSubmission submission;
        try
        {
            submission = await externalProviderClient.SubmitAcquireAsync(
                externalProvider.BaseUrl, apiKey, acquireRequest, idempotencyKey, resolution.Route!, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutException or TaskCanceledException)
        {
            return ManualImportResult.Invalid($"The acquisition could not be started: {exception.Message}");
        }

        if (submission.Outcome == ProviderAcquireOutcome.CandidateChanged)
        {
            return ManualImportResult.Invalid(
                "That candidate has changed upstream since it was found. Search again for a fresh result.");
        }

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
            now);
        job.RecordSubmission(
            submission.JobId!,
            submission.State!.Value,
            now.AddSeconds(submission.PollAfterSeconds ?? 2),
            now);

        providerAcquisitionJobs.Add(job);
        await providerAcquisitionJobs.SaveChangesAsync(cancellationToken);

        return ManualImportResult.AcquisitionInProgress(job.Id);
    }
}
