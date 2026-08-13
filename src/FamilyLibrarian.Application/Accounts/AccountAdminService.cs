using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Domain.Accounts;
using FamilyLibrarian.Domain.Audit;

namespace FamilyLibrarian.Application.Accounts;

/// <summary>
/// Administrator operations on family accounts.
/// </summary>
/// <remarks>
/// Every method here is Admin-authorized at the endpoint boundary. What this type
/// adds are the rules that must hold regardless of which administrator calls it:
/// an administrator cannot lock themselves out, and the last administrator cannot
/// be removed. Without those, a single mis-click leaves an installation with no
/// way back in — the bootstrap refuses to run once any administrator exists.
/// </remarks>
public sealed class AccountAdminService(
    IUserAccountStore accounts,
    IAuditWriter audit,
    ICurrentUser currentUser)
{
    public Task<IReadOnlyList<UserAccount>> ListAsync(CancellationToken cancellationToken) =>
        accounts.ListAsync(cancellationToken);

    public async Task<AccountOperationResult> SetStatusAsync(
        Guid userId,
        UserStatus status,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(status))
        {
            return AccountOperationResult.Failure("That is not an account status.");
        }

        var account = await accounts.FindAsync(userId, cancellationToken);
        if (account is null)
        {
            return AccountOperationResult.Failure("That account no longer exists.");
        }

        var wouldLoseAccess = !UserStatuses.CanSignIn(status);

        if (wouldLoseAccess && userId == currentUser.UserId)
        {
            return AccountOperationResult.Failure("You cannot disable your own account.");
        }

        if (wouldLoseAccess && account.IsAdmin && await IsLastAdminAsync(cancellationToken))
        {
            return AccountOperationResult.Failure(
                "This is the only administrator. Grant the role to someone else first.");
        }

        var result = await accounts.SetStatusAsync(userId, status, cancellationToken);
        if (result.Succeeded)
        {
            await audit.WriteAsync(
                AuditActions.AccountStatusChanged,
                AuditSubjectTypes.Account,
                userId.ToString(),
                new { UserId = userId, Status = status.ToString() },
                cancellationToken);
        }

        return result;
    }

    public async Task<AccountOperationResult> SetAdminAsync(
        Guid userId,
        bool isAdmin,
        CancellationToken cancellationToken)
    {
        var account = await accounts.FindAsync(userId, cancellationToken);
        if (account is null)
        {
            return AccountOperationResult.Failure("That account no longer exists.");
        }

        if (!isAdmin && userId == currentUser.UserId)
        {
            return AccountOperationResult.Failure("You cannot remove your own administrator role.");
        }

        if (!isAdmin && account.IsAdmin && await IsLastAdminAsync(cancellationToken))
        {
            return AccountOperationResult.Failure(
                "This is the only administrator. Grant the role to someone else first.");
        }

        var result = await accounts.SetAdminAsync(userId, isAdmin, cancellationToken);
        if (result.Succeeded)
        {
            await audit.WriteAsync(
                isAdmin ? AuditActions.AccountAdminGranted : AuditActions.AccountAdminRevoked,
                AuditSubjectTypes.Account,
                userId.ToString(),
                new { UserId = userId, IsAdmin = isAdmin },
                cancellationToken);
        }

        return result;
    }

    /// <summary>
    /// Sets a new password for an account, for the family member who has locked
    /// themselves out and has no email delivery to reset through yet.
    /// </summary>
    public async Task<AccountOperationResult> SetPasswordAsync(
        Guid userId,
        string password,
        CancellationToken cancellationToken)
    {
        if (await accounts.FindAsync(userId, cancellationToken) is null)
        {
            return AccountOperationResult.Failure("That account no longer exists.");
        }

        var result = await accounts.SetPasswordAsync(userId, password, cancellationToken);
        if (result.Succeeded)
        {
            // The audit records that a reset happened, never the password.
            await audit.WriteAsync(
                AuditActions.AccountPasswordReset,
                AuditSubjectTypes.Account,
                userId.ToString(),
                new { UserId = userId },
                cancellationToken);
        }

        return result;
    }

    private async Task<bool> IsLastAdminAsync(CancellationToken cancellationToken) =>
        await accounts.CountAdminsAsync(cancellationToken) <= 1;
}
