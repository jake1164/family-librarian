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
            .Include(job => job.Outputs)
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

    public Task<bool> HasLeftWaitingSinceAsync(
        Guid externalProviderId, DateTimeOffset sinceUtc, CancellationToken cancellationToken) =>
        database.ProviderAcquisitionJobs.AnyAsync(
            job => job.ExternalProviderId == externalProviderId && job.LeftWaitingAtUtc != null &&
                job.LeftWaitingAtUtc >= sinceUtc,
            cancellationToken);

    public async Task<IReadOnlyList<ProviderAcquisitionJob>> ListByPartSetAsync(
        Guid partSetId, CancellationToken cancellationToken) =>
        await database.ProviderAcquisitionJobs
            .Include(job => job.Outputs)
            .Where(job => job.PartSetId == partSetId)
            .OrderBy(job => job.PartNumber)
            .ToArrayAsync(cancellationToken);

    public async Task<IReadOnlyList<ProviderAcquisitionJob>> ListWithProviderJobAsync(
        int maximumCount, CancellationToken cancellationToken) =>
        await database.ProviderAcquisitionJobs
            .Where(job => job.ProviderJobId != null)
            .OrderByDescending(job => job.CreatedAtUtc)
            .Take(maximumCount)
            .ToArrayAsync(cancellationToken);

    public async Task<IReadOnlyList<AttemptJobFacts>> ListAttemptJobFactsAsync(
        IReadOnlyCollection<Guid> jobIds, CancellationToken cancellationToken)
    {
        if (jobIds.Count == 0)
        {
            return [];
        }

        var linked = await database.ProviderAcquisitionJobs
            .AsNoTracking()
            .Where(job => jobIds.Contains(job.Id))
            .Select(job => new AttemptJobFacts(job.Id, job.LifecycleState, job.PartSetId))
            .ToListAsync(cancellationToken);
        var setIds = linked.Where(fact => fact.PartSetId != null).Select(fact => fact.PartSetId!.Value).Distinct().ToArray();
        if (setIds.Length > 0)
        {
            linked.AddRange(await database.ProviderAcquisitionJobs
                .AsNoTracking()
                .Where(job => job.PartSetId != null && setIds.Contains(job.PartSetId.Value) && !jobIds.Contains(job.Id))
                .Select(job => new AttemptJobFacts(job.Id, job.LifecycleState, job.PartSetId))
                .ToListAsync(cancellationToken));
        }

        return linked;
    }

    public async Task<IReadOnlyList<ProviderAcquisitionJob>> ListActiveForFormatAsync(
        Guid requestFormatId, CancellationToken cancellationToken) =>
        await database.ProviderAcquisitionJobs
            .Include(job => job.Outputs)
            .Where(job => job.RequestFormatId == requestFormatId &&
                (job.LifecycleState == ProviderAcquisitionJobLifecycleState.Queued ||
                 job.LifecycleState == ProviderAcquisitionJobLifecycleState.Running ||
                 job.LifecycleState == ProviderAcquisitionJobLifecycleState.Waiting))
            .ToArrayAsync(cancellationToken);

    public void Add(ProviderAcquisitionJob job) => database.ProviderAcquisitionJobs.Add(job);

    public async Task<IReadOnlyList<SourceJobActivity>> ListSourceActivityAsync(CancellationToken cancellationToken) =>
        await database.ProviderAcquisitionJobs.AsNoTracking()
            .Where(job => job.LifecycleState == ProviderAcquisitionJobLifecycleState.Queued ||
                job.LifecycleState == ProviderAcquisitionJobLifecycleState.Running ||
                job.LifecycleState == ProviderAcquisitionJobLifecycleState.Waiting)
            .Select(job => new SourceJobActivity(job.ProviderId, job.RequestFormatId,
                job.LifecycleState, job.Phase, job.InteractionType != null))
            .ToArrayAsync(cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => database.SaveChangesAsync(cancellationToken);
}
