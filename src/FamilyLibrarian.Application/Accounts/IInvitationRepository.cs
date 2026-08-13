using FamilyLibrarian.Domain.Accounts;

namespace FamilyLibrarian.Application.Accounts;

public interface IInvitationRepository
{
    Task<IReadOnlyList<Invitation>> ListAsync(CancellationToken cancellationToken);

    Task<Invitation?> FindAsync(Guid invitationId, CancellationToken cancellationToken);

    /// <summary>
    /// Finds an invitation by the hash of a presented token.
    /// </summary>
    /// <remarks>
    /// Lookup is by hash rather than by scanning and comparing, so the stored
    /// value is the only thing ever compared and the token never needs to be
    /// held alongside the row.
    /// </remarks>
    Task<Invitation?> FindByTokenHashAsync(string tokenHash, CancellationToken cancellationToken);

    /// <summary>Outstanding (unredeemed, unrevoked, unexpired) invitations for an address.</summary>
    Task<IReadOnlyList<Invitation>> FindOutstandingForEmailAsync(
        string normalizedEmail,
        DateTimeOffset atUtc,
        CancellationToken cancellationToken);

    void Add(Invitation invitation);

    Task SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Runs <paramref name="operation"/> in a database transaction.
    /// </summary>
    /// <remarks>
    /// Redemption creates an account and burns the invitation. Those must commit
    /// together: a half-completed redemption either strands a live invitation
    /// beside a real account, or burns an invitation that produced nothing.
    /// </remarks>
    Task<TResult> InTransactionAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken);
}

/// <summary>Creates and hashes invitation tokens.</summary>
public interface IInvitationTokenGenerator
{
    /// <summary>A fresh, unguessable, URL-safe token. Returned to the caller once.</summary>
    string CreateToken();

    /// <summary>The stored form of a token.</summary>
    string Hash(string token);
}
