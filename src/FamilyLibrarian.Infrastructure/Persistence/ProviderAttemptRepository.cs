using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Domain.Acquisition;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.Persistence;

public sealed class ProviderAttemptRepository(AppDbContext database) : IProviderAttemptRepository
{
    public async Task<IReadOnlyList<ProviderAttempt>> ListForRequestAsync(
        Guid requestId,
        CancellationToken cancellationToken) =>
        await database.ProviderAttempts
            .Where(attempt => attempt.RequestId == requestId)
            .OrderByDescending(attempt => attempt.AttemptedAtUtc)
            .ToArrayAsync(cancellationToken);

    public async Task<IReadOnlyList<ProviderAttempt>> ListRecentAsync(
        int maximumCount,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCount);

        // The dashboard shows one row per format+provider lookup, not every
        // outcome in that lookup's history. Over-fetch a wider recent window
        // so collapsing repeated lookups for the same format+provider still
        // leaves a full page of distinct rows, then trim to the requested
        // count. Grouping happens in memory because EF Core cannot translate
        // a "latest per group" query against Npgsql.
        var recent = await database.ProviderAttempts
            .AsNoTracking()
            .OrderByDescending(attempt => attempt.AttemptedAtUtc)
            .ThenByDescending(attempt => attempt.Id)
            .Take(maximumCount * 3)
            .ToArrayAsync(cancellationToken);

        return recent
            .GroupBy(attempt => (attempt.RequestFormatId, attempt.ProviderId))
            .Select(group => group.First())
            .Take(maximumCount)
            .ToArray();
    }

    public Task<ProviderAttempt?> FindLatestForFormatAsync(
        Guid requestFormatId,
        string providerId,
        CancellationToken cancellationToken)
    {
        // Provider ids are normalized at the domain boundary. Normalize the
        // parameter before composing SQL so PostgreSQL can use the composite
        // index instead of applying a function to its provider_id column.
        var normalizedProviderId = providerId.Trim().ToLowerInvariant();
        return database.ProviderAttempts
            .Where(attempt => attempt.RequestFormatId == requestFormatId && attempt.ProviderId == normalizedProviderId)
            .OrderByDescending(attempt => attempt.AttemptedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ProviderAttempt>> ListLatestByProviderAsync(
        CancellationToken cancellationToken) =>
        await database.ProviderAttempts
            .AsNoTracking()
            .GroupBy(attempt => attempt.ProviderId)
            .Select(group => group
                .OrderByDescending(attempt => attempt.AttemptedAtUtc)
                .ThenByDescending(attempt => attempt.Id)
                .First())
            .ToArrayAsync(cancellationToken);

    public void Add(ProviderAttempt attempt) => database.ProviderAttempts.Add(attempt);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => database.SaveChangesAsync(cancellationToken);
}
