using FamilyLibrarian.Application.Setup;
using Microsoft.AspNetCore.Identity;

namespace FamilyLibrarian.Infrastructure.Identity;

public sealed class IdentityUserSetupStateStore(UserManager<AppUser> userManager) : IUserSetupStateStore
{
    public async Task<UserSetupState?> GetAsync(Guid userId, CancellationToken cancellationToken) =>
        await userManager.FindByIdAsync(userId.ToString()) is { } user
            ? new UserSetupState(user.SetupWizardSeenAtUtc, user.SetupCompletedSteps, user.SetupSkippedSteps)
            : null;

    public async Task<bool> SetAsync(Guid userId, UserSetupState state, CancellationToken cancellationToken)
    {
        if (await userManager.FindByIdAsync(userId.ToString()) is not { } user)
        {
            return false;
        }

        user.SetupWizardSeenAtUtc = state.WizardSeenAtUtc;
        user.SetupCompletedSteps = state.Completed;
        user.SetupSkippedSteps = state.Skipped;
        return (await userManager.UpdateAsync(user)).Succeeded;
    }
}
