using System.Security.Cryptography;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Notifications;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Application.Requests;
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
    NotificationService notifications)
{
    private const int BatchSize = 20;

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

                    try
                    {
                        var identity = new BookIdentity(
                            work.Title, work.PrimaryAuthor, work.Isbn13s,
                            work.Authors, work.Series, work.Language, work.PublicationYear, work.Publisher);
                        var options = await candidateChecker.FindForProviderAsync(
                            provider, resolution.Route!, identity, format.MediaType, cancellationToken);

                        if (options.Count == 0)
                        {
                            AddAttempt(request, format, provider, ProviderAttemptOutcome.NoMatch,
                                "No matching candidate was reported by this provider.", nextCheck);
                            continue;
                        }

                        // A single ISBN-corroborated candidate is trusted the same way the
                        // manual acquire flow already trusts one (docs/04 §7) -- fetch, scan,
                        // and evaluate it automatically instead of waiting on a librarian --
                        // but only once the admin has separately opted this provider into
                        // automatic acquisition (AutoAcquireEnabled, plan §B): RecheckSchedule
                        // controls retry cadence for discovery only and never by itself
                        // authorizes an unattended fetch. Anything weaker (title/author only,
                        // unconfirmed, language-excluded, or more than one such candidate)
                        // still requires review regardless of that toggle. A release concern
                        // (docs/04 §7/§10/§16 -- a collection, a sample, an abridged mismatch)
                        // disqualifies a candidate from automatic acquisition even with a
                        // verified identifier match: a correct ISBN on an omnibus edition is
                        // still an omnibus, and that always needs a librarian's eyes, never a
                        // silent automatic fetch.
                        var identifierMatches = options
                            .Where(option =>
                                option.MatchBasis == BookMatchBasis.Identifier &&
                                !option.RequiresLanguageConfirmation &&
                                !option.RequiresReleaseConfirmation)
                            .ToArray();

                        if (identifierMatches.Length == 1 && provider.AutoAcquireEnabled)
                        {
                            var acquireResult = await security.AcquireAndEvaluateAsync(
                                request.Id, format.Id, provider.ProviderId, identifierMatches[0].ProviderResultId, cancellationToken);

                            if (acquireResult.Outcome == ManualImportOutcome.Success)
                            {
                                AddAttempt(request, format, provider, ProviderAttemptOutcome.Acquired,
                                    "A high-confidence copy was acquired and sent through the security pipeline.",
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
                            $"Found {options.Count} candidate(s); choose a reviewed candidate before acquisition.",
                            nextEligibleCheckAtUtc: null);
                        await MarkForCandidateReviewAsync(
                            request, format, provider, work.Title, options, cancellationToken);
                        break;
                    }
                    catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or CryptographicException)
                    {
                        AddAttempt(request, format, provider, ProviderAttemptOutcome.Failed,
                            "The provider lookup failed and will be retried on its configured schedule.", nextCheck);
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

    private static TimeSpan ToInterval(ProviderRecheckSchedule schedule) => schedule switch
    {
        ProviderRecheckSchedule.Daily => TimeSpan.FromDays(1),
        ProviderRecheckSchedule.Weekly => TimeSpan.FromDays(7),
        _ => throw new ArgumentOutOfRangeException(nameof(schedule), schedule, "Only scheduled providers may be rechecked.")
    };

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
        IReadOnlyList<FulfillmentOption> options,
        CancellationToken cancellationToken)
    {
        if (request.Status != RequestStatus.PendingAcquisition)
        {
            return;
        }

        var reason = $"{provider.DisplayName} found {options.Count} candidate(s) that need a choice before acquisition.";
        request.MarkNeedsReview(
            RequestReviewCategory.PreferenceAmbiguity,
            reason,
            clock.UtcNow,
            options.Select(option => (
                format.Id,
                option.ProviderId,
                option.ProviderResultId,
                option.Title ?? workTitle,
                option.Author,
                option.Language)).ToArray());
        await notifications.RecordRequestNeedsReviewAsync(request.Id, workTitle, reason, cancellationToken);
        foreach (var requesterId in request.ActiveRequesterIds)
        {
            await notifications.RecordPreferenceAmbiguityAsync(
                requesterId, request.Id, workTitle, reason, cancellationToken);
        }
    }
}
