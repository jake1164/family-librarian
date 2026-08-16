using FamilyLibrarian.Domain.Accounts;

namespace FamilyLibrarian.Application.Accounts;

/// <summary>
/// The external-login-linking operations OIDC sign-in needs, kept out of
/// <see cref="IUserAccountStore"/> because they touch ASP.NET Identity's
/// separate external-login table rather than account administration.
/// </summary>
public interface IExternalLoginStore
{
    /// <summary>The id of the account already linked to this <c>(issuer, subject)</c> pair, if any.</summary>
    Task<Guid?> FindLinkedUserIdAsync(string issuer, string subject, CancellationToken cancellationToken);

    Task LinkAsync(
        Guid userId, string issuer, string subject, string? providerDisplayName, CancellationToken cancellationToken);

    /// <summary>
    /// Creates a passwordless account — sign-in is only ever possible through a
    /// linked external login, never a local password — and links it.
    /// </summary>
    Task<Guid> CreatePasswordlessAsync(
        string email,
        string displayName,
        UserStatus status,
        bool isAdmin,
        string issuer,
        string subject,
        CancellationToken cancellationToken);
}
