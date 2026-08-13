using FamilyLibrarian.Application.Accounts;
using FamilyLibrarian.Domain.Accounts;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.Persistence;

public sealed class InvitationRepository(AppDbContext database) : IInvitationRepository
{
    public async Task<IReadOnlyList<Invitation>> ListAsync(CancellationToken cancellationToken) =>
        await database.Invitations
            .OrderByDescending(invitation => invitation.CreatedAtUtc)
            .ToArrayAsync(cancellationToken);

    public Task<Invitation?> FindAsync(Guid invitationId, CancellationToken cancellationToken) =>
        database.Invitations.SingleOrDefaultAsync(
            invitation => invitation.Id == invitationId,
            cancellationToken);

    public Task<Invitation?> FindByTokenHashAsync(
        string tokenHash,
        CancellationToken cancellationToken) =>
        database.Invitations.SingleOrDefaultAsync(
            invitation => invitation.TokenHash == tokenHash,
            cancellationToken);

    public async Task<IReadOnlyList<Invitation>> FindOutstandingForEmailAsync(
        string normalizedEmail,
        DateTimeOffset atUtc,
        CancellationToken cancellationToken) =>
        await database.Invitations
            .Where(invitation => invitation.NormalizedEmail == normalizedEmail &&
                invitation.RedeemedAtUtc == null &&
                invitation.RevokedAtUtc == null &&
                invitation.ExpiresAtUtc > atUtc)
            .ToArrayAsync(cancellationToken);

    public void Add(Invitation invitation) => database.Invitations.Add(invitation);

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        database.SaveChangesAsync(cancellationToken);

    public async Task<TResult> InTransactionAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (database.Database.CurrentTransaction is not null)
        {
            return await operation(cancellationToken);
        }

        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var result = await operation(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }
}
