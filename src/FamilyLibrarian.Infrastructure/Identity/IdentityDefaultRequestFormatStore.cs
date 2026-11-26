using FamilyLibrarian.Application.Accounts;
using FamilyLibrarian.Domain.Accounts;
using Microsoft.AspNetCore.Identity;

namespace FamilyLibrarian.Infrastructure.Identity;

public sealed class IdentityDefaultRequestFormatStore(UserManager<AppUser> userManager) : IDefaultRequestFormatStore
{
    public async Task<DefaultRequestFormat?> GetAsync(Guid userId, CancellationToken cancellationToken) =>
        (await userManager.FindByIdAsync(userId.ToString()))?.DefaultRequestFormat;

    public async Task<AccountOperationResult> SetAsync(
        Guid userId, DefaultRequestFormat format, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return AccountOperationResult.Failure("That account no longer exists.");
        }

        user.DefaultRequestFormat = format;
        var updated = await userManager.UpdateAsync(user);
        return updated.Succeeded
            ? AccountOperationResult.Success(user.Id)
            : AccountOperationResult.Failure(string.Join(" ", updated.Errors.Select(error => error.Description)));
    }
}
