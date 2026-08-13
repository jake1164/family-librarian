using FamilyLibrarian.Domain.Accounts;
using Microsoft.AspNetCore.Identity;

namespace FamilyLibrarian.Infrastructure.Identity;

public sealed class AppUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Whether this account may be used, and how it came to exist.
    /// </summary>
    /// <remarks>
    /// Checked at sign-in. Changing it away from <see cref="UserStatus.Active"/>
    /// must be paired with a security-stamp rotation, or an already-issued cookie
    /// keeps working until it expires; see <c>IdentityUserAccountStore</c>.
    /// </remarks>
    public UserStatus Status { get; set; } = UserStatus.Active;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset? LastLoginAtUtc { get; set; }
}
