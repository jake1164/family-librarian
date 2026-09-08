using FamilyLibrarian.Domain.Delivery;

namespace FamilyLibrarian.Application.Delivery;

public interface IDeliveryAttemptRepository
{
    Task<DeliveryAttempt?> FindAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<DeliveryAttempt>> ListForRequestAsync(Guid requestId, CancellationToken cancellationToken);

    Task<IReadOnlyList<DeliveryAttempt>> ListForUserAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Attempts eligible for the retry sweep: <see cref="DeliveryAttemptStatus.Failed"/>,
    /// <see cref="DeliveryAttempt.IsRetryable"/>, below <paramref name="maxAttemptNumber"/>,
    /// and completed before <paramref name="olderThanUtc"/> (the cooldown boundary for
    /// that attempt number).
    /// </summary>
    Task<IReadOnlyList<DeliveryAttempt>> ListRetryableFailedAsync(
        DateTimeOffset olderThanUtc, int maxAttemptNumber, CancellationToken cancellationToken);

    void Add(DeliveryAttempt attempt);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
