using FamilyLibrarian.Application.Accounts;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Notifications;
using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Domain.Accounts;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Application.Acquisition;

/// <summary>
/// Moves a request through unattended acquisition whenever an explicitly
/// opt-in provider returns one high-confidence direct-download match — and
/// keeps retrying on its own, once the cooldown elapses, for as long as none
/// has found anything yet.
/// </summary>
/// <remarks>
/// This service intentionally cannot use store offers, external actions, or
/// admin-registered external providers. Those options may be useful to show a
/// librarian, but do not carry the provider-specific confidence guarantee this
/// workflow requires. Every acquired file still enters quarantine, is scanned,
/// structurally validated, identity-checked, and only then sent to its library.
/// <para>
/// A request only ever reaches <see cref="RequestStatus.NeedsReview"/> from
/// here for the two cases that genuinely need a librarian's judgment:
/// different providers confidently disagreeing on which file is the right
/// one, or a downloaded file failing its post-download security/identity
/// check. Coming up empty is not one of those — that just means "not yet",
/// so the request stays in the automatic queue and tries again later.
/// </para>
/// </remarks>
/// <summary>Outcome of resolving a SELFSERV-1 <see cref="RequestReviewCategory.PreferenceAmbiguity"/> review.</summary>
public enum PreferenceAmbiguityResolutionOutcome
{
    Resolved,
    NotFound,
    Unauthenticated,
    Conflict
}

public sealed class AutomaticRequestFulfillmentService(
    IRequestRepository requests,
    IProviderAttemptRepository attempts,
    IEnumerable<IAutomaticDirectAcquisitionProvider> providers,
    DirectAcquisitionSecurityService acquisition,
    IClock clock,
    NotificationService notifications,
    ICurrentUser currentUser,
    IUserAccountStore accounts)
{
    private const int BatchSize = 20;

    /// <summary>
    /// How long to wait before asking the same provider about the same format
    /// again after it found nothing. A free catalog's contents change slowly,
    /// so this trades a little latency for not hammering an unauthenticated
    /// API every poll cycle for a book it has already said it doesn't have.
    /// </summary>
    private static readonly TimeSpan RetryCooldown = TimeSpan.FromDays(1);

    public async Task<int> ProcessPendingAsync(CancellationToken cancellationToken)
    {
        var automaticProviders = providers.ToArray();
        if (automaticProviders.Length == 0)
        {
            return 0;
        }

        var pending = await requests.ListPendingForAutomaticFulfillmentAsync(
            BatchSize, cancellationToken, includeNeedsReview: true);
        var processed = 0;

        foreach (var request in pending)
        {
            if (request.RequiresManualFulfillment) continue;
            var refreshLegacyReview = IsLegacyCollapsedPreferenceReview(request);
            var reviewedFormatIds = request.Status == RequestStatus.NeedsReview && !refreshLegacyReview
                ? request.ReviewCandidates.Select(candidate => candidate.RequestFormatId).ToHashSet()
                : [];
            foreach (var format in request.Formats.Where(format =>
                format.Status == RequestFormatStatus.Requested && !reviewedFormatIds.Contains(format.Id)))
            {
                if (await requests.HasAcquiredArtifactAsync(format.Id, cancellationToken))
                {
                    continue;
                }

                var options = new List<FulfillmentOption>();
                foreach (var provider in automaticProviders)
                {
                    var latestAttempt = await attempts.FindLatestForFormatAsync(
                        format.Id, provider.Id, cancellationToken);
                    // A pre-evidence review needs one immediate refresh even
                    // when its earlier lookup is inside the normal cooldown.
                    if (!refreshLegacyReview && HasRecentAttempt(latestAttempt, request))
                    {
                        continue;
                    }

                    if (!await provider.IsReadyAsync(cancellationToken))
                    {
                        // Not broken, just not ready yet (e.g. a local catalogue
                        // mid-import) -- skip silently rather than recording a
                        // "no match" that would start this provider's retry
                        // cooldown on a lookup that was never really asked.
                        continue;
                    }

                    try
                    {
                        var providerOptions = await provider.FindDirectAcquisitionsAsync(
                            request.WorkId,
                            format.MediaType,
                            cancellationToken);
                        options.AddRange(providerOptions);
                        var onlyExcludedAudiobookFormats = format.MediaType == RequestMediaType.Audiobook &&
                                                           providerOptions.Count > 0 &&
                                                           providerOptions.All(option =>
                                                               !AudiobookFormatPolicy.IsUsableForAutomaticAcquisition(option.Format));
                        attempts.Add(new ProviderAttempt(
                            request.Id, format.Id, provider.Id,
                            providerOptions.Count == 0 || onlyExcludedAudiobookFormats
                                ? ProviderAttemptOutcome.NoMatch
                                : ProviderAttemptOutcome.CandidatesFound,
                            providerOptions.Count == 0
                                ? "No high-confidence automatic copy was found."
                                : onlyExcludedAudiobookFormats
                                    ? "The provider reported only audiobook formats excluded from automatic acquisition."
                                : DescribeProviderCandidates(providerOptions),
                            clock.UtcNow,
                            nextEligibleCheckAtUtc: null));
                    }
                    catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
                    {
                        attempts.Add(new ProviderAttempt(
                            request.Id, format.Id, provider.Id, ProviderAttemptOutcome.Failed,
                            DescribeProviderFailure(exception),
                            clock.UtcNow,
                            nextEligibleCheckAtUtc: null));
                    }
                }

                // A previously-declined candidate ("keep looking") must not
                // come back on the very next pass just because the retry cooldown
                // was bypassed by the dismissal itself -- only a genuinely
                // different provider result is offered again.
                var declined = request.DeclinedCandidates
                    .Where(candidate => candidate.RequestFormatId == format.Id)
                    .Select(candidate => (candidate.ProviderId, candidate.ProviderResultId))
                    .ToHashSet(StringTupleComparer.OrdinalIgnoreCase);

                var distinctOptions = options
                    .GroupBy(option => (option.ProviderId, option.ProviderResultId), StringTupleComparer.OrdinalIgnoreCase)
                    .Select(group => group.Single())
                    .Where(option => !declined.Contains((option.ProviderId, option.ProviderResultId)))
                    .ToArray();

                // A RequiresLanguageConfirmation option (ACCURACY-1: title/author
                // matched, but every result was excluded by LanguageAcceptance)
                // must never be auto-selected here -- it is kept separate so it
                // can be offered to the requester as a preference decision below
                // instead of silently acquired or silently discarded.
                var automaticFormatOptions = format.MediaType == RequestMediaType.Audiobook
                    ? distinctOptions.Where(option => AudiobookFormatPolicy.IsUsableForAutomaticAcquisition(option.Format)).ToArray()
                    : distinctOptions;
                var autoEligible = automaticFormatOptions.Where(option => !option.RequiresLanguageConfirmation).ToArray();
                var languageExcluded = automaticFormatOptions.Where(option => option.RequiresLanguageConfirmation).ToArray();

                // Cross-record audiobook selection -- narration preference,
                // completeness, then packaging/popularity/ID as late
                // tiebreakers -- resolves only same-source candidates.
                // Different sources remain a real trust disagreement (below).
                // Multiple acceptable candidates never force a review by
                // themselves: AudiobookCandidateSelector's comparator chain
                // always ends in a stable-ID tiebreak, so it always produces
                // exactly one winner from an acceptable candidate set. The
                // one exception is genuine unresolved uncertainty -- a
                // HumanOnly requirement no candidate can confirm satisfying.
                if (format.MediaType == RequestMediaType.Audiobook &&
                    autoEligible.Length > 0 &&
                    autoEligible.Select(option => option.ProviderId).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
                {
                    var preference = await GetNarrationPreferenceAsync(request.UserId, cancellationToken);
                    var selection = AudiobookCandidateSelector.Select(autoEligible, preference);
                    if (selection.Winner is { } winner)
                    {
                        autoEligible = [winner with { AutomaticSelectionReason = selection.DecisionReason }];
                    }
                    else if (selection.CandidatesRequiringNarrationConfirmation.Count > 0)
                    {
                        await MarkForReviewAsync(
                            request, RequestReviewCategory.PreferenceAmbiguity,
                            selection.DecisionReason, cancellationToken,
                            selection.CandidatesRequiringNarrationConfirmation.Select(option => (format.Id, option.ProviderId, option.ProviderResultId,
                                option.Title, option.Author, option.Language,
                                RequestReviewCandidatePresentation.BuildDetails(option),
                                option.AdminInspectionUri?.ToString()))
                                .ToArray());
                        await attempts.SaveChangesAsync(cancellationToken);
                        await requests.SaveChangesAsync(cancellationToken);
                        continue;
                    }
                    else
                    {
                        autoEligible = [];
                    }
                }

                if (autoEligible.Length > 1)
                {
                    if (autoEligible.Select(option => option.ProviderId).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
                    {
                        // One provider found multiple plausible editions of the
                        // same work -- per SELFSERV-1, an edition preference is
                        // the requester's call, not a trust/safety concern, so
                        // this is routed the same way as a language-excluded
                        // result rather than the cross-provider case below.
                        await MarkForReviewAsync(
                            request, RequestReviewCategory.PreferenceAmbiguity,
                            DescribeSameProviderAmbiguity(autoEligible), cancellationToken,
                            autoEligible.Select(option => (format.Id, option.ProviderId, option.ProviderResultId,
                                option.Title, option.Author, option.Language,
                                RequestReviewCandidatePresentation.BuildDetails(option),
                                option.AdminInspectionUri?.ToString()))
                                .ToArray());
                        await attempts.SaveChangesAsync(cancellationToken);
                        await requests.SaveChangesAsync(cancellationToken);
                        // A request can ask for both ebook and audiobook. An
                        // ambiguity in one format must not prevent a separate
                        // requested format with one safe candidate from being
                        // acquired in this same pass.
                        continue;
                    }

                    // Different providers confidently disagree on the file. Picking
                    // one automatically risks shipping the wrong edition, so this is
                    // the one "found something" case that still needs a librarian.
                    await MarkForReviewAsync(
                        request, RequestReviewCategory.ProviderDisagreement,
                        "More than one high-confidence automatic copy was found.", cancellationToken);
                    await attempts.SaveChangesAsync(cancellationToken);
                    await requests.SaveChangesAsync(cancellationToken);
                    continue;
                }

                if (autoEligible.Length == 0)
                {
                    if (languageExcluded.Length > 0)
                    {
                        // Something was found, but only in a language the requester
                        // didn't ask for -- a preference decision, not a trust/safety
                        // judgment, so it goes to them (SELFSERV-1), not just an admin.
                        await MarkForReviewAsync(
                            request, RequestReviewCategory.PreferenceAmbiguity,
                            "A copy was found, but not in English.", cancellationToken,
                            languageExcluded.Select(option => (format.Id, option.ProviderId, option.ProviderResultId,
                                option.Title, option.Author, option.Language,
                                RequestReviewCandidatePresentation.BuildDetails(option),
                                option.AdminInspectionUri?.ToString()))
                                .ToArray());
                        await attempts.SaveChangesAsync(cancellationToken);
                        await requests.SaveChangesAsync(cancellationToken);
                        continue;
                    }

                    // Nothing found yet, not a failure — leave the request in the
                    // automatic queue. The cooldown above means this format is tried
                    // again once it elapses, with no librarian action needed.
                    await attempts.SaveChangesAsync(cancellationToken);
                    continue;
                }

                if (await AcquireOptionAsync(request, format, autoEligible[0], cancellationToken))
                {
                    processed++;
                }
                else
                {
                    // The failed format is now in review, but another requested
                    // format can still be fulfilled automatically.
                    continue;
                }
            }
        }

        return processed;
    }

    /// <summary>
    /// Acquires one already-chosen option and records the outcome -- shared by
    /// the automatic single-match path above and by
    /// <see cref="ResolvePreferenceAmbiguityAsync"/> ("get it anyway"), which
    /// hands this the specific candidate the requester picked instead of an
    /// automatically-selected one.
    /// </summary>
    private async Task<bool> AcquireOptionAsync(
        BookRequest request,
        RequestFormat format,
        FulfillmentOption option,
        CancellationToken cancellationToken,
        bool confirmLowConfidenceMatch = false)
    {
        ManualImportResult result;
        try
        {
            result = await acquisition.AcquireAndEvaluateAsync(
                request.Id,
                format.Id,
                option.ProviderId,
                option.ProviderResultId,
                cancellationToken,
                confirmLowConfidenceMatch);
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            // A transport-level failure mid-download (e.g. the source
            // closing an idle connection partway through a multi-file
            // audiobook fetch), or the security/approval pipeline
            // rejecting the asset's state (AutomatedSecurityPipeline
            // throws InvalidOperationException when approval fails
            // for a reason other than an identity mismatch), must not
            // abort the whole batch — every other pending request
            // would silently stop being processed until the next
            // poll. Treat it exactly like an acquisition failure
            // below: unlike a search-phase failure (see
            // DescribeProviderFailure), this sends the request to
            // review rather than retrying on its own, so the reason
            // should not claim otherwise.
            result = ManualImportResult.Invalid(
                $"The file could not be processed: {exception.Message}");
        }

        if (result.Outcome != ManualImportOutcome.Success)
        {
            attempts.Add(new ProviderAttempt(
                request.Id, format.Id, option.ProviderId, ProviderAttemptOutcome.Failed,
                result.Error ?? "The automatic copy could not be acquired.", clock.UtcNow,
                nextEligibleCheckAtUtc: null));
            await MarkForReviewAsync(
                request, RequestReviewCategory.SecurityOrIdentityFailure,
                result.Error ?? "The automatic copy could not be acquired.", cancellationToken);
            await attempts.SaveChangesAsync(cancellationToken);
            await requests.SaveChangesAsync(cancellationToken);
            return false;
        }

        attempts.Add(new ProviderAttempt(
            request.Id, format.Id, option.ProviderId, ProviderAttemptOutcome.Acquired,
            AudiobookFormatPolicy.DescribeAcquiredOption(option), clock.UtcNow,
            nextEligibleCheckAtUtc: null));
        await attempts.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// SELFSERV-1: the requester accepted a specific
    /// <see cref="RequestReviewCategory.PreferenceAmbiguity"/> candidate ("get
    /// it anyway"), scoped to their own request -- mirrors
    /// <c>DeliveryAttemptService.RetryAsync</c>'s owner-check shape.
    /// </summary>
    public async Task<PreferenceAmbiguityResolutionOutcome> ResolvePreferenceAmbiguityAsync(
        Guid requestId, Guid candidateId, uint? expectedVersion, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return PreferenceAmbiguityResolutionOutcome.Unauthenticated;
        }

        // The pre-lock lookup deliberately uses the projected view, not
        // FindOwnedRequestAsync -- a tracking query here would be cached by
        // EF's identity map, so the "reload" inside the lock below would
        // silently return that same stale tracked instance instead of a
        // fresh row (missing the concurrent-update case F4 exists to catch).
        // Mirrors BookRequestService.TransitionAsync's FindViewAsync usage.
        var initial = await requests.FindViewAsync(requestId, userId, cancellationToken);
        if (initial is null)
        {
            return PreferenceAmbiguityResolutionOutcome.NotFound;
        }

        return await requests.InCreateRequestScopeAsync(userId, initial.WorkId, async token =>
        {
            var request = await requests.FindOwnedRequestAsync(requestId, userId, token);
            return request is null || !IsActiveParticipant(request, userId)
                ? PreferenceAmbiguityResolutionOutcome.NotFound
                : await AcceptCandidateAsync(request, candidateId, expectedVersion, userId, token);
        }, cancellationToken);
    }

    /// <summary>Admin counterpart of <see cref="ResolvePreferenceAmbiguityAsync"/> -- no ownership check, additive per SELFSERV-1.</summary>
    public async Task<PreferenceAmbiguityResolutionOutcome> AdminResolvePreferenceAmbiguityAsync(
        Guid requestId, Guid candidateId, uint? expectedVersion, CancellationToken cancellationToken)
    {
        // See the remark on ResolvePreferenceAmbiguityAsync -- FindAdminViewAsync
        // is a projection, so it does not pre-track the entity the lock's
        // reload needs to actually re-read.
        var initial = await requests.FindAdminViewAsync(requestId, cancellationToken);
        if (initial is null)
        {
            return PreferenceAmbiguityResolutionOutcome.NotFound;
        }

        var actorUserId = currentUser.UserId;
        return await requests.InCreateRequestScopeAsync(actorUserId ?? Guid.Empty, initial.Request.WorkId, async token =>
        {
            var request = await requests.FindRequestForAdminAsync(requestId, token);
            return request is null
                ? PreferenceAmbiguityResolutionOutcome.NotFound
                : await AcceptCandidateAsync(request, candidateId, expectedVersion, actorUserId, token);
        }, cancellationToken);
    }

    /// <summary>
    /// SELFSERV-1: the requester declined every offered candidate ("keep
    /// looking"), scoped to their own request. Returns the request to
    /// automatic acquisition, which will try again once each provider's retry
    /// cooldown elapses.
    /// </summary>
    public async Task<PreferenceAmbiguityResolutionOutcome> DismissPreferenceAmbiguityAsync(
        Guid requestId, uint? expectedVersion, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return PreferenceAmbiguityResolutionOutcome.Unauthenticated;
        }

        var initial = await requests.FindViewAsync(requestId, userId, cancellationToken);
        if (initial is null)
        {
            return PreferenceAmbiguityResolutionOutcome.NotFound;
        }

        return await requests.InCreateRequestScopeAsync(userId, initial.WorkId, async token =>
        {
            var request = await requests.FindOwnedRequestAsync(requestId, userId, token);
            return request is null || !IsActiveParticipant(request, userId)
                ? PreferenceAmbiguityResolutionOutcome.NotFound
                : await DismissAsync(request, expectedVersion, userId, token);
        }, cancellationToken);
    }

    /// <summary>Admin counterpart of <see cref="DismissPreferenceAmbiguityAsync"/> -- no ownership check.</summary>
    public async Task<PreferenceAmbiguityResolutionOutcome> AdminDismissPreferenceAmbiguityAsync(
        Guid requestId, uint? expectedVersion, CancellationToken cancellationToken)
    {
        var initial = await requests.FindAdminViewAsync(requestId, cancellationToken);
        if (initial is null)
        {
            return PreferenceAmbiguityResolutionOutcome.NotFound;
        }

        var actorUserId = currentUser.UserId;
        return await requests.InCreateRequestScopeAsync(actorUserId ?? Guid.Empty, initial.Request.WorkId, async token =>
        {
            var request = await requests.FindRequestForAdminAsync(requestId, token);
            return request is null
                ? PreferenceAmbiguityResolutionOutcome.NotFound
                : await DismissAsync(request, expectedVersion, actorUserId, token);
        }, cancellationToken);
    }

    /// <summary>
    /// Callers reload <paramref name="request"/> fresh, inside
    /// <see cref="IRequestRepository.InCreateRequestScopeAsync{TResult}"/>'s
    /// advisory lock on its Work, immediately before calling this -- the
    /// combination is what actually closes the race two concurrent
    /// resolutions could otherwise hit: checking <see cref="BookRequest.Version"/> against a version loaded
    /// before the lock was acquired would let both callers pass the check.
    /// </summary>
    private async Task<PreferenceAmbiguityResolutionOutcome> AcceptCandidateAsync(
        BookRequest request, Guid candidateId, uint? expectedVersion, Guid? actorUserId, CancellationToken cancellationToken)
    {
        if (expectedVersion is not null && request.Version != expectedVersion)
        {
            return PreferenceAmbiguityResolutionOutcome.Conflict;
        }

        RequestReviewCandidate candidate;
        try
        {
            candidate = request.AcceptReviewCandidate(candidateId, actorUserId, clock.UtcNow);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            return PreferenceAmbiguityResolutionOutcome.NotFound;
        }

        var format = request.Formats.Single(candidateFormat => candidateFormat.Id == candidate.RequestFormatId);
        await requests.SaveChangesAsync(cancellationToken);

        var option = new FulfillmentOption(
            candidate.ProviderId, candidate.ProviderResultId, request.WorkId, EditionId: null,
            format.MediaType, OptionKind.DirectAcquisition, AcquisitionMethod.DirectDownload,
            Format: null, candidate.Language, Quality: null, Availability: null, Cost: null, Currency: null,
            LicenseOrUsageStatus: null, DrmStatus: null, ExternalActionUri: null, ProviderData: null);

        // Whether or not the acquisition itself succeeds, the review action
        // has been resolved -- a failure already re-flags the request as
        // SecurityOrIdentityFailure via AcquireOptionAsync, which the caller
        // will see on its next load.
        await AcquireOptionAsync(request, format, option, cancellationToken, confirmLowConfidenceMatch: true);
        return PreferenceAmbiguityResolutionOutcome.Resolved;
    }

    /// <summary>See the remark on <see cref="AcceptCandidateAsync"/> -- same locking contract.</summary>
    private async Task<PreferenceAmbiguityResolutionOutcome> DismissAsync(
        BookRequest request, uint? expectedVersion, Guid? actorUserId, CancellationToken cancellationToken)
    {
        if (expectedVersion is not null && request.Version != expectedVersion)
        {
            return PreferenceAmbiguityResolutionOutcome.Conflict;
        }

        try
        {
            request.DismissReviewPreference(actorUserId, clock.UtcNow);
        }
        catch (InvalidOperationException)
        {
            return PreferenceAmbiguityResolutionOutcome.NotFound;
        }

        await requests.SaveChangesAsync(cancellationToken);
        return PreferenceAmbiguityResolutionOutcome.Resolved;
    }

    /// <summary>
    /// <see cref="IRequestRepository.FindOwnedRequestAsync"/>
    /// deliberately still returns a request for a withdrawn participant (so
    /// BookRequestService.TransitionAsync can let them reopen it), so a
    /// requester-facing review resolution must check active participation
    /// itself rather than relying on the lookup to have done it.
    /// </summary>
    private static bool IsActiveParticipant(BookRequest request, Guid userId) =>
        request.Participants.Any(participant => participant.UserId == userId && participant.WithdrawnAtUtc is null);

    /// <summary>
    /// Whether this provider was already asked about this format recently
    /// enough to skip asking it again — nothing has changed since (no status
    /// change) and the cooldown has not elapsed yet. Cancelling and reopening
    /// a request, or a librarian's manual recheck, updates
    /// <see cref="BookRequest.StatusChangedAtUtc"/>, which is what lets this
    /// bypass the cooldown immediately instead of waiting out the full period.
    /// </summary>
    private bool HasRecentAttempt(ProviderAttempt? attempt, BookRequest request) =>
        attempt is not null &&
        attempt.AttemptedAtUtc >= request.StatusChangedAtUtc &&
        attempt.AttemptedAtUtc >= clock.UtcNow - RetryCooldown;

    private async Task MarkForReviewAsync(
        BookRequest request,
        RequestReviewCategory category,
        string reason,
        CancellationToken cancellationToken,
        IReadOnlyList<(Guid RequestFormatId, string ProviderId, string ProviderResultId, string? Title, string? Author, string? Language, string? Details, string? AdminInspectionUri)>? candidateOptions = null)
    {
        var refreshLegacyReview = category == RequestReviewCategory.PreferenceAmbiguity &&
                                  candidateOptions is { Count: > 0 } &&
                                  IsLegacyCollapsedPreferenceReview(request);
        if (request.Status != RequestStatus.PendingAcquisition && !refreshLegacyReview)
        {
            return;
        }

        var view = await requests.FindAdminViewAsync(request.Id, cancellationToken);
        var workTitle = view?.Request.WorkTitle ?? request.WorkId.ToString();

        // The title/author are FL's canonical catalog facts, never raw labels
        // from the provider. A raw release title can carry filename/source
        // debris; the neutral details below are the only provider evidence a
        // requester needs to distinguish an edition.
        var workAuthor = view?.Request.Authors is { Count: > 0 } authors ? authors[0] : null;
        var candidates = candidateOptions?
            .Select(option => (option.RequestFormatId, option.ProviderId, option.ProviderResultId,
                Title: workTitle, Author: workAuthor, option.Language,
                option.Details, option.AdminInspectionUri))
            .ToArray();
        if (refreshLegacyReview)
        {
            request.RefreshPreferenceReview(reason, clock.UtcNow, candidates!);
        }
        else
        {
            request.MarkNeedsReview(category, reason, clock.UtcNow, candidates);
        }

        // Admin can still resolve a PreferenceAmbiguity item too (additive,
        // not exclusive), so this fires unconditionally for every category.
        if (!refreshLegacyReview)
        {
            await notifications.RecordRequestNeedsReviewAsync(request.Id, workTitle, reason, cancellationToken);
        }

        if (!refreshLegacyReview && category == RequestReviewCategory.PreferenceAmbiguity)
        {
            foreach (var requesterId in request.ActiveRequesterIds)
            {
                await notifications.RecordPreferenceAmbiguityAsync(requesterId, request.Id, workTitle, reason, cancellationToken);
            }
        }
    }

    private static string DescribeProviderFailure(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: { } statusCode } =>
            $"The automatic provider returned HTTP {(int)statusCode}; it will be tried again automatically.",
        TaskCanceledException =>
            "The automatic provider timed out; it will be tried again automatically.",
        _ => "The automatic provider could not be reached; it will be tried again automatically."
    };

    private static string DescribeProviderCandidates(IReadOnlyList<FulfillmentOption> options) =>
        options.Count == 1 && !string.IsNullOrWhiteSpace(options[0].AutomaticSelectionReason)
            ? options[0].AutomaticSelectionReason!
            : $"Found {options.Count} high-confidence automatic candidate(s).";

    private static bool IsLegacyCollapsedPreferenceReview(BookRequest request) =>
        request.Status == RequestStatus.NeedsReview &&
        request.ReviewCategory == RequestReviewCategory.PreferenceAmbiguity &&
        request.ReviewCandidates.Count == 1 &&
        string.Equals(
            request.StatusHistory.LastOrDefault()?.Reason,
            "Multiple plausible editions were found.",
            StringComparison.Ordinal);

    private async Task<AudiobookNarrationPreference> GetNarrationPreferenceAsync(
        Guid requesterUserId, CancellationToken cancellationToken)
    {
        var account = await accounts.FindAsync(requesterUserId, cancellationToken);
        return account?.AudiobookNarrationPreference ?? AudiobookNarrationPreference.PreferHuman;
    }

    private static string DescribeSameProviderAmbiguity(IReadOnlyList<FulfillmentOption> options)
    {
        const string gutenbergProviderId = "gutendex";
        const int minimumDownloads = 1_000;
        const double dominanceRatio = 3.0;

        var ranked = options.OrderByDescending(option => option.ProviderPopularity ?? 0).ToArray();
        if (ranked.Length >= 2 &&
            ranked.All(option => option.ProviderId.Equals(gutenbergProviderId, StringComparison.OrdinalIgnoreCase) &&
                                 option.ProviderPopularity is > 0) &&
            ranked[1].ProviderPopularity is { } runnerUpDownloads)
        {
            var leading = ranked[0];
            var leadingDownloads = leading.ProviderPopularity!.Value;
            var ratio = leadingDownloads / (double)runnerUpDownloads;
            return $"Project Gutenberg found {ranked.Length} eligible records. Its leading record " +
                   $"(#{leading.ProviderResultId}, {leadingDownloads:N0} downloads) is only " +
                   $"{ratio:0.#}× the runner-up (#{ranked[1].ProviderResultId}, {runnerUpDownloads:N0}). " +
                   $"Automatic selection requires at least {minimumDownloads:N0} downloads and a {dominanceRatio:0.#}× lead.";
        }

        return "Several eligible records were found, but no single record met the automatic-selection rule.";
    }

    private sealed class StringTupleComparer : IEqualityComparer<(string ProviderId, string ProviderResultId)>
    {
        public static readonly StringTupleComparer OrdinalIgnoreCase = new();

        public bool Equals((string ProviderId, string ProviderResultId) x, (string ProviderId, string ProviderResultId) y) =>
            string.Equals(x.ProviderId, y.ProviderId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.ProviderResultId, y.ProviderResultId, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string ProviderId, string ProviderResultId) value) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.ProviderId),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.ProviderResultId));
    }
}
