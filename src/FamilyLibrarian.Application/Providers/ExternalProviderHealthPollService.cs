using System.Security.Cryptography;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Notifications;
using FamilyLibrarian.Domain.Providers;

namespace FamilyLibrarian.Application.Providers;

/// <summary>
/// Independent periodic <c>/health</c> probe for every enabled external
/// provider, deliberately decoupled from <see cref="ExternalProvider.RecheckSchedule"/>
/// -- that field governs candidate-lookup cadence only (see
/// <c>ExternalProviderRecheckService</c>) and must not gate whether the
/// provider itself gets checked for reachability. Driven by
/// <c>ExternalProviderHealthPollService</c>'s hosted-service wrapper on a
/// fixed interval, the same shape as <c>PublishingDestinationHealthHostedService</c>
/// already uses for CWA/Audiobookshelf.
/// </summary>
/// <remarks>
/// Every failure mode here is recorded, never silently skipped: an
/// unreachable provider and an
/// undecryptable stored credential both get the same "unhealthy, here's why"
/// treatment as an ordinary connection failure, so the Sources page chip and
/// (on a transition into non-operational) an admin notification both fire.
/// Silently leaving any of these untouched would recreate exactly the blind
/// spot this service exists to close.
/// </remarks>
public sealed class ExternalProviderHealthPollService(
    IExternalProviderStore store,
    ExternalCandidateAvailabilityChecker candidateChecker,
    NotificationService notifications,
    IClock clock)
{
    public async Task<int> CheckAllEnabledAsync(CancellationToken cancellationToken)
    {
        var enabled = await store.ListEnabledAsync(cancellationToken);
        var checkedCount = 0;
        var changed = false;

        foreach (var provider in enabled)
        {
            var wasOperational = provider.LastTestSucceeded != false;
            checkedCount++;

            try
            {
                var health = await candidateChecker.CheckHealthAsync(provider, cancellationToken);
                provider.RecordHealthCheck(
                    health.IsFullyOperational,
                    health.IsFullyOperational
                        ? "Reachable on periodic background check."
                        : "Reachable on periodic background check, but search or acquire was reported unavailable.",
                    health.Status.ToString(), health.Search.ToString(), health.Acquire.ToString(), clock.UtcNow);
                changed = true;
                await NotifyIfDegradedAsync(provider, wasOperational, cancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                provider.RecordHealthCheck(
                    false,
                    $"The provider is unreachable: {exception.Message}",
                    healthStatus: nameof(ProviderHealthStatus.Unhealthy),
                    searchOperationStatus: nameof(ProviderOperationalStatus.Unavailable),
                    acquireOperationStatus: nameof(ProviderOperationalStatus.Unavailable),
                    clock.UtcNow);
                changed = true;
                await NotifyIfDegradedAsync(provider, wasOperational, cancellationToken);
            }
            catch (CryptographicException)
            {
                // Almost always a stored API key that can no longer be
                // decrypted (e.g. the data protection key ring rotated, or
                // the row is corrupted) -- a real, persistent, operator-
                // actionable failure. Recorded the same as any other
                // failure, not silently skipped: left untouched, this
                // provider would fail the same way every 15 minutes forever
                // with no chip change and no notification ever reaching the
                // admin -- the exact blind spot this service exists to close.
                provider.RecordHealthCheck(
                    false,
                    "This provider's stored API key could not be read -- it may need to be re-entered.",
                    healthStatus: nameof(ProviderHealthStatus.Unhealthy),
                    searchOperationStatus: nameof(ProviderOperationalStatus.Unavailable),
                    acquireOperationStatus: nameof(ProviderOperationalStatus.Unavailable),
                    clock.UtcNow);
                changed = true;
                await NotifyIfDegradedAsync(provider, wasOperational, cancellationToken);
            }
        }

        if (changed)
        {
            await store.SaveChangesAsync(cancellationToken);
        }

        return checkedCount;
    }

    /// <summary>
    /// Only a transition into non-operational raises a notification -- not
    /// every still-down poll tick, since <see cref="NotificationService"/>'s
    /// upsert un-dismisses on every recur and a provider that stays broken
    /// for days would otherwise nag every 15 minutes forever. Recovery is
    /// deliberately silent: the chip already shows it, and there is no
    /// "resolved" counterpart to any notification in this codebase.
    /// </summary>
    private Task NotifyIfDegradedAsync(ExternalProvider provider, bool wasOperational, CancellationToken cancellationToken) =>
        wasOperational && provider.LastTestSucceeded == false
            ? notifications.RecordProviderHealthDegradedAsync(
                provider.Id, provider.DisplayName, provider.LastTestMessage ?? "This provider is no longer reachable.",
                cancellationToken)
            : Task.CompletedTask;
}
