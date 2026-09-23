using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.Acquisition;

public sealed class ProviderAcquisitionJobStore(AppDbContext database) : IProviderAcquisitionJobStore
{
    public Task<ProviderAcquisitionJob?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        database.ProviderAcquisitionJobs
            .Include(job => job.Outputs)
            .FirstOrDefaultAsync(job => job.Id == id, cancellationToken);

    public Task<ProviderAcquisitionJob?> FindByIdempotencyKeyAsync(
        Guid externalProviderId, string idempotencyKey, CancellationToken cancellationToken) =>
        database.ProviderAcquisitionJobs
            .Include(job => job.Outputs)
            .FirstOrDefaultAsync(
                job => job.ExternalProviderId == externalProviderId && job.IdempotencyKey == idempotencyKey,
                cancellationToken);

    public async Task<IReadOnlyList<ProviderAcquisitionJob>> ListDueForPollAsync(
        DateTimeOffset asOfUtc, int maximumCount, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCount);

        return await database.ProviderAcquisitionJobs
            .Where(job => job.NextPollAtUtc != null && job.NextPollAtUtc <= asOfUtc)
            .OrderBy(job => job.NextPollAtUtc)
            .Take(maximumCount)
            .ToArrayAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ProviderAcquisitionJob>> ListWaitingForInteractionAsync(
        CancellationToken cancellationToken) =>
        await database.ProviderAcquisitionJobs
            .Where(job => job.LifecycleState == ProviderAcquisitionJobLifecycleState.Waiting &&
                job.InteractionType != null)
            .OrderBy(job => job.InteractionExpiresAtUtc)
            .ThenBy(job => job.CreatedAtUtc)
            .ToArrayAsync(cancellationToken);

    public void Add(ProviderAcquisitionJob job) => database.ProviderAcquisitionJobs.Add(job);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => database.SaveChangesAsync(cancellationToken);
}
