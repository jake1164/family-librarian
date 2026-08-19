using System.Security.Cryptography;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Integrations;
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
/// cadence. A result is evidence for a librarian, never permission to fetch a
/// third-party file without review.
/// </summary>
public sealed class ExternalProviderRecheckService(
    IRequestRepository requests,
    IProviderAttemptRepository attempts,
    IExternalProviderStore providers,
    IExternalProviderClient client,
    ICredentialProtector protector,
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

                    if (work is null)
                    {
                        AddAttempt(request, format, provider, ProviderAttemptOutcome.Failed,
                            "The requested work is no longer available for provider lookup.", nextCheck);
                        continue;
                    }

                    try
                    {
                        var apiKey = provider.HasApiKey
                            ? protector.Unprotect(
                                ExternalProviderSecretPurposes.ApiKey, provider.ProtectedApiKey!, provider.ApiKeyFormatVersion)
                            : null;
                        var candidates = await client.SearchAsync(
                            provider.BaseUrl,
                            apiKey,
                            new ExternalProviderSearchRequest(
                                request.Id, format.MediaType, work.Title,
                                work.PrimaryAuthor is null ? [] : [work.PrimaryAuthor], Isbn13: null),
                            resolution.Route!,
                            cancellationToken);

                        if (candidates.Count == 0)
                        {
                            AddAttempt(request, format, provider, ProviderAttemptOutcome.NoMatch,
                                "No matching candidate was reported by this provider.", nextCheck);
                            continue;
                        }

                        AddAttempt(request, format, provider, ProviderAttemptOutcome.CandidatesFound,
                            $"Found {candidates.Count} candidate(s); librarian review is required before acquisition.",
                            nextEligibleCheckAtUtc: null);
                        await MarkForReviewAsync(
                            request, work.Title, $"{provider.DisplayName} found a candidate that needs librarian review.",
                            cancellationToken);
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
}
