using FamilyLibrarian.Domain.Delivery;

namespace FamilyLibrarian.Application.Delivery;

public interface IDeliveryAttemptRepository
{
    Task<DeliveryAttempt?> FindAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<DeliveryAttempt>> ListForRequestAsync(Guid requestId, CancellationToken cancellationToken);

    Task<IReadOnlyList<DeliveryAttempt>> ListForUserAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>The admin Publishing Queue: recent Kindle delivery attempts.</summary>
    Task<IReadOnlyList<DeliveryAttemptView>> ListRecentAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Select the latest row per delivery before filtering eligibility: <see cref="DeliveryAttemptStatus.Failed"/>,
    /// <see cref="DeliveryAttempt.IsRetryable"/>, below <paramref name="maxAttemptNumber"/>,
    /// and completed before <paramref name="olderThanUtc"/> (the cooldown boundary for
    /// that attempt number).
    /// </summary>
    Task<IReadOnlyList<DeliveryAttempt>> ListRetryableFailedAsync(
        DateTimeOffset olderThanUtc, int maxAttemptNumber, CancellationToken cancellationToken);

    Task<DeliveryAttempt?> FindLatestAsync(Guid deliveryId, CancellationToken cancellationToken);

    /// <summary>Fresh authorization/eligibility check, including owner, target and request membership.</summary>
    Task<DeliveryTarget?> GetEligibleTargetAsync(DeliveryAttempt attempt, CancellationToken cancellationToken);

    /// <summary>Persist before dispatch; false means another caller already created this attempt.</summary>
    Task<bool> TryAddAsync(DeliveryAttempt attempt, CancellationToken cancellationToken);

    /// <summary>Compare-and-save using the loaded row version. A competing transition wins.</summary>
    Task<bool> TryTransitionAsync(DeliveryAttempt attempt, DeliveryAttemptStatus status, DateTimeOffset atUtc,
        CancellationToken cancellationToken, string? reason = null, bool retryable = false);

    Task<IReadOnlyList<DeliveryAttempt>> ListUnfinishedAsync(CancellationToken cancellationToken);

    /// <summary>Reconcile durable request intent against already verified library imports.</summary>
    Task<IReadOnlyList<ReadyRequestDelivery>> ListUnreleasedAsync(CancellationToken cancellationToken);

    void Add(DeliveryAttempt attempt);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public sealed record ReadyRequestDelivery(
    FamilyLibrarian.Domain.Requests.BookRequest Request, string ExternalBookId, string BookFormat, string? WorkTitle);
