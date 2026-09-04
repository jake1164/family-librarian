using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Notifications;
using FamilyLibrarian.Application.Requests;
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
public sealed class AutomaticRequestFulfillmentService(
    IRequestRepository requests,
    IProviderAttemptRepository attempts,
    IEnumerable<IAutomaticDirectAcquisitionProvider> providers,
    DirectAcquisitionSecurityService acquisition,
    IClock clock,
    NotificationService notifications)
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

        var pending = await requests.ListPendingForAutomaticFulfillmentAsync(BatchSize, cancellationToken);
        var processed = 0;

        foreach (var request in pending)
        {
            foreach (var format in request.Formats.Where(format => format.Status == RequestFormatStatus.Requested))
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
                    if (HasRecentAttempt(latestAttempt, request))
                    {
                        continue;
                    }

                    try
                    {
                        var providerOptions = await provider.FindDirectAcquisitionsAsync(
                            request.WorkId,
                            format.MediaType,
                            cancellationToken);
                        options.AddRange(providerOptions);
                        attempts.Add(new ProviderAttempt(
                            request.Id, format.Id, provider.Id,
                            providerOptions.Count == 0 ? ProviderAttemptOutcome.NoMatch : ProviderAttemptOutcome.CandidatesFound,
                            providerOptions.Count == 0
                                ? "No high-confidence automatic copy was found."
                                : $"Found {providerOptions.Count} high-confidence automatic candidate(s).",
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

                var distinctOptions = options
                    .GroupBy(option => (option.ProviderId, option.ProviderResultId), StringTupleComparer.OrdinalIgnoreCase)
                    .Select(group => group.Single())
                    .ToArray();

                if (distinctOptions.Length > 1)
                {
                    // Different providers confidently disagree on the file. Picking
                    // one automatically risks shipping the wrong edition, so this is
                    // the one "found something" case that still needs a librarian.
                    await MarkForReviewAsync(
                        request, "More than one high-confidence automatic copy was found.", cancellationToken);
                    await attempts.SaveChangesAsync(cancellationToken);
                    await requests.SaveChangesAsync(cancellationToken);
                    break;
                }

                if (distinctOptions.Length == 0)
                {
                    // Nothing found yet, not a failure — leave the request in the
                    // automatic queue. The cooldown above means this format is tried
                    // again once it elapses, with no librarian action needed.
                    await attempts.SaveChangesAsync(cancellationToken);
                    continue;
                }

                var option = distinctOptions[0];
                ManualImportResult result;
                try
                {
                    result = await acquisition.AcquireAndEvaluateAsync(
                        request.Id,
                        format.Id,
                        option.ProviderId,
                        option.ProviderResultId,
                        cancellationToken);
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
                    await MarkForReviewAsync(request, result.Error ?? "The automatic copy could not be acquired.", cancellationToken);
                    await attempts.SaveChangesAsync(cancellationToken);
                    await requests.SaveChangesAsync(cancellationToken);
                    break;
                }

                attempts.Add(new ProviderAttempt(
                    request.Id, format.Id, option.ProviderId, ProviderAttemptOutcome.Acquired,
                    "A high-confidence copy was acquired and sent through the security pipeline.", clock.UtcNow,
                    nextEligibleCheckAtUtc: null));
                await attempts.SaveChangesAsync(cancellationToken);
                processed++;
            }
        }

        return processed;
    }

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

    private async Task MarkForReviewAsync(BookRequest request, string reason, CancellationToken cancellationToken)
    {
        if (request.Status != RequestStatus.PendingAcquisition)
        {
            return;
        }

        request.TransitionTo(RequestStatus.NeedsReview, actorUserId: null, reason, clock.UtcNow);
        var view = await requests.FindAdminViewAsync(request.Id, cancellationToken);
        var workTitle = view?.Request.WorkTitle ?? request.WorkId.ToString();
        await notifications.RecordRequestNeedsReviewAsync(request.Id, workTitle, reason, cancellationToken);
    }

    private static string DescribeProviderFailure(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: { } statusCode } =>
            $"The automatic provider returned HTTP {(int)statusCode}; it will be tried again automatically.",
        TaskCanceledException =>
            "The automatic provider timed out; it will be tried again automatically.",
        _ => "The automatic provider could not be reached; it will be tried again automatically."
    };

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
