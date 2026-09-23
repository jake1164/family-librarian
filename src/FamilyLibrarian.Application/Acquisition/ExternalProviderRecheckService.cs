using System.Security.Cryptography;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Accounts;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Notifications;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Domain.Accounts;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Providers;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Application.Acquisition;

/// <summary>
/// Rechecks administrator-approved external providers on their configured
/// cadence. A single ISBN-corroborated ("Identifier"-basis) candidate is
/// trusted and acquired automatically, exactly like the manual acquire flow
/// already trusts one (docs/04-external-provider-http-protocol.md §7,
/// <see cref="ExternalCandidateAvailabilityChecker"/>) -- but only for a
/// provider with <see cref="ExternalProvider.AutoAcquireEnabled"/> separately
/// turned on; the recheck schedule alone only controls how often this
/// service looks, never whether it may fetch. Anything weaker -- title/author
/// only, unconfirmed, language-excluded, or more than one equally plausible
/// candidate -- is evidence for a librarian, never permission to fetch a
/// third-party file without review, regardless of that toggle.
/// </summary>
public sealed class ExternalProviderRecheckService(
    IRequestRepository requests,
    IProviderAttemptRepository attempts,
    IExternalProviderStore providers,
    ExternalCandidateAvailabilityChecker candidateChecker,
    DirectAcquisitionSecurityService security,
    PrivateEgressRouteResolver routeResolver,
    IWorkLookup workLookup,
    IClock clock,
    NotificationService notifications,
    IUserAccountStore accounts)
{
    private const int BatchSize = 20;
    private static readonly TimeSpan BackgroundSearchTimeout = TimeSpan.FromMinutes(2);

    public async Task<int> ProcessDueAsync(CancellationToken cancellationToken)
    {
        var scheduledProviders = (await providers.ListEnabledAsync(cancellationToken))
            .Where(provider => provider.RecheckSchedule is ProviderRecheckSchedule.Daily or ProviderRecheckSchedule.Weekly)
            .ToArray();
        if (scheduledProviders.Length == 0)
        {
            return 0;
        }

        var pending = await requests.ListPendingForAutomaticFulfillmentAsync(BatchSize, cancellationToken);
        if (pending.Count == 0)
        {
            return 0;
        }

        var checks = 0;
        foreach (var request in pending)
        {
            var work = await workLookup.FindAsync(request.WorkId, cancellationToken);
            foreach (var format in request.Formats.Where(format => format.Status == RequestFormatStatus.Requested))
            {
                if (await requests.HasAcquiredArtifactAsync(format.Id, cancellationToken))
                {
                    continue;
                }

                foreach (var provider in scheduledProviders)
                {
                    var latest = await attempts.FindLatestForFormatAsync(format.Id, provider.ProviderId, cancellationToken);
                    if (!IsDue(latest, request, clock.UtcNow))
                    {
                        continue;
                    }

                    checks++;
                    var nextCheck = clock.UtcNow + ToInterval(provider.RecheckSchedule);
                    var resolution = routeResolver.Resolve(provider.EffectiveEgressPolicy);
                    if (!resolution.IsAllowed)
                    {
                        AddAttempt(request, format, provider, ProviderAttemptOutcome.Blocked,
                            resolution.BlockedReason ?? "The provider's egress policy could not be satisfied.", nextCheck);
                        continue;
                    }

                    // Protocol v2 §5: operations.search/operations.acquire is
                    // the specific signal, over the coarse overall status --
                    // a provider already known unable to search or acquire
                    // (from ExternalProviderHealthPollService's own
                    // independent background probe, kept fresh on its own
                    // fixed interval regardless of this provider's
                    // RecheckSchedule) is skipped without a live search
                    // call, and the reason is recorded explicitly rather
                    // than left indistinguishable from "no candidates" or a
                    // generic connection failure.
                    if (ExternalCandidateAvailabilityChecker.IsKnownSearchUnavailable(provider) ||
                        ExternalCandidateAvailabilityChecker.IsKnownAcquireUnavailable(provider))
                    {
                        AddAttempt(request, format, provider, ProviderAttemptOutcome.Blocked,
                            $"{provider.DisplayName}'s last health check reported search: " +
                            $"{provider.CachedSearchOperationStatus ?? "unknown"}, acquire: " +
                            $"{provider.CachedAcquireOperationStatus ?? "unknown"} — skipped without a live search call.",
                            nextCheck);
                        continue;
                    }

                    if (work is null)
                    {
                        AddAttempt(request, format, provider, ProviderAttemptOutcome.Failed,
                            "The requested work is no longer available for provider lookup.", nextCheck);
                        continue;
                    }

                    CancellationTokenSource? searchCancellation = null;
                    try
                    {
                        searchCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        searchCancellation.CancelAfter(BackgroundSearchTimeout);
                        var identity = new BookIdentity(
                            work.Title, work.PrimaryAuthor, work.Isbn13s,
                            work.Authors, work.Series, work.Language, work.PublicationYear, work.Publisher,
                            work.AlternateTitles);
                        var options = await candidateChecker.FindForProviderAsync(
                            provider, resolution.Route!, identity, format.MediaType, searchCancellation.Token);

                        if (options.Count == 0)
                        {
                            AddAttempt(request, format, provider, ProviderAttemptOutcome.NoMatch,
                                "No matching candidate was reported by this provider.", nextCheck);
                            continue;
                        }

                        var reviewableOptions = format.MediaType == RequestMediaType.Audiobook
                            ? options.Where(option => AudiobookFormatPolicy.IsUsableForAutomaticAcquisition(option.Format)).ToArray()
                            : options;
                        reviewableOptions = CollapseEquivalentReviewOptions(reviewableOptions);
                        if (reviewableOptions.Count == 0)
                        {
                            AddAttempt(request, format, provider, ProviderAttemptOutcome.NoMatch,
                                "The provider reported only audiobook formats excluded from automatic acquisition.", nextCheck);
                            continue;
                        }

                        // Identifier evidence is strongest, but identical normalized title and
                        // observed-author records are also a deterministic single-work choice.
                        // This does not apply to the broad TitleAuthor fallback, derivatives,
                        // or a language conflict. The selected option is fetched exactly once;
                        // failure reaches review rather than triggering a fallback download.
                        var automaticMatches = reviewableOptions
                            .Where(option =>
                                (option.MatchBasis is BookMatchBasis.Identifier or BookMatchBasis.StrictTitleAuthor) &&
                                !option.RequiresLanguageConfirmation &&
                                (!option.RequiresReleaseConfirmation || IsUnknownDrmOnlyConcern(option)))
                            .ToArray();

                        // A narration-aware winner replaces raw format-rank
                        // collapsing here too -- same selector, same rules,
                        // as the built-in Gutenberg path. Unlike that fully
                        // trusted path, anything short of exactly one clean
                        // winner still falls through to this method's existing
                        // conservative review fallback below (reviewableOptions),
                        // matching this service's doc comment: for an
                        // admin-registered provider, weaker evidence is always
                        // for a librarian, never silent automatic acquisition.
                        AudiobookCandidateSelectionResult? audiobookSelection = null;
                        if (format.MediaType == RequestMediaType.Audiobook)
                        {
                            var preference = await GetNarrationPreferenceAsync(request.UserId, cancellationToken);
                            audiobookSelection = AudiobookCandidateSelector.Select(automaticMatches, preference);
                            automaticMatches = audiobookSelection.Winner is { } winner ? [winner] : [];
                        }

                        if (automaticMatches.Length == 1 && provider.AutoAcquireEnabled)
                        {
                            var acquireResult = await security.AcquireAndEvaluateAsync(
                                request.Id,
                                format.Id,
                                provider.ProviderId,
                                automaticMatches[0].ProviderResultId,
                                cancellationToken,
                                allowDownloadTimeDrmValidation: IsUnknownDrmOnlyConcern(automaticMatches[0]));

                            if (acquireResult.Outcome == ManualImportOutcome.Success)
                            {
                                AddAttempt(request, format, provider, ProviderAttemptOutcome.Acquired,
                                    audiobookSelection?.DecisionReason ?? AudiobookFormatPolicy.DescribeAcquiredOption(automaticMatches[0]),
                                    nextEligibleCheckAtUtc: null);
                                break;
                            }

                            if (acquireResult.Outcome == ManualImportOutcome.AcquisitionInProgress)
                            {
                                // Protocol v2: the provider accepted a durable
                                // job rather than returning bytes immediately.
                                // AcquisitionJobPollingService drives it to
                                // completion -- this is progress, not a
                                // failure, so it must not route to review.
                                AddAttempt(request, format, provider, ProviderAttemptOutcome.Submitted,
                                    "A high-confidence copy acquisition was submitted and is being tracked to completion.",
                                    nextEligibleCheckAtUtc: null);
                                break;
                            }

                            AddAttempt(request, format, provider, ProviderAttemptOutcome.Failed,
                                acquireResult.Error ?? "The automatic copy could not be acquired.", nextEligibleCheckAtUtc: null);
                            await MarkForReviewAsync(
                                request, work.Title, acquireResult.Error ?? "The automatic copy could not be acquired.",
                                cancellationToken);
                            break;
                        }

                        AddAttempt(request, format, provider, ProviderAttemptOutcome.CandidatesFound,
                            $"Found {reviewableOptions.Count} candidate(s); choose a reviewed candidate before acquisition.",
                            nextEligibleCheckAtUtc: null);
                        await MarkForCandidateReviewAsync(
                            request, format, provider, work.Title, work.PrimaryAuthor, reviewableOptions,
                            DescribeCandidateReviewReason(reviewableOptions, provider.AutoAcquireEnabled), cancellationToken);
                        break;
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested &&
                                                           searchCancellation?.IsCancellationRequested == true)
                    {
                        AddAttempt(request, format, provider, ProviderAttemptOutcome.Failed,
                            $"The provider search did not complete within {BackgroundSearchTimeout.TotalMinutes:0} minutes and will be retried on its configured schedule.", nextCheck);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        AddAttempt(request, format, provider, ProviderAttemptOutcome.Failed,
                            "The provider canceled the lookup and it will be retried on its configured schedule.", nextCheck);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception) when (exception is HttpRequestException or CryptographicException)
                    {
                        AddAttempt(request, format, provider, ProviderAttemptOutcome.Failed,
                            "The provider lookup failed and will be retried on its configured schedule.", nextCheck);
                    }
                    finally
                    {
                        searchCancellation?.Dispose();
                    }
                }
            }

            await attempts.SaveChangesAsync(cancellationToken);
            await requests.SaveChangesAsync(cancellationToken);
        }

        return checks;
    }

    private static bool IsDue(ProviderAttempt? latest, BookRequest request, DateTimeOffset now) =>
        // A cancellation followed by "Ask again" begins a new request cycle.
        // Preserve previous attempts for the audit trail, but do not make their
        // next-check timestamp block the new request.
        latest is null || latest.AttemptedAtUtc < request.StatusChangedAtUtc ||
        latest.NextEligibleCheckAtUtc is { } next && next <= now;

    /// <summary>
    /// An external provider can report multiple opaque references for one
    /// edition and release. Those references are server-side acquisition
    /// handles, not meaningful requester choices, so retain the first one
    /// once all neutral presentation facts agree.
    /// </summary>
    private static FulfillmentOption[] CollapseEquivalentReviewOptions(
        IEnumerable<FulfillmentOption> options) =>
        options
            .GroupBy(option => new ReviewCandidatePresentationKey(
                NormalizeReviewFact(option.ProviderId),
                option.MediaType,
                NormalizeReviewFact(option.Format),
                NormalizeReviewFact(option.Language),
                option.PublicationYear,
                NormalizeReviewFact(option.Publisher),
                option.SizeBytes,
                option.PartCount,
                option.ProviderPopularity,
                option.IsAbridged,
                option.IsUnabridged,
                option.NarrationKind,
                NormalizeReviewFact(option.Narrator),
                option.RequiresLanguageConfirmation,
                option.RequiresReleaseConfirmation,
                NormalizeReviewFact(option.DrmStatus),
                NormalizeReviewFact(option.ReleaseConcern)))
            .Select(group => group.First())
            .ToArray();

    private static string? NormalizeReviewFact(string? value) => string.IsNullOrWhiteSpace(value)
        ? null
        : string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    private static TimeSpan ToInterval(ProviderRecheckSchedule schedule) => schedule switch
    {
        ProviderRecheckSchedule.Daily => TimeSpan.FromDays(1),
        ProviderRecheckSchedule.Weekly => TimeSpan.FromDays(7),
        _ => throw new ArgumentOutOfRangeException(nameof(schedule), schedule, "Only scheduled providers may be rechecked.")
    };

    private sealed record ReviewCandidatePresentationKey(
        string? ProviderId,
        RequestMediaType MediaType,
        string? Format,
        string? Language,
        int? PublicationYear,
        string? Publisher,
        long? SizeBytes,
        int? PartCount,
        int? ProviderPopularity,
        bool? IsAbridged,
        bool? IsUnabridged,
        NarrationKind? NarrationKind,
        string? Narrator,
        bool RequiresLanguageConfirmation,
        bool RequiresReleaseConfirmation,
        string? DrmStatus,
        string? ReleaseConcern);

    private async Task<AudiobookNarrationPreference> GetNarrationPreferenceAsync(
        Guid requesterUserId, CancellationToken cancellationToken)
    {
        var account = await accounts.FindAsync(requesterUserId, cancellationToken);
        return account?.AudiobookNarrationPreference ?? AudiobookNarrationPreference.PreferHuman;
    }

    private static bool IsUnknownDrmOnlyConcern(FulfillmentOption option) =>
        option.DrmStatus == "unknown" &&
        string.Equals(
            option.ReleaseConcern,
            ExternalReleasePolicy.UnknownDrmConfirmationReason,
            StringComparison.Ordinal);

    private void AddAttempt(
        BookRequest request,
        RequestFormat format,
        ExternalProvider provider,
        ProviderAttemptOutcome outcome,
        string summary,
        DateTimeOffset? nextEligibleCheckAtUtc) =>
        attempts.Add(new ProviderAttempt(
            request.Id, format.Id, provider.ProviderId, outcome, summary, clock.UtcNow, nextEligibleCheckAtUtc));

    private async Task MarkForReviewAsync(
        BookRequest request, string workTitle, string reason, CancellationToken cancellationToken)
    {
        if (request.Status != RequestStatus.PendingAcquisition)
        {
            return;
        }

        request.TransitionTo(RequestStatus.NeedsReview, actorUserId: null, reason, clock.UtcNow);
        await notifications.RecordRequestNeedsReviewAsync(request.Id, workTitle, reason, cancellationToken);
    }

    /// <summary>
    /// A provider search that yields reviewable evidence must carry its
    /// candidate list into the same requester/admin preference flow used by
    /// built-in providers. A generic NeedsReview state without selections is
    /// a dead end: the user can see that a provider found something but FL has
    /// discarded the only references that could be safely sent back to it.
    /// </summary>
    private async Task MarkForCandidateReviewAsync(
        BookRequest request,
        RequestFormat format,
        ExternalProvider provider,
        string workTitle,
        string? workAuthor,
        IReadOnlyList<FulfillmentOption> options,
        string reason,
        CancellationToken cancellationToken)
    {
        if (request.Status != RequestStatus.PendingAcquisition)
        {
            return;
        }

        // The stored review may collapse records that are identical to the
        // requester, so do not report the raw provider result count as though
        // it were the number of choices a person will see.
        request.MarkNeedsReview(
            RequestReviewCategory.PreferenceAmbiguity,
            reason,
            clock.UtcNow,
            options.Select(option => (
                format.Id,
                option.ProviderId,
                option.ProviderResultId,
                workTitle,
                workAuthor,
                option.Language,
                RequestReviewCandidatePresentation.BuildDetails(option),
                option.AdminInspectionUri?.ToString())).ToArray());
        await notifications.RecordRequestNeedsReviewAsync(request.Id, workTitle, reason, cancellationToken);
        foreach (var requesterId in request.ActiveRequesterIds)
        {
            await notifications.RecordPreferenceAmbiguityAsync(
                requesterId, request.Id, workTitle, reason, cancellationToken);
        }
    }

    private static string DescribeCandidateReviewReason(
        IReadOnlyList<FulfillmentOption> options,
        bool autoAcquireEnabled)
    {
        var hasConfirmedWorkIdentity = options.Any(option =>
            option.MatchBasis is BookMatchBasis.Identifier or BookMatchBasis.StrictTitleAuthor);

        if (!hasConfirmedWorkIdentity)
        {
            return "Possible copies were found, but their titles could not be confirmed as the requested work. A librarian must verify the source before acquisition.";
        }

        return autoAcquireEnabled
            ? "Several matching copies need a librarian comparison before acquisition."
            : "Matching copies were found, but automatic acquisition is disabled. A librarian will verify and select one.";
    }
}
