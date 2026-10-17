using System.Runtime.ExceptionServices;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Providers;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Application.Acquisition;

public sealed partial class ExternalProviderRecheckService
{
    private sealed record PreparedSearch(PooledSearch? Search, ExceptionDispatchInfo? Error = null, bool TimedOut = false,
        DateTimeOffset StartedAtUtc = default);

    /// <summary>
    /// With guidance enabled, collect deterministic searches before considering a model. The ordinary processing loop
    /// consumes these exact searches/errors, so a refused or invalid proposal retains the deterministic outcome.
    /// </summary>
    private async Task<Dictionary<string, PreparedSearch>> PrepareSearchGuidanceAsync(BookRequest request, RequestFormat format,
        WorkSummary? work, ExternalProvider[] scheduled, CancellationToken cancellationToken)
    {
        var prepared = new Dictionary<string, PreparedSearch>(StringComparer.OrdinalIgnoreCase);
        if (searchGuidance is null || work is null || !await searchGuidance.IsEnabledAsync(cancellationToken)) return prepared;
        var identity = new BookIdentity(work.Title, work.PrimaryAuthor, work.Isbn13s, work.Authors, work.Series,
            work.Language, work.PublicationYear, work.Publisher, work.AlternateTitles);
        var cycleStart = clock.UtcNow;
        foreach (var provider in scheduled)
        {
            var latest = await attempts.FindLatestForFormatAsync(format.Id, provider.ProviderId, cancellationToken);
            if (!IsDue(latest, request, clock.UtcNow) || ExternalCandidateAvailabilityChecker.IsKnownSearchUnavailable(provider) ||
                ExternalCandidateAvailabilityChecker.IsKnownAcquireUnavailable(provider)) continue;
            if (request.Status == RequestStatus.NeedsReview && request.ReviewCategory != RequestReviewCategory.PreferenceAmbiguity &&
                request.CountAutomaticCandidateFailures(format.Id, provider.ProviderId) < provider.AutomaticAttemptLimit &&
                (questions is null || await questions.FindReserveAsync(request, format, work.Title, cancellationToken) is null)) continue;

            var retry = latest?.Outcome == ProviderAttemptOutcome.Retrying;
            var providerCycle = DateTimeOffset.MinValue;
            if (retry)
            {
                providerCycle = (await attempts.ListForRequestAsync(request.Id, cancellationToken))
                    .Where(a => a.RequestFormatId == format.Id && string.Equals(a.ProviderId, provider.ProviderId, StringComparison.OrdinalIgnoreCase) &&
                        a.Outcome != ProviderAttemptOutcome.Retrying)
                    .Select(a => a.AttemptedAtUtc).OrderDescending().Skip(1).DefaultIfEmpty(DateTimeOffset.MinValue).First();
                if (providerCycle < cycleStart) cycleStart = providerCycle;
            }
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var startedAt = clock.UtcNow;
            try
            {
                var exclusions = ExternalCandidateExclusions.From(request.DeclinedCandidates, provider.ProviderId, format.Id);
                prepared[provider.ProviderId] = new(await SearchPooledAsync(provider, identity, format.MediaType, exclusions,
                    limit, format.Id, retry, providerCycle, cancellationToken), StartedAtUtc: startedAt);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested &&
                exception is HttpRequestException or OperationCanceledException or System.Security.Cryptography.CryptographicException)
            {
                prepared[provider.ProviderId] = new(null, ExceptionDispatchInfo.Capture(exception), limit.IsCancellationRequested);
            }
        }

        if (prepared.Count != scheduled.Length || prepared.Values.Any(p => p.Search is not { Complete: true, RateLimitedFor: null })) return prepared;
        // An existing search or finding suppresses another call in the same retry cycle, even after a give_up or failure.
        if (prepared.Values.Any(p => p.Search!.Memory.Any(a => a.VariantKind == nameof(SearchVariantKind.AiSuggestion) && a.LastTriedAtUtc >= cycleStart)))
            return prepared;
        foreach (var provider in scheduled)
        {
            var options = prepared[provider.ProviderId].Search!.Options;
            var exclusions = ExternalCandidateExclusions.From(request.DeclinedCandidates, provider.ProviderId, format.Id);
            var available = options.Where(o => !exclusions.Excludes(o)).ToArray();
            var userExclusions = ExternalCandidateExclusions.From(request.DeclinedCandidates
                .Where(c => c.Reason == DeclinedCandidateReason.RequesterDeclined), provider.ProviderId, format.Id);
            var questionOptions = options.Where(o => !userExclusions.Excludes(o)).ToArray();
            if (HasAllowedAction(identity, available, format.MediaType, provider.EffectiveConfidence(format.MediaType)) ||
                questions is not null && (AcquisitionQuestionPolicy.Select(identity, questionOptions, format.MediaType,
                    provider.EffectiveConfidence(format.MediaType)) is not null ||
                    await questions.FindReserveAsync(request, format, work.Title, cancellationToken) is not null)) return prepared;
        }

        var keys = prepared.Values.SelectMany(p => p.Search!.Memory).Select(a => a.VariantKey)
            .Distinct(StringComparer.Ordinal).ToArray();
        var guidance = await searchGuidance.GetAsync(request.Id, format.Id, identity,
            prepared.Values.SelectMany(p => p.Search!.Options).ToArray(), keys, cycleStart, cancellationToken);
        if (guidance is null || guidance.GiveUp) return prepared;
        var variants = SearchGuidanceVariants.Build(identity, guidance);
        if (variants is null) return prepared;
        foreach (var provider in scheduled)
        {
            var search = prepared[provider.ProviderId].Search!;
            var memory = search.Memory.ToList();
            var pool = new ExternalCandidatePool(format.MediaType);
            foreach (var option in search.Options) pool.Merge([option], search.VariantOf(option));
            var start = prepared[provider.ProviderId].StartedAtUtc;
            var complete = true;
            TimeSpan? rateLimited = null;
            foreach (var variant in variants)
            {
                if (clock.UtcNow - start > PassTimeCap) { complete = false; break; }
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                limit.CancelAfter(BackgroundSearchTimeout);
                IReadOnlyList<FulfillmentOption> added;
                try { added = await candidateChecker.SearchVariantAsync(provider, identity, format.MediaType, variant, limit.Token); }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is HttpRequestException or OperationCanceledException)
                {
                    rateLimited = exception is HttpRequestException http ? RateLimitWait(http) : null;
                    complete = false;
                    break;
                }
                var remembered = memory.FirstOrDefault(a => a.VariantKind == nameof(SearchVariantKind.AiSuggestion) && a.VariantKey == variant.Key);
                if (remembered is null)
                {
                    remembered = new SearchVariantAttempt(format.Id, provider.ProviderId, nameof(SearchVariantKind.AiSuggestion), variant.Key, clock.UtcNow, added.Count);
                    memory.Add(remembered);
                    attempts.AddVariantAttempt(remembered);
                }
                else remembered.RecordTry(clock.UtcNow, added.Count);
                // contained_in stays marked in the variant reference; Phase 5 still proves containment independently.
                pool.Merge(added, variant.Ref);
            }
            var exclusions = ExternalCandidateExclusions.From(request.DeclinedCandidates, provider.ProviderId, format.Id);
            prepared[provider.ProviderId] = new(new PooledSearch(
                ExternalCandidateAvailabilityChecker.SelectOneStrictCandidate(pool.Options, format.MediaType, exclusions), pool.Found,
                rateLimited, memory, complete), StartedAtUtc: start);
        }
        return prepared;
    }

    private bool HasAllowedAction(BookIdentity identity, IReadOnlyList<FulfillmentOption> options,
        RequestMediaType mediaType, ProviderConfidenceLevel level)
    {
        var usable = mediaType == RequestMediaType.Audiobook ? options.Where(o => string.IsNullOrWhiteSpace(o.Format) ||
            AudiobookFormatPolicy.IsUsableForAutomaticAcquisition(o.Format) && IsImportableAudiobookFormat(o.Format)).ToArray() : options;
        return usable.Any(o => CandidateConfidence.Allows(level, CandidateConfidence.Classify(o))) ||
            SameWorkCandidateSelector.TrySelect(ReviewableForRequester(usable, identity.Language), identity, mediaType, level) is { } same &&
                CandidateConfidence.Allows(level, same.Tier) ||
            mediaType == RequestMediaType.Audiobook && level != ProviderConfidenceLevel.SuggestOnly && partSets is not null &&
                AudiobookPartSetSelector.TrySelect(usable, level) is not null;
    }
}
