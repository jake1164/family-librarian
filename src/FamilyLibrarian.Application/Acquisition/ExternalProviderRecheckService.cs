using System.Globalization;
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
/// cadence. Automatic downloads require confidence permitted by the provider's
/// effective level. Identity,
/// language, release and attempt-budget gates apply before submission; every
/// downloaded file still passes security and content validation.
/// </summary>
public sealed partial class ExternalProviderRecheckService(
    IRequestRepository requests,
    IProviderAttemptRepository attempts,
    IExternalProviderStore providers,
    ExternalCandidateAvailabilityChecker candidateChecker,
    DirectAcquisitionSecurityService security,
    IWorkLookup workLookup,
    IClock clock,
    NotificationService notifications,
    IUserAccountStore accounts,
    IAutomaticFulfillmentSignal? fulfillmentSignal = null,
    AudiobookPartSetAcquisitionService? partSets = null,
    SemanticAssessment.ICandidateSemanticShadow? semanticShadow = null,
    ManualImportPolicy? importPolicy = null,
    AcquisitionQuestionService? questions = null,
    ISavedAcquisitionQuestionAcceptor? savedQuestions = null,
    SemanticAssessment.SearchGuidanceService? searchGuidance = null)
{
    private const int BatchSize = 20;
    private static readonly TimeSpan BackgroundSearchTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan TransientRetryDelay = TimeSpan.FromMinutes(15);

    public async Task<int> ProcessDueAsync(CancellationToken cancellationToken)
    {
        var scheduledProviders = (await providers.ListEnabledAsync(cancellationToken))
            .Where(provider => provider.RecheckSchedule is ProviderRecheckSchedule.Daily or ProviderRecheckSchedule.Weekly)
            .ToArray();
        if (scheduledProviders.Length == 0)
        {
            return 0;
        }

        // Requests waiting on a person, or whose attempt budget is spent, stay in the batch (AF-10, OQ-5); least recently looked at
        // first so a batch of not-yet-due requests cannot starve the ones behind it.
        var pending = await requests.ListPendingForAutomaticFulfillmentAsync(
            BatchSize, cancellationToken, includeNeedsReview: true, includeSpentBudgetReviews: true,
            orderByLeastRecentlyAttempted: true);
        if (pending.Count == 0)
        {
            return 0;
        }

        var checks = 0;
        foreach (var request in pending)
        {
            if (request.QuestionKind is not null && savedQuestions is not null &&
                await savedQuestions.TryAcceptAsync(request.Id, cancellationToken)) continue;
            var work = await workLookup.FindAsync(request.WorkId, cancellationToken);
            if (questions is not null && work is not null) await questions.RemindAsync(request, work.Title, cancellationToken);
            foreach (var format in request.Formats.Where(format => format.Status == RequestFormatStatus.Requested))
            {
                var offers = new List<AcquisitionQuestionOffer>();
                var searchedProviders = 0;
                var advanced = false;
                // One download per format at a time: a second source starting the same book while the first is still
                // downloading only spends a (possibly metered) download on a duplicate.
                if (await requests.HasAcquiredArtifactAsync(format.Id, cancellationToken) ||
                    await requests.HasDownloadInProgressAsync(format.Id, cancellationToken))
                {
                    continue;
                }

                var prepared = await PrepareSearchGuidanceAsync(request, format, work, scheduledProviders, cancellationToken);
                foreach (var provider in scheduledProviders)
                {
                    var latest = await attempts.FindLatestForFormatAsync(format.Id, provider.ProviderId, cancellationToken);
                    if (!IsDue(latest, request, clock.UtcNow))
                    {
                        continue;
                    }

                    // A review that is not a preference question is rechecked only once the attempt budget is spent: that is the
                    // request left waiting for a better copy. Any other NeedsReview request belongs to a librarian.
                    if (request.Status == RequestStatus.NeedsReview &&
                        request.ReviewCategory != RequestReviewCategory.PreferenceAmbiguity &&
                        request.CountAutomaticCandidateFailures(format.Id, provider.ProviderId) < provider.AutomaticAttemptLimit &&
                        (questions is null || work is null || await questions.FindReserveAsync(request, format, work.Title, cancellationToken) is null))
                    {
                        continue;
                    }

                    checks++;
                    var nextCheck = clock.UtcNow + ToInterval(provider.RecheckSchedule);
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
                        // Candidates already ruled out for this format must not
                        // be nominated again -- a requester's "keep looking",
                        // and any candidate a previous unattended attempt
                        // downloaded and failed to verify. Passing them to the
                        // checker is what makes the retry loop advance: the
                        // confirmed single candidate is chosen from what is
                        // left, so the next pass picks the next-ranked record
                        // rather than re-nominating the one that just failed.
                        var exclusions = ExternalCandidateExclusions.From(
                            request.DeclinedCandidates, provider.ProviderId, format.Id);
                        // How long to leave this request before looking again when it is waiting: the provider cadence, or
                        // the AF-10 sliding scale from the publication (or request) date, whichever is longer.
                        var waitingNext = clock.UtcNow + MaxInterval(
                            ToInterval(provider.RecheckSchedule),
                            RecheckSchedule.WaitingInterval(work.FirstPublicationDate, request.RequestedAtUtc, clock.UtcNow));
                        var alreadyTried = request.CountAutomaticCandidateFailures(format.Id, provider.ProviderId) > 0;

                        // Several one-dimension searches are pooled, then judged once exactly as a single search was.
                        var retryPass = latest?.Outcome == ProviderAttemptOutcome.Retrying;
                        var cycleStart = DateTimeOffset.MinValue;
                        if (retryPass)
                        {
                            // The current cycle's searches all ran after the previous cycle's closing lookup (the second-latest
                            // lookup that was not itself a step of the retry loop). The cycle's own closing lookup is written
                            // after its searches, so it cannot be the boundary.
                            cycleStart = (await attempts.ListForRequestAsync(request.Id, cancellationToken))
                                .Where(attempt => attempt.RequestFormatId == format.Id &&
                                    string.Equals(attempt.ProviderId, provider.ProviderId, StringComparison.OrdinalIgnoreCase) &&
                                    attempt.Outcome != ProviderAttemptOutcome.Retrying)
                                .Select(attempt => attempt.AttemptedAtUtc)
                                .OrderDescending().Skip(1)
                                .DefaultIfEmpty(DateTimeOffset.MinValue).First();
                        }

                        PooledSearch search;
                        if (prepared.TryGetValue(provider.ProviderId, out var cached))
                        {
                            if (cached.TimedOut) searchCancellation.Cancel();
                            cached.Error?.Throw();
                            search = cached.Search!;
                        }
                        else search = await SearchPooledAsync(
                            provider, identity, format.MediaType, exclusions, searchCancellation, format.Id, retryPass, cycleStart,
                            cancellationToken);
                        var options = search.Options;
                        if (search.RateLimitedFor is null && search.Complete) searchedProviders++;
                        var userExclusions = ExternalCandidateExclusions.From(request.DeclinedCandidates
                            .Where(c => c.Reason == DeclinedCandidateReason.RequesterDeclined), provider.ProviderId, format.Id);
                        var questionOptions = options.Where(o => !userExclusions.Excludes(o)).ToArray();
                        if (search.RateLimitedFor is { } rateLimitedFor)
                        {
                            nextCheck = clock.UtcNow + rateLimitedFor;
                        }

                        var searchFoundSomething = options.Count > 0;
                        // Shadow only: hands unsettled candidates to a background reviewer and returns at once.
                        semanticShadow?.Observe(request.Id, options);
                        // By ID, and by release: a copy that failed is not
                        // tried again just because the same posting turns up
                        // under another reference.
                        options = options.Where(option => !exclusions.Excludes(option)).ToArray();

                        // Previously language-held copies remain excluded from automatic acquisition. Only a language question may re-admit them.
                        if (options.Count == 0 && questions is not null && AcquisitionQuestionPolicy.Select(identity, questionOptions,
                                format.MediaType, provider.EffectiveConfidence(format.MediaType)) is { Kind: AcquisitionQuestionKind.OtherLanguageOnly } foreign)
                        {
                            offers.Add(foreign with { SearchVariant = search.VariantOf(foreign.Candidate).ToStored() });
                            AddAttempt(request, format, provider, ProviderAttemptOutcome.CandidatesFound, "A language choice is available; still checking for a copy in the requested language.", waitingNext);
                            continue;
                        }

                        if (options.Count == 0 && questions is not null && await questions.FindReserveAsync(request, format, work.Title, cancellationToken) is not null)
                        {
                            AddAttempt(request, format, provider, ProviderAttemptOutcome.NoMatch, "No narrated copy was found; a dramatized copy is kept in reserve.", waitingNext);
                            continue;
                        }

                        // Everything this source offers has now been tried and
                        // failed. That is emphatically not "nothing found": it
                        // must reach a librarian rather than leave the request
                        // sitting in the automatic queue retrying a source with
                        // nothing left to give.
                        if (options.Count == 0 && searchFoundSomething && alreadyTried)
                        {
                            var exhaustedReason = DescribeExhaustedAttempts(request, format, provider);
                            AddAttempt(request, format, provider, ProviderAttemptOutcome.Failed,
                                exhaustedReason, waitingNext);
                            await MarkForReviewAsync(request, work.Title, exhaustedReason, cancellationToken);
                            break;
                        }

                        if (options.Count == 0)
                        {
                            AddAttempt(request, format, provider, ProviderAttemptOutcome.NoMatch,
                                "No matching candidate was reported by this provider.", nextCheck);
                            continue;
                        }

                        // An unrecognized audiobook container is excluded; an
                        // *absent* one is not. A source that reports only a
                        // release name frequently cannot state a container at
                        // all, and dropping those candidates made this service
                        // record "no match" for a search that had in fact
                        // returned usable results -- a factually wrong summary
                        // that also hid the results from review. The ranker
                        // puts an unknown container last, and the
                        // post-download audio structural validator remains the
                        // gate on whether the bytes are a usable audiobook.
                        // A stated container that the configured import policy refuses (FLAC, Opus...) is never
                        // usable, however well it ranks: fetching it only spends a download on a file staging rejects.
                        var reviewableOptions = format.MediaType == RequestMediaType.Audiobook
                            ? options.Where(option =>
                                string.IsNullOrWhiteSpace(option.Format) ||
                                (AudiobookFormatPolicy.IsUsableForAutomaticAcquisition(option.Format) &&
                                 IsImportableAudiobookFormat(option.Format))).ToArray()
                            : options;
                        // Pool-level identity must see every reviewable record before presentation deduplication.
                        var sameWorkPool = ReviewableForRequester(reviewableOptions, work.Language);
                        reviewableOptions = CollapseEquivalentReviewOptions(reviewableOptions);
                        if (reviewableOptions.Count == 0)
                        {
                            AddAttempt(request, format, provider, ProviderAttemptOutcome.NoMatch,
                                "The provider reported only audiobook formats excluded from automatic acquisition.", nextCheck);
                            continue;
                        }

                        reviewableOptions = ExternalCandidateRanker.Rank(reviewableOptions, format.MediaType).ToArray();

                        // Only a result that could be this book is worth a person's time. A result that
                        // contradicts the requested work, or exists only in another language, is recorded
                        // as no acceptable copy: the request keeps waiting and nobody is asked to choose.
                        // Automatic acquisition below still sees every option; this narrows review only.
                        var reviewCandidates = ReviewableForRequester(reviewableOptions, work.Language);

                        // A provider that lists one audiobook as separately
                        // numbered fragments ("1 of 2", "2 of 2") has no single
                        // record that is the whole book. When every part of one
                        // complete, compatible, strictly matched set is present,
                        // fetch them all; anything weaker keeps the ordinary
                        // review path below. The set is re-derived server-side
                        // for each part, and publishes only if every part passes.
                        if (format.MediaType == RequestMediaType.Audiobook &&
                            provider.EffectiveConfidence(format.MediaType) != ProviderConfidenceLevel.SuggestOnly &&
                            partSets is not null &&
                            AudiobookPartSetSelector.TrySelect(
                                [.. reviewableOptions.Where(option => search.VariantOf(option).IsBase)],
                                provider.EffectiveConfidence(format.MediaType)) is { } completeSet)
                        {
                            var started = await partSets.StartAsync(
                                request.Id, format.Id, provider.ProviderId, completeSet, cancellationToken);
                            if (started.Started)
                            {
                                AddAttempt(request, format, provider, ProviderAttemptOutcome.Submitted,
                                    $"Fetching a complete {completeSet.Total}-part audiobook set. Each part is scanned and " +
                                    "checked on its own, and the book is published only once every part passes.",
                                    nextEligibleCheckAtUtc: null, providerAcquisitionJobId: started.FirstJobId);
                            }
                            else
                            {
                                var setReason = $"A complete {completeSet.Total}-part audiobook set was found, but it " +
                                    $"could not be fetched. {started.Error}";
                                var setRecorded = await MarkForCandidateReviewAsync(
                                    request, format, provider, work.Title, work.PrimaryAuthor, reviewCandidates, search,
                                    setReason, cancellationToken);
                                AddAttempt(request, format, provider, ProviderAttemptOutcome.Failed,
                                    setRecorded
                                        ? setReason
                                        : $"{setReason} This request is already under review for a different reason.",
                                    waitingNext);
                            }

                            advanced = true;
                            break;
                        }

                        // The source's effective level gates confidence tiers. Within an allowed
                        // tier the shared ranker orders copies; a failed copy advances through
                        // the existing exclusions and attempt budget on the next recheck.
                        var automaticMatches = CandidateConfidence.Rank(
                            reviewableOptions.Where(option => !exclusions.Excludes(option) &&
                                CandidateConfidence.Allows(provider.EffectiveConfidence(format.MediaType), CandidateConfidence.Classify(option))),
                            format.MediaType).ToArray();

                        if (automaticMatches.Length == 0 &&
                            SameWorkCandidateSelector.TrySelect(sameWorkPool, identity, format.MediaType,
                                provider.EffectiveConfidence(format.MediaType)) is { } sameWork &&
                            CandidateConfidence.Allows(provider.EffectiveConfidence(format.MediaType), sameWork.Tier))
                            automaticMatches = [sameWork.Winner];

                        // A narration-aware winner replaces raw format-rank
                        // collapsing here too -- same selector, same rules,
                        // as the built-in Gutenberg path. Without exactly one
                        // eligible winner, the pool goes to review below.
                        AudiobookCandidateSelectionResult? audiobookSelection = null;
                        if (format.MediaType == RequestMediaType.Audiobook)
                        {
                            var preference = await GetNarrationPreferenceAsync(request.UserId, cancellationToken);
                            audiobookSelection = AudiobookCandidateSelector.Select(automaticMatches, preference);
                            automaticMatches = audiobookSelection.Winner is { } winner ? [winner] : [];
                        }
                        else if (automaticMatches.Length > 1) automaticMatches = [automaticMatches[0]];

                        // The attempt budget is spent: stop downloading and let
                        // a librarian see every candidate that already failed,
                        // rather than walking further down a list that may be
                        // costing real transfers against a metered source.
                        var failuresSoFar = request.CountAutomaticCandidateFailures(format.Id, provider.ProviderId);
                        if (automaticMatches.Length == 1 &&
                            failuresSoFar >= provider.AutomaticAttemptLimit)
                        {
                            advanced = true;
                            // One extra attempt for a candidate that is new, strong and within the cap (OQ-5, OQ-14): it
                            // downloads with the review still open. Anything else only waits.
                            if (request.Status == RequestStatus.NeedsReview &&
                                request.ReviewCategory != RequestReviewCategory.PreferenceAmbiguity &&
                                failuresSoFar - provider.AutomaticAttemptLimit < provider.AutomaticAttemptLimit &&
                                IsStrong(automaticMatches[0], ExternalCandidateExclusions.None))
                            {
                                await TryExtraAttemptAsync(
                                    request, format, provider, work.Title, automaticMatches[0], search, waitingNext,
                                    PeerSizeEvidence.From(automaticMatches[0], ReviewableForRequester(options, work.Language)), cancellationToken);
                                break;
                            }

                            var exhaustedReason = DescribeExhaustedAttempts(request, format, provider);
                            var exhaustedRecorded = await MarkForCandidateReviewAsync(
                                request, format, provider, work.Title, work.PrimaryAuthor, reviewCandidates, search,
                                exhaustedReason, cancellationToken);
                            AddAttempt(request, format, provider, ProviderAttemptOutcome.CandidatesFound,
                                exhaustedRecorded
                                    ? exhaustedReason
                                    : $"{exhaustedReason} This request is already under review for a different " +
                                      "reason; it will be reconsidered once that is resolved.",
                                waitingNext);
                            break;
                        }

                        if (automaticMatches.Length == 1)
                        {
                            advanced = true;
                            // A copy that needs no answer makes a pending question moot: withdraw it, tell the requesters, and
                            // carry on into the download. The status changes before any attempt row is written, so the next
                            // pass is not due at once.
                            if (request is { Status: RequestStatus.NeedsReview, ReviewCategory: RequestReviewCategory.PreferenceAmbiguity } &&
                                (request.QuestionKind is null || request.QuestionFormatId == format.Id))
                            {
                                request.WithdrawPreferenceReviewForAutomaticCandidate("Found a copy that needs no answer.", clock.UtcNow);
                                foreach (var requesterId in request.ActiveRequesterIds)
                                {
                                    await notifications.RecordReviewWithdrawnAsync(
                                        requesterId, request.Id, work.Title,
                                        "Found a standalone copy; no answer is needed.", cancellationToken);
                                }
                            }

                            search.MarkSelected(automaticMatches[0], clock.UtcNow);
                            var (acquireResult, identityFailure) = await security.AcquireEvaluateAndVerifyAsync(
                                request.Id,
                                format.Id,
                                provider.ProviderId,
                                automaticMatches[0].ProviderResultId,
                                cancellationToken,
                                allowDownloadTimeDrmValidation: IsUnknownDrmOnlyConcern(automaticMatches[0]),
                                isAutomaticAcquisition: true,
                                // Re-derive the option by the very search that found it.
                                searchVariant: search.VariantOf(automaticMatches[0]),
                                peerSizeEvidence: PeerSizeEvidence.From(automaticMatches[0], ReviewableForRequester(options, work.Language)));

                            // A clean download of the wrong book is the exact
                            // case the retry loop exists for, so it is routed
                            // through the same advance path as a failed one
                            // rather than being reported as an acquisition.
                            if (acquireResult.Outcome == ManualImportOutcome.Success && identityFailure is not null)
                            {
                                await RecordAutomaticFailureAsync(
                                    request, format, provider, work.Title, automaticMatches[0], identityFailure,
                                    waitingNext, cancellationToken);
                                break;
                            }

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
                                    AutomaticAttemptNarrative.Starting(
                                        AutomaticAttemptNarrative.DescribeCandidate(
                                            automaticMatches[0].ReleaseName, automaticMatches[0].Title,
                                            automaticMatches[0].Format, automaticMatches[0].SizeBytes),
                                        failuresSoFar + 1, provider.AutomaticAttemptLimit),
                                    nextEligibleCheckAtUtc: null,
                                    providerAcquisitionJobId: acquireResult.ProviderAcquisitionJobId);
                                break;
                            }

                            // This candidate is spent. Rule it out and let the
                            // next pass advance to the next ranked record --
                            // one bad copy is not a reason to stop looking, it
                            // is a reason to try the next one. Only once the
                            // provider's attempt budget is gone does this
                            // become a librarian's problem.
                            await RecordAutomaticFailureAsync(
                                request, format, provider, work.Title, automaticMatches[0],
                                acquireResult.Error ?? "The automatic copy could not be acquired.",
                                waitingNext, cancellationToken);
                            break;
                        }

                        if (questions is not null && AcquisitionQuestionPolicy.Select(identity, reviewableOptions,
                                format.MediaType, provider.EffectiveConfidence(format.MediaType)) is { } offer)
                        {
                            offers.Add(offer with { SearchVariant = search.VariantOf(offer.Candidate).ToStored() });
                            AddAttempt(request, format, provider, ProviderAttemptOutcome.CandidatesFound, "A product choice is available; still checking other sources.", waitingNext);
                            continue;
                        }

                        if (reviewCandidates.Length == 0)
                        {
                            AddAttempt(request, format, provider, ProviderAttemptOutcome.NoMatch,
                                DescribeNoAcceptableCopy(options, work.Language), waitingNext);
                            continue;
                        }

                        search.MarkSelected(reviewCandidates[0], clock.UtcNow);
                        advanced = true;
                        var recorded = await MarkForCandidateReviewAsync(
                            request, format, provider, work.Title, work.PrimaryAuthor, reviewCandidates, search,
                            provider.EffectiveConfidence(format.MediaType) == ProviderConfidenceLevel.SuggestOnly
                                ? "This source is set to suggest only. A librarian must verify and choose a copy before downloading."
                                : DescribeCandidateReviewReason(reviewCandidates), cancellationToken);
                        AddAttempt(request, format, provider, ProviderAttemptOutcome.CandidatesFound,
                            recorded
                                ? $"Found {reviewCandidates.Length} candidate(s); choose a reviewed candidate before acquisition."
                                : $"Found {reviewCandidates.Length} candidate(s), but this request is already under " +
                                  "review for a different reason; it will be reconsidered once that is resolved.",
                            waitingNext);
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
                    catch (HttpRequestException exception) when (RateLimitWait(exception) is { } wait)
                    {
                        AddAttempt(request, format, provider, ProviderAttemptOutcome.Failed,
                            $"The provider asked Family Librarian to slow down; retrying after {clock.UtcNow + wait:yyyy-MM-dd HH:mm} UTC.",
                            clock.UtcNow + wait);
                    }
                    catch (HttpRequestException exception) when (IsTransientProviderOutage(exception))
                    {
                        // The provider was unreachable or momentarily
                        // overloaded (for example while it restarts during a
                        // redeploy). Retry soon instead of waiting out a
                        // daily/weekly schedule for a problem that clears in
                        // minutes.
                        AddAttempt(request, format, provider, ProviderAttemptOutcome.Failed,
                            $"The provider was unavailable ({DescribeHttpFailure(exception)}); retrying in " +
                            $"{TransientRetryDelay.TotalMinutes:0} minutes.",
                            clock.UtcNow + TransientRetryDelay);
                    }
                    catch (HttpRequestException exception)
                    {
                        AddAttempt(request, format, provider, ProviderAttemptOutcome.Failed,
                            $"The provider lookup failed ({DescribeHttpFailure(exception)}) and will be retried on its configured schedule.",
                            nextCheck);
                    }
                    catch (CryptographicException)
                    {
                        AddAttempt(request, format, provider, ProviderAttemptOutcome.Failed,
                            "The provider lookup failed (the saved API key could not be decrypted; re-enter it) and will be retried on its configured schedule.",
                            nextCheck);
                    }
                    finally
                    {
                        searchCancellation?.Dispose();
                    }
                }

                if (!advanced && searchedProviders == scheduledProviders.Length && questions is not null && work is not null)
                {
                    var rankedOffers = CandidateConfidence.Rank(offers.Select(o => o.Candidate), format.MediaType);
                    var best = rankedOffers.Count == 0 ? null : rankedOffers[0];
                    var offer = best is null ? await questions.FindReserveAsync(request, format, work.Title, cancellationToken)
                        : offers.First(o => ReferenceEquals(o.Candidate, best));
                    if (offer is not null && await questions.AskAsync(request, format, work.Title, offer, cancellationToken))
                    {
                        if (savedQuestions is not null && await savedQuestions.TryAcceptAsync(request.Id, cancellationToken)) break;
                        await questions.NotifyAsync(request, work.Title, cancellationToken);
                    }
                }
            }

            await attempts.SaveChangesAsync(cancellationToken);
            await requests.SaveChangesAsync(cancellationToken);
        }

        return checks;
    }

    private static readonly TimeSpan RateLimitFloor = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan PassTimeCap = TimeSpan.FromMinutes(6);

    private sealed record PooledSearch(
        IReadOnlyList<FulfillmentOption> Options,
        IReadOnlyDictionary<string, SearchVariantRef> Found,
        TimeSpan? RateLimitedFor,
        IReadOnlyList<SearchVariantAttempt> Memory,
        bool Complete = true)
    {
        /// <summary>Remembers that this candidate (downloaded, or first in review) came from the variant that found it.</summary>
        public void MarkSelected(FulfillmentOption option, DateTimeOffset at)
        {
            var variant = VariantOf(option);
            Memory.FirstOrDefault(row => row.VariantKind == variant.Kind.ToString() && row.VariantKey == variant.Key)
                ?.MarkFoundSelected(at);
        }

        public SearchVariantRef VariantOf(FulfillmentOption option) =>
            Found.GetValueOrDefault(option.ProviderResultId) ?? new SearchVariantRef(SearchVariantKind.Base, string.Empty);
    }

    /// <summary>
    /// Runs the planned variants in order (each at most once per pass), pooling and de-duplicating what they return, and stops
    /// early on a strong candidate. Then applies the single-strict-winner step once to the pool. A rate-limit answer (or, once
    /// something has been found, any later failure) ends the list; the pool so far is still judged.
    /// </summary>
    private async Task<PooledSearch> SearchPooledAsync(
        ExternalProvider provider, BookIdentity identity, RequestMediaType mediaType, ExternalCandidateExclusions exclusions,
        CancellationTokenSource searchCancellation, Guid requestFormatId, bool retryPass, DateTimeOffset cycleStartUtc,
        CancellationToken cancellationToken)
    {
        var memory = (await attempts.ListVariantAttemptsAsync(requestFormatId, provider.ProviderId, cancellationToken)).ToList();
        var plan = SearchVariantPlanner.Plan(identity, mediaType).ToList();
        if (retryPass)
        {
            foreach (var row in memory.Where(row => row.VariantKind == nameof(SearchVariantKind.AiSuggestion) &&
                         row.LastResultCount > 0 && row.LastTriedAtUtc >= cycleStartUtc))
            {
                if (SearchGuidanceVariants.Restore(identity, new(SearchVariantKind.AiSuggestion, row.VariantKey)) is { } variant)
                    plan.Add(variant);
            }
        }
        var pool = new ExternalCandidatePool(mediaType);
        var clockStart = clock.UtcNow;
        TimeSpan? rateLimited = null;
        var complete = true;
        for (var index = 0; index < plan.Count; index++)
        {
            // A time guard for one provider pass, not a search budget: variants not reached run on the next recheck.
            if (index > 0 && clock.UtcNow - clockStart > PassTimeCap) { complete = false; break; }

            // A retry pass rebuilds the pool only to get fresh acquire tokens: the base search and the variants that
            // returned something this cycle. A scheduled recheck runs the whole list again.
            var kind = plan[index].Kind.ToString();
            var remembered = memory.FirstOrDefault(row => row.VariantKind == kind && row.VariantKey == plan[index].Key);
            if (retryPass && index > 0 && !(remembered is { LastResultCount: > 0 } && remembered.LastTriedAtUtc >= cycleStartUtc))
            {
                complete = false;
                continue;
            }

            searchCancellation.CancelAfter(BackgroundSearchTimeout);
            IReadOnlyList<FulfillmentOption> raw;
            try
            {
                raw = await candidateChecker.SearchVariantAsync(provider, identity, mediaType, plan[index], searchCancellation.Token);
            }
            catch (Exception exception) when (pool.Options.Count > 0 && !cancellationToken.IsCancellationRequested &&
                                              (exception is HttpRequestException || exception is OperationCanceledException))
            {
                rateLimited = exception is HttpRequestException http ? RateLimitWait(http) : null;
                complete = false;
                break;
            }

            if (remembered is null)
            {
                remembered = new SearchVariantAttempt(requestFormatId, provider.ProviderId, kind, plan[index].Key, clock.UtcNow, raw.Count);
                memory.Add(remembered);
                attempts.AddVariantAttempt(remembered);
            }
            else
            {
                remembered.RecordTry(clock.UtcNow, raw.Count);
            }

            pool.Merge(raw, plan[index].Ref);
            if (pool.Options.Any(option => IsStrong(option, exclusions))) break;
        }

        return new PooledSearch(
            ExternalCandidateAvailabilityChecker.SelectOneStrictCandidate(pool.Options, mediaType, exclusions), pool.Found, rateLimited, memory, complete);
    }

    /// <summary>An identifier or strict title-and-author copy with nothing left for a person to confirm.</summary>
    private static bool IsStrong(FulfillmentOption option, ExternalCandidateExclusions exclusions) =>
        CandidateConfidence.Classify(option) == CandidateConfidenceTier.Strong &&
        !exclusions.Excludes(option);

    /// <summary>How long to leave a provider alone after it asked to be slowed down; null when the failure is not a rate limit.</summary>
    private static TimeSpan? RateLimitWait(HttpRequestException exception) => exception switch
    {
        ExternalProviderRateLimitedException limited => limited.RetryAfter is { } after && after > RateLimitFloor ? after : RateLimitFloor,
        { StatusCode: System.Net.HttpStatusCode.TooManyRequests } => RateLimitFloor,
        _ => null
    };

    /// <summary>No HTTP status means the request never completed (refused, reset, DNS); 502-504 mean the provider or its gateway was briefly down.</summary>
    private static bool IsTransientProviderOutage(HttpRequestException exception) =>
        exception.StatusCode is null or System.Net.HttpStatusCode.BadGateway
            or System.Net.HttpStatusCode.ServiceUnavailable or System.Net.HttpStatusCode.GatewayTimeout;

    /// <summary>
    /// A short, secret-free reason for the admin banner. Deliberately avoids
    /// the exception message, which can embed the provider URL.
    /// </summary>
    private static string DescribeHttpFailure(HttpRequestException exception) =>
        exception.StatusCode is { } status
            ? $"HTTP {(int)status} {status}"
            : exception.InnerException is System.Net.Sockets.SocketException socket
                ? $"could not connect: {socket.SocketErrorCode}"
                : "network error before a response was received";

    private static bool IsDue(ProviderAttempt? latest, BookRequest request, DateTimeOffset now) =>
        // A cancellation followed by "Ask again" begins a new request cycle.
        // Preserve previous attempts for the audit trail, but do not make their
        // next-check timestamp block the new request.
        latest is null || request.QuestionKind is null && latest.AttemptedAtUtc < request.StatusChangedAtUtc ||
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
                option.AudiobookPart?.Number,
                option.AudiobookPart?.Total,
                option.ProviderPopularity,
                option.IsAbridged,
                option.IsUnabridged,
                option.NarrationKind,
                NormalizeReviewFact(option.Narrator),
                option.RequiresLanguageConfirmation,
                option.RequiresReleaseConfirmation,
                NormalizeReviewFact(option.DrmStatus),
                NormalizeReviewFact(option.ReleaseConcern),
                // Provenance is a real distinction, not a duplicate: a retail
                // release and an untagged one are different things to both a
                // librarian comparing them and the ranker choosing between
                // them.
                NormalizeReviewFact(option.Quality)))
            // The group's best member, not its first. Response order used to
            // decide this, which could discard the one candidate carrying the
            // confirmed MatchBasis and leave the format with nothing eligible
            // for automatic acquisition at all -- turning a confirmed match
            // into a review purely through collapsing.
            .Select(group => ExternalCandidateRanker.Rank(group, group.Key.MediaType)[0])
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
        int? PartNumber,
        int? PartTotal,
        int? ProviderPopularity,
        bool? IsAbridged,
        bool? IsUnabridged,
        NarrationKind? NarrationKind,
        string? Narrator,
        bool RequiresLanguageConfirmation,
        bool RequiresReleaseConfirmation,
        string? DrmStatus,
        string? ReleaseConcern,
        string? Quality);

    private async Task<AudiobookNarrationPreference> GetNarrationPreferenceAsync(
        Guid requesterUserId, CancellationToken cancellationToken)
    {
        var account = await accounts.FindAsync(requesterUserId, cancellationToken);
        return account?.AudiobookNarrationPreference ?? AudiobookNarrationPreference.PreferHuman;
    }

    private bool IsImportableAudiobookFormat(string format) =>
        importPolicy is null ||
        importPolicy.IsExtensionAllowed(RequestMediaType.Audiobook, "." + format.Trim().TrimStart('.'));

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
        DateTimeOffset? nextEligibleCheckAtUtc,
        Guid? providerAcquisitionJobId = null) =>
        attempts.Add(new ProviderAttempt(
            request.Id, format.Id, provider.ProviderId, outcome, summary, clock.UtcNow, nextEligibleCheckAtUtc,
            providerAcquisitionJobId));

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
    /// <returns>
    /// Whether the candidates were actually recorded. False means the caller
    /// must not log an attempt summary that promises a reviewable candidate --
    /// e.g. a different format's lookup already put this request under review
    /// for an unrelated reason in the same pass, and there is nothing this
    /// provider can add to that.
    /// </returns>
    /// <summary>
    /// What a person may be asked to choose between: not blocked by the identity assessment (a rule-level
    /// contradiction of title, language or edition) and in the accepted language. Author spelling alone never
    /// blocks. The checker's own result is untouched because browsing callers and the admin debug search show it all.
    /// </summary>
    private static FulfillmentOption[] ReviewableForRequester(
        IReadOnlyList<FulfillmentOption> options, string? acceptedLanguage) =>
        options.Where(option =>
            option.AcquisitionAssessment?.Suitability != AcquisitionSuitability.Blocked &&
            LanguageAcceptance.IsAcceptedOrUnspecified(option.Language, acceptedLanguage)).ToArray();

    private static string DescribeNoAcceptableCopy(IReadOnlyList<FulfillmentOption> options, string? acceptedLanguage)
    {
        var otherLanguages = options
            .Where(option => !LanguageAcceptance.IsAcceptedOrUnspecified(option.Language, acceptedLanguage))
            .Select(option => option.Language!).ToArray();
        return otherLanguages.Length == options.Count
            ? "Found only editions in other languages (" +
              string.Join(", ", otherLanguages.Select(DescribeLanguage).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)) + ")."
            : $"Found {options.Count} result(s); none of them is this book.";
    }

    private static string DescribeLanguage(string language)
    {
        var code = language.Trim();
        var separator = code.IndexOfAny(['-', '_']);
        try
        {
            return CultureInfo.GetCultureInfo(separator > 0 ? code[..separator] : code).EnglishName;
        }
        catch (CultureNotFoundException)
        {
            return code;
        }
    }

    private async Task<bool> MarkForCandidateReviewAsync(
        BookRequest request,
        RequestFormat format,
        ExternalProvider provider,
        string workTitle,
        string? workAuthor,
        FulfillmentOption[] options,
        PooledSearch search,
        string reason,
        CancellationToken cancellationToken)
    {
        if (options.Length == 0)
        {
            return false;
        }

        if (request.QuestionKind is not null) return false;

        // The stored review may collapse records that are identical to the
        // requester, so do not report the raw provider result count as though
        // it were the number of choices a person will see.
        // Every candidate keeps the title and author its own source claimed.
        // Storing workTitle/workAuthor here instead was the defect that made a
        // review of twenty-three unrelated records -- "The Threshing Floor",
        // "The Threshing Circle", a paper on galaxy threshing -- render as
        // twenty-three identical rows reading "Threshing Day by Rebecca
        // Yarros", with no way for a librarian to tell them apart or to see
        // that none of them was the requested book.
        var candidates = options
            .Select(option => RequestReviewCandidateRecord.WithCatalogFallback(
                RequestReviewCandidateRecord.From(format.Id, option, search.VariantOf(option).ToStored()), workTitle, workAuthor))
            .ToArray();

        if (request.Status == RequestStatus.PendingAcquisition)
        {
            request.MarkNeedsReview(RequestReviewCategory.PreferenceAmbiguity, reason, clock.UtcNow, candidates);
        }
        else if (request.Status == RequestStatus.NeedsReview &&
                 request.ReviewCategory == RequestReviewCategory.PreferenceAmbiguity &&
                 request.ReviewCandidates.All(candidate => candidate.RequestFormatId != format.Id))
        {
            // A different format's lookup already put this request under
            // review in this same background pass (two external providers,
            // or this same provider against the request's other format, can
            // both complete within one ProcessDueAsync call). Add this
            // format's own candidates instead of silently dropping them -- or
            // wiping the other format's with RefreshPreferenceReview, which
            // replaces the whole list rather than appending to it.
            request.AddReviewCandidatesForFormat(format.Id, reason, clock.UtcNow, candidates);
        }
        else
        {
            // Under review for an unrelated reason, or this format's
            // candidates are already stored. Nothing to record.
            return false;
        }

        await notifications.RecordRequestNeedsReviewAsync(request.Id, workTitle, reason, cancellationToken);
        foreach (var requesterId in request.ActiveRequesterIds)
        {
            await notifications.RecordPreferenceAmbiguityAsync(
                requesterId, request.Id, workTitle, reason, cancellationToken);
        }

        return true;
    }

    /// <summary>
    /// Rules out one automatically fetched copy that failed after download and
    /// either lines up the next candidate or, once the provider's attempt
    /// budget is spent, hands the request to a librarian.
    /// </summary>
    /// <remarks>
    /// One place for the bookkeeping every failure shares, so the identity
    /// mismatch and the plain fetch failure cannot drift apart. A step that
    /// advances is recorded as <see cref="ProviderAttemptOutcome.Retrying"/>,
    /// not <see cref="ProviderAttemptOutcome.Failed"/>: nothing needs doing yet,
    /// and the ledger should not read as though something does.
    /// </remarks>
    private async Task RecordAutomaticFailureAsync(
        BookRequest request, RequestFormat format, ExternalProvider provider, string workTitle,
        FulfillmentOption option, string reason, DateTimeOffset exhaustedNextCheck, CancellationToken cancellationToken)
    {
        request.RecordAutomaticCandidateFailure(
            format.Id, provider.ProviderId, option.ProviderResultId, reason, clock.UtcNow,
            ExternalReleaseFingerprint.Compute(option.ReleaseName, option.SizeBytes));
        var failures = request.CountAutomaticCandidateFailures(format.Id, provider.ProviderId);

        if (failures >= provider.AutomaticAttemptLimit)
        {
            AddAttempt(request, format, provider, ProviderAttemptOutcome.Failed,
                AutomaticAttemptNarrative.Exhausted(reason, failures, provider.AutomaticAttemptLimit),
                exhaustedNextCheck);
            await MarkForReviewAsync(request, workTitle, reason, cancellationToken);
            return;
        }

        // Retry promptly: the next candidate is a different record, not a
        // repeat of the lookup that just failed, so the daily/weekly schedule
        // is the wrong wait here -- and so is the next worker sweep.
        AddAttempt(request, format, provider, ProviderAttemptOutcome.Retrying,
            AutomaticAttemptNarrative.Advancing(reason, failures, provider.AutomaticAttemptLimit),
            nextEligibleCheckAtUtc: clock.UtcNow);
        fulfillmentSignal?.Request();
    }

    private static TimeSpan MaxInterval(TimeSpan first, TimeSpan second) => first >= second ? first : second;

    /// <summary>
    /// The one extra attempt a spent budget allows (AUTOFIND 2c, owner answers OQ-5 and OQ-14): a new, strong candidate is
    /// downloaded while the review stays open. Success withdraws the review; a failure rules the candidate out, adds a note to
    /// the open review and sends no second notification. Never advances to the next candidate by itself.
    /// </summary>
    private async Task TryExtraAttemptAsync(
        BookRequest request, RequestFormat format, ExternalProvider provider, string workTitle, FulfillmentOption option,
        PooledSearch search, DateTimeOffset nextCheck, PeerSizeEvidence? peers, CancellationToken cancellationToken)
    {
        search.MarkSelected(option, clock.UtcNow);
        var (result, identityFailure) = await security.AcquireEvaluateAndVerifyAsync(
            request.Id, format.Id, provider.ProviderId, option.ProviderResultId, cancellationToken,
            allowDownloadTimeDrmValidation: IsUnknownDrmOnlyConcern(option), isAutomaticAcquisition: true,
            searchVariant: search.VariantOf(option), peerSizeEvidence: peers);

        if (result.Outcome == ManualImportOutcome.Success && identityFailure is null)
        {
            AddAttempt(request, format, provider, ProviderAttemptOutcome.Acquired,
                "Found a stronger new copy after the attempt limit; it passed its checks.", nextEligibleCheckAtUtc: null);
            request.WithdrawReviewForSuccessfulAttempt("A newer copy passed its checks.", clock.UtcNow);
            return;
        }

        if (result.Outcome == ManualImportOutcome.AcquisitionInProgress)
        {
            AddAttempt(request, format, provider, ProviderAttemptOutcome.Submitted,
                "Found a stronger new copy after the attempt limit; trying it once.", nextEligibleCheckAtUtc: null,
                providerAcquisitionJobId: result.ProviderAcquisitionJobId);
            return;
        }

        var reason = identityFailure ?? result.Error ?? "The automatic copy could not be acquired.";
        request.RecordAutomaticCandidateFailure(
            format.Id, provider.ProviderId, option.ProviderResultId, reason, clock.UtcNow,
            ExternalReleaseFingerprint.Compute(option.ReleaseName, option.SizeBytes));
        request.AddReviewNote($"A newer copy was tried once after the attempt limit and also failed: {reason}", clock.UtcNow);
        AddAttempt(request, format, provider, ProviderAttemptOutcome.Failed,
            $"A stronger new copy was tried once after the attempt limit and failed: {reason}", nextCheck);
        _ = workTitle;
    }

    /// <summary>
    /// Names what was already downloaded and rejected, so the review says why
    /// it stopped rather than only that something failed.
    /// </summary>
    private static string DescribeExhaustedAttempts(
        BookRequest request, RequestFormat format, ExternalProvider provider)
    {
        var failures = request.DeclinedCandidates
            .Where(declined => declined.RequestFormatId == format.Id &&
                declined.Reason == DeclinedCandidateReason.AutomaticVerificationFailed &&
                string.Equals(declined.ProviderId, provider.ProviderId, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var distinctReasons = failures
            .Select(failure => failure.FailureReason)
            .Where(reason => !string.IsNullOrWhiteSpace(reason))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var summary = failures.Length == 1
            ? "One automatically acquired copy failed its post-download checks"
            : $"{failures.Length} automatically acquired copies failed their post-download checks";
        return distinctReasons.Length > 0
            ? $"{summary} ({string.Join("; ", distinctReasons)}). A librarian must choose a source."
            : $"{summary}. A librarian must choose a source.";
    }

    private static string DescribeCandidateReviewReason(IReadOnlyList<FulfillmentOption> options)
    {
        var hasConfirmedWorkIdentity = options.Any(option =>
            option.MatchBasis is BookMatchBasis.Identifier or BookMatchBasis.StrictTitleAuthor or BookMatchBasis.StrictTitle);

        if (!hasConfirmedWorkIdentity)
        {
            if (options.Any(option => option.HasPlausibleTitle))
                return "The release names include the requested title, but other identity or release details need a librarian review before acquisition.";
            return "Possible copies were found, but their titles could not be confirmed as the requested work. A librarian must verify the source before acquisition.";
        }

        var releaseConcerns = options.Where(option => option.RequiresReleaseConfirmation)
            .Select(option => option.ReleaseConcern).Where(reason => !string.IsNullOrWhiteSpace(reason)).Distinct().ToArray();
        if (releaseConcerns.Length > 0)
            return $"Matching titles were found. {string.Join(" ", releaseConcerns)}";

        return "Several matching copies need a librarian comparison before acquisition.";
    }
}
