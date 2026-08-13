using FamilyLibrarian.Application.Accounts;
using FamilyLibrarian.Domain;
using FamilyLibrarian.Domain.Accounts;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.Identity;

/// <summary>
/// Implements the application's account boundary over ASP.NET Core Identity.
/// </summary>
public sealed class IdentityUserAccountStore(UserManager<AppUser> userManager) : IUserAccountStore
{
    public async Task<IReadOnlyList<UserAccount>> ListAsync(CancellationToken cancellationToken)
    {
        var users = await userManager.Users
            .OrderBy(user => user.DisplayName)
            .ThenBy(user => user.Email)
            .ToArrayAsync(cancellationToken);

        var admins = await GetAdminIdsAsync(cancellationToken);

        return users.Select(user => ToAccount(user, admins.Contains(user.Id))).ToArray();
    }

    public async Task<UserAccount?> FindAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await userManager.Users
            .SingleOrDefaultAsync(candidate => candidate.Id == userId, cancellationToken);

        return user is null
            ? null
            : ToAccount(user, await userManager.IsInRoleAsync(user, RoleNames.Admin));
    }

    public async Task<UserAccount?> FindByEmailAsync(
        string email,
        CancellationToken cancellationToken)
    {
        var user = await userManager.FindByEmailAsync(email);
        return user is null
            ? null
            : ToAccount(user, await userManager.IsInRoleAsync(user, RoleNames.Admin));
    }

    public async Task<int> CountAdminsAsync(CancellationToken cancellationToken)
    {
        // Only accounts that can actually sign in count. A disabled administrator
        // is not a way back into the installation, so it must not satisfy the
        // "there is still another administrator" check.
        var admins = await userManager.GetUsersInRoleAsync(RoleNames.Admin);
        return admins.Count(admin => UserStatuses.CanSignIn(admin.Status));
    }

    public async Task<AccountOperationResult> CreateAsync(
        string email,
        string displayName,
        string password,
        UserStatus status,
        bool isAdmin,
        CancellationToken cancellationToken)
    {
        var user = new AppUser
        {
            UserName = email,
            Email = email,
            // Not a claim that the address was proven. It records that an
            // administrator vouched for it by inviting it, which is the trust
            // anchor this deployment has until email delivery exists.
            EmailConfirmed = true,
            DisplayName = displayName,
            Status = status,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        var created = await userManager.CreateAsync(user, password);
        if (!created.Succeeded)
        {
            return AccountOperationResult.Failure(Describe(created));
        }

        var roleAdded = await userManager.AddToRoleAsync(user, RoleNames.User);
        if (!roleAdded.Succeeded)
        {
            return AccountOperationResult.Failure(Describe(roleAdded));
        }

        if (isAdmin)
        {
            var adminAdded = await userManager.AddToRoleAsync(user, RoleNames.Admin);
            if (!adminAdded.Succeeded)
            {
                return AccountOperationResult.Failure(Describe(adminAdded));
            }
        }

        return AccountOperationResult.Success(user.Id);
    }

    public async Task<AccountOperationResult> SetStatusAsync(
        Guid userId,
        UserStatus status,
        CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return AccountOperationResult.Failure("That account no longer exists.");
        }

        user.Status = status;
        var updated = await userManager.UpdateAsync(user);
        if (!updated.Succeeded)
        {
            return AccountOperationResult.Failure(Describe(updated));
        }

        // The status check at sign-in only governs new sessions. Rotating the
        // security stamp is what invalidates the cookie the account is already
        // holding, so a disabled account stops working now rather than whenever
        // its cookie happened to expire.
        if (!UserStatuses.CanSignIn(status))
        {
            var stamped = await userManager.UpdateSecurityStampAsync(user);
            if (!stamped.Succeeded)
            {
                return AccountOperationResult.Failure(Describe(stamped));
            }
        }

        return AccountOperationResult.Success(user.Id);
    }

    public async Task<AccountOperationResult> SetPasswordAsync(
        Guid userId,
        string password,
        CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return AccountOperationResult.Failure("That account no longer exists.");
        }

        // Remove-then-add rather than a reset token: there is no email delivery
        // to carry a token through yet, and this path is already Admin-only.
        if (await userManager.HasPasswordAsync(user))
        {
            var removed = await userManager.RemovePasswordAsync(user);
            if (!removed.Succeeded)
            {
                return AccountOperationResult.Failure(Describe(removed));
            }
        }

        var added = await userManager.AddPasswordAsync(user, password);
        if (!added.Succeeded)
        {
            return AccountOperationResult.Failure(Describe(added));
        }

        // A password change ends every other session the account had open.
        var stamped = await userManager.UpdateSecurityStampAsync(user);
        return stamped.Succeeded
            ? AccountOperationResult.Success(user.Id)
            : AccountOperationResult.Failure(Describe(stamped));
    }

    public async Task<AccountOperationResult> SetAdminAsync(
        Guid userId,
        bool isAdmin,
        CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return AccountOperationResult.Failure("That account no longer exists.");
        }

        var alreadyAdmin = await userManager.IsInRoleAsync(user, RoleNames.Admin);
        if (alreadyAdmin == isAdmin)
        {
            return AccountOperationResult.Success(user.Id);
        }

        var result = isAdmin
            ? await userManager.AddToRoleAsync(user, RoleNames.Admin)
            : await userManager.RemoveFromRoleAsync(user, RoleNames.Admin);

        if (!result.Succeeded)
        {
            return AccountOperationResult.Failure(Describe(result));
        }

        // The role lives in the cookie's claims, so without this the account
        // keeps its old privileges for the life of its current session.
        var stamped = await userManager.UpdateSecurityStampAsync(user);
        return stamped.Succeeded
            ? AccountOperationResult.Success(user.Id)
            : AccountOperationResult.Failure(Describe(stamped));
    }

    private async Task<HashSet<Guid>> GetAdminIdsAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var admins = await userManager.GetUsersInRoleAsync(RoleNames.Admin);
        return admins.Select(admin => admin.Id).ToHashSet();
    }

    private static UserAccount ToAccount(AppUser user, bool isAdmin) => new(
        user.Id,
        user.Email ?? user.UserName ?? string.Empty,
        string.IsNullOrWhiteSpace(user.DisplayName)
            ? user.Email ?? user.UserName ?? "Family member"
            : user.DisplayName,
        user.Status,
        isAdmin,
        user.CreatedAtUtc,
        user.LastLoginAtUtc);

    private static string Describe(IdentityResult result) =>
        string.Join(" ", result.Errors.Select(error => error.Description));
}
