using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FamilyLibrarian.Infrastructure.Acquisition;

public sealed class ProviderInteractionClaimStore(AppDbContext database) : IProviderInteractionClaimStore
{
    public Task<ProviderInteractionClaim?> FindAsync(Guid jobId, CancellationToken cancellationToken) =>
        database.ProviderInteractionClaims.FirstOrDefaultAsync(claim => claim.JobId == jobId, cancellationToken);

    public async Task<bool> TryInsertAsync(ProviderInteractionClaim claim, CancellationToken cancellationToken)
    {
        database.ProviderInteractionClaims.Add(claim);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            database.Entry(claim).State = EntityState.Detached;
            return false;
        }
    }

    public void Remove(ProviderInteractionClaim claim) => database.ProviderInteractionClaims.Remove(claim);

    public async Task ReleaseIfOwnedAsync(Guid jobId, Guid userId, Guid alertId, CancellationToken cancellationToken)
    {
        await database.ProviderInteractionClaims
            .Where(claim => claim.JobId == jobId && claim.ClaimedByUserId == userId && claim.AlertId == alertId)
            .ExecuteDeleteAsync(cancellationToken);
        database.ChangeTracker.Clear();
    }

    public async Task<IReadOnlyList<ProviderInteractionClaim>> ListAsync(CancellationToken cancellationToken) =>
        await database.ProviderInteractionClaims.ToArrayAsync(cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => database.SaveChangesAsync(cancellationToken);
}
