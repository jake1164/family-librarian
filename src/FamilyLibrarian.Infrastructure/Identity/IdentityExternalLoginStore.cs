using FamilyLibrarian.Application.Accounts;
using FamilyLibrarian.Domain;
using FamilyLibrarian.Domain.Accounts;
using Microsoft.AspNetCore.Identity;

namespace FamilyLibrarian.Infrastructure.Identity;

/// <summary>Implements external-login linking over ASP.NET Core Identity's own login table.</summary>
public sealed class IdentityExternalLoginStore(UserManager<AppUser> userManager) : IExternalLoginStore
{
    public async Task<Guid?> FindLinkedUserIdAsync(
        string issuer, string subject, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var user = await userManager.FindByLoginAsync(issuer, subject);
        return user?.Id;
    }

    public async Task LinkAsync(
        Guid userId, string issuer, string subject, string? providerDisplayName, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var user = await userManager.FindByIdAsync(userId.ToString())
            ?? throw new InvalidOperationException("That account no longer exists.");

        var result = await userManager.AddLoginAsync(
            user, new UserLoginInfo(issuer, subject, providerDisplayName ?? issuer));
        EnsureSucceeded(result, "link the external login");
    }

    public async Task<Guid> CreatePasswordlessAsync(
        string email,
        string displayName,
        UserStatus status,
        bool isAdmin,
        string issuer,
        string subject,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var user = new AppUser
        {
            UserName = email,
            Email = email,
            // Not a claim the address was proven independently — it comes from a
            // token the configured issuer just signed, which is the trust anchor
            // for every externally-provisioned account.
            EmailConfirmed = true,
            DisplayName = displayName,
            Status = status,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        // No password overload: this account can only ever sign in through a
        // linked external login, never a local one.
        var created = await userManager.CreateAsync(user);
        EnsureSucceeded(created, "create the account");

        var roleAdded = await userManager.AddToRoleAsync(user, RoleNames.User);
        EnsureSucceeded(roleAdded, "assign the user role");

        if (isAdmin)
        {
            var adminAdded = await userManager.AddToRoleAsync(user, RoleNames.Admin);
            EnsureSucceeded(adminAdded, "assign the administrator role");
        }

        var loginAdded = await userManager.AddLoginAsync(
            user, new UserLoginInfo(issuer, subject, displayName));
        EnsureSucceeded(loginAdded, "link the external login");

        return user.Id;
    }

    private static void EnsureSucceeded(IdentityResult result, string operation)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Unable to {operation}: {string.Join(" ", result.Errors.Select(error => error.Description))}");
        }
    }
}
