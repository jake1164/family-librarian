using FamilyLibrarian.Domain.Accounts;

namespace FamilyLibrarian.Application.Accounts;

/// <summary>
/// The account operations the admin surface needs, without exposing ASP.NET
/// Identity to the application layer.
/// </summary>
/// <remarks>
/// Identity owns credentials, hashing, and the security stamp; this boundary
/// exists so the rules about <em>who may do what to whom</em> can live in
/// <see cref="AccountAdminService"/> and be tested without a database.
/// </remarks>
public interface IUserAccountStore
{
    Task<IReadOnlyList<UserAccount>> ListAsync(CancellationToken cancellationToken);

    Task<UserAccount?> FindAsync(Guid userId, CancellationToken cancellationToken);

    Task<UserAccount?> FindByEmailAsync(string email, CancellationToken cancellationToken);

    Task<int> CountAdminsAsync(CancellationToken cancellationToken);

    /// <summary>Creates an account with the given password and roles.</summary>
    Task<AccountOperationResult> CreateAsync(
        string email,
        string displayName,
        string password,
        UserStatus status,
        bool isAdmin,
        CancellationToken cancellationToken);

    /// <summary>
    /// Changes an account's status.
    /// </summary>
    /// <remarks>
    /// Implementations must also rotate the Identity security stamp whenever the
    /// account can no longer sign in. Refusing a disabled account at the login
    /// endpoint alone would leave an already-issued cookie working until it
    /// expired.
    /// </remarks>
    Task<AccountOperationResult> SetStatusAsync(
        Guid userId,
        UserStatus status,
        CancellationToken cancellationToken);

    Task<AccountOperationResult> SetPasswordAsync(
        Guid userId,
        string password,
        CancellationToken cancellationToken);

    Task<AccountOperationResult> SetAdminAsync(
        Guid userId,
        bool isAdmin,
        CancellationToken cancellationToken);

    /// <summary>
    /// Sets the account's own audiobook narration preference (self-service --
    /// see <c>AudiobookNarrationPreferenceService</c>). Unlike the other
    /// setters here, this is not an administrator operation.
    /// </summary>
    Task<AccountOperationResult> SetAudiobookNarrationPreferenceAsync(
        Guid userId,
        AudiobookNarrationPreference preference,
        CancellationToken cancellationToken);

    /// <summary>
    /// Every account that can currently sign in and holds the <c>Admin</c> role —
    /// HUMAN-ACQ-1's Matrix alert fan-out list, before filtering to a verified
    /// Matrix link.
    /// </summary>
    Task<IReadOnlyList<AdminAccountSummary>> ListActiveAdminsAsync(CancellationToken cancellationToken);

    /// <summary>This account's own quiet-hours setting (HUMAN-ACQ-1 D9), or <see langword="null"/> if unset.</summary>
    Task<QuietHours?> GetQuietHoursAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Sets or clears (<paramref name="quietHours"/> <see langword="null"/>) the account's quiet-hours setting.</summary>
    Task<AccountOperationResult> SetQuietHoursAsync(
        Guid userId,
        QuietHours? quietHours,
        CancellationToken cancellationToken);
}

/// <summary>An administrator, as HUMAN-ACQ-1's alert fan-out needs it — never a credential or role list.</summary>
public sealed record AdminAccountSummary(Guid Id, string DisplayName);

/// <summary>An account as the admin surface displays it. Never carries a credential.</summary>
public sealed record UserAccount(
    Guid Id,
    string Email,
    string DisplayName,
    UserStatus Status,
    bool IsAdmin,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastLoginAtUtc,
    AudiobookNarrationPreference AudiobookNarrationPreference = AudiobookNarrationPreference.PreferHuman);

public sealed record AccountOperationResult(bool Succeeded, string? Error, Guid UserId)
{
    public static AccountOperationResult Success(Guid userId = default) => new(true, null, userId);

    public static AccountOperationResult Failure(string error) => new(false, error, default);
}
