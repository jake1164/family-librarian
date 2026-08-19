using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Providers;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Application.Acquisition;

/// <summary>
/// Moves a request through unattended acquisition only when an explicitly
/// opt-in provider returns one high-confidence direct-download match.
/// </summary>
/// <remarks>
/// This service intentionally cannot use store offers, external actions, or
/// admin-registered external providers. Those options may be useful to show a
/// librarian, but do not carry the provider-specific confidence guarantee this
/// workflow requires. Every acquired file still enters quarantine, is scanned,
/// structurally validated, identity-checked, and only then sent to its library.
/// </remarks>
public sealed class AutomaticRequestFulfillmentService(
    IRequestRepository requests,
    IProviderAttemptRepository attempts,
    IExternalProviderStore externalProviders,
    IEnumerable<IAutomaticDirectAcquisitionProvider> providers,
    DirectAcquisitionSecurityService acquisition,
    IClock clock)
{
    private const int BatchSize = 20;

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
                    if (IsCurrentPendingCycleAttempt(latestAttempt, request))
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

                if (distinctOptions.Length != 1)
                {
                    // A scheduled external provider can still discover an option,
                    // but never downloads it unattended. Without one, this is a
                    // terminal automatic attempt and belongs in the review queue.
                    if (distinctOptions.Length > 1 || !await HasScheduledExternalProviderAsync(cancellationToken))
                    {
                        MarkForReview(request, distinctOptions.Length == 0
                            ? "No high-confidence automatic copy was found."
                            : "More than one high-confidence automatic copy was found.");
                    }

                    await attempts.SaveChangesAsync(cancellationToken);
                    await requests.SaveChangesAsync(cancellationToken);
                    break;
                }

                var option = distinctOptions[0];
                var result = await acquisition.AcquireAndEvaluateAsync(
                    request.Id,
                    format.Id,
                    option.ProviderId,
                    option.ProviderResultId,
                    cancellationToken);

                if (result.Outcome != ManualImportOutcome.Success)
                {
                    attempts.Add(new ProviderAttempt(
                        request.Id, format.Id, option.ProviderId, ProviderAttemptOutcome.Failed,
                        result.Error ?? "The automatic copy could not be acquired.", clock.UtcNow,
                        nextEligibleCheckAtUtc: null));
                    MarkForReview(request, result.Error ?? "The automatic copy could not be acquired.");
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

    private async Task<bool> HasScheduledExternalProviderAsync(CancellationToken cancellationToken) =>
        (await externalProviders.ListEnabledAsync(cancellationToken))
            .Any(provider => provider.RecheckSchedule is ProviderRecheckSchedule.Daily or ProviderRecheckSchedule.Weekly);

    /// <summary>
    /// A user can cancel and then reopen a request. The old lookup remains
    /// valuable audit history, but it must not suppress a new fulfillment pass
    /// for the reopened request.
    /// </summary>
    private static bool IsCurrentPendingCycleAttempt(ProviderAttempt? attempt, BookRequest request) =>
        attempt is not null && attempt.AttemptedAtUtc >= request.StatusChangedAtUtc;

    private void MarkForReview(BookRequest request, string reason)
    {
        if (request.Status == RequestStatus.PendingAcquisition)
        {
            request.TransitionTo(RequestStatus.NeedsReview, actorUserId: null, reason, clock.UtcNow);
        }
    }

    private static string DescribeProviderFailure(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: { } statusCode } =>
            $"The automatic provider returned HTTP {(int)statusCode}; automatic retry is disabled for this source.",
        TaskCanceledException =>
            "The automatic provider timed out; automatic retry is disabled for this source.",
        _ => "The automatic provider could not be reached; automatic retry is disabled for this source."
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
