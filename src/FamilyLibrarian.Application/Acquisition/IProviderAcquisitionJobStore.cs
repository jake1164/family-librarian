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

    void Add(ProviderAcquisitionJob job);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
