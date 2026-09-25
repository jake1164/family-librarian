using FamilyLibrarian.Domain.Acquisition;

namespace FamilyLibrarian.Application.Acquisition;

public interface IProviderAcquisitionJobStore
{
    Task<ProviderAcquisitionJob?> FindAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Idempotency-key replay lookup (protocol v2 §8): a retried submit for
    /// the same provider using the same key must resolve to the same job
    /// row, never insert a duplicate.
    /// </summary>
    Task<ProviderAcquisitionJob?> FindByIdempotencyKeyAsync(
        Guid externalProviderId, string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>
    /// The background poller's due-work query: every job whose
    /// <see cref="ProviderAcquisitionJob.NextPollAtUtc"/> has arrived,
    /// including ones that existed before a Family Librarian restart — this
    /// is what makes restart recovery real rather than merely intended.
    /// </summary>
    Task<IReadOnlyList<ProviderAcquisitionJob>> ListDueForPollAsync(
        DateTimeOffset asOfUtc, int maximumCount, CancellationToken cancellationToken);

    /// <summary>
    /// Administrator-only work queue for provider jobs that are explicitly
    /// waiting on a human interaction. The returned job is the durable
    /// interaction record; no browser URL or provider credential is included
    /// in its client projection.
    /// </summary>
    Task<IReadOnlyList<ProviderAcquisitionJob>> ListWaitingForInteractionAsync(
        CancellationToken cancellationToken);

    /// <summary>
    /// Whether any job of this provider left <see cref="ProviderAcquisitionJobLifecycleState.Waiting"/>
    /// within the given window — HUMAN-ACQ-1's quiescence check (a provider actively
    /// draining its queue should not trigger a fresh alert for the next job in line).
    /// </summary>
    Task<bool> HasLeftWaitingSinceAsync(Guid externalProviderId, DateTimeOffset sinceUtc, CancellationToken cancellationToken);

    void Add(ProviderAcquisitionJob job);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
