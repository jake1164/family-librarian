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

    /// <summary>
    /// Whether this is the one bootstrap administrator <c>IdentityInitializer</c>
    /// creates from <c>Admin_Email</c>/<c>Admin_Password</c> at first boot.
    /// </summary>
    /// <remarks>
    /// The single account exempt from <c>OidcSettings.LocalLoginDisabled</c> —
    /// a break-glass path so disabling local sign-in in favor of OIDC can never
    /// lock every administrator out at once. Nothing else ever sets this true.
    /// </remarks>
    public bool IsBreakGlass { get; set; }
}
