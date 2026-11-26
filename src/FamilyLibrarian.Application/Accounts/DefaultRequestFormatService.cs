using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Domain.Accounts;
using FamilyLibrarian.Domain.Audit;

namespace FamilyLibrarian.Application.Accounts;

public interface IDefaultRequestFormatStore
{
    /// <summary><see langword="null"/> when the account no longer exists.</summary>
    Task<DefaultRequestFormat?> GetAsync(Guid userId, CancellationToken cancellationToken);

    Task<AccountOperationResult> SetAsync(Guid userId, DefaultRequestFormat format, CancellationToken cancellationToken);
}

/// <summary>A household member's own default request format -- self-service, never another account's.</summary>
public sealed class DefaultRequestFormatService(
    IDefaultRequestFormatStore store,
    ICurrentUser currentUser,
    IAuditWriter audit)
{
    public async Task<DefaultRequestFormat?> GetAsync(CancellationToken cancellationToken) =>
        currentUser.UserId is { } userId ? await store.GetAsync(userId, cancellationToken) : null;

    public async Task<AccountOperationResult> SetAsync(DefaultRequestFormat format, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return AccountOperationResult.Failure("You must be signed in.");
        }

        if (!Enum.IsDefined(format))
        {
            return AccountOperationResult.Failure("That is not a request format.");
        }

        var result = await store.SetAsync(userId, format, cancellationToken);
        if (result.Succeeded)
        {
            await audit.WriteAsync(
                AuditActions.AccountDefaultRequestFormatChanged,
                AuditSubjectTypes.Account,
                userId.ToString(),
                new { UserId = userId, Format = format.ToString() },
                cancellationToken);
        }

        return result;
    }
}
