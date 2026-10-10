using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Domain.Accounts;
using FamilyLibrarian.Domain.Audit;

namespace FamilyLibrarian.Application.Accounts;

/// <summary>
/// A household member's own audiobook narration preference -- self-service,
/// distinct from <see cref="AccountAdminService"/>'s administrator-only
/// operations on other accounts (see <c>IUserAccountStore.SetAudiobookNarrationPreferenceAsync</c>'s remark).
/// </summary>
public sealed class AudiobookNarrationPreferenceService(
    IUserAccountStore accounts,
    ICurrentUser currentUser,
    IAuditWriter audit)
{
    public async Task<AudiobookNarrationPreference?> GetAsync(CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return null;
        }

        var account = await accounts.FindAsync(userId, cancellationToken);
        return account?.AudiobookNarrationPreference;
    }

    public async Task<AccountOperationResult> SetAsync(
        AudiobookNarrationPreference preference, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return AccountOperationResult.Failure("You must be signed in.");
        }

        if (!Enum.IsDefined(preference))
        {
            return AccountOperationResult.Failure("That is not an audiobook narration preference.");
        }

        var result = await accounts.SetAudiobookNarrationPreferenceAsync(userId, preference, cancellationToken);
        if (result.Succeeded)
        {
            await audit.WriteAsync(
                AuditActions.AccountAudiobookNarrationPreferenceChanged,
                AuditSubjectTypes.Account,
                userId.ToString(),
                new { UserId = userId, Preference = preference.ToString() },
                cancellationToken);
        }

        return result;
    }
}
