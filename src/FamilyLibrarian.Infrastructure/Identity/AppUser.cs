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

    /// <summary>
    /// This account's own tolerance for computer-generated audiobook
    /// narration (self-service, at <c>/settings/audiobook-narration</c>).
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="AudiobookNarrationPreference.PreferHuman"/> for
    /// every account, existing or new -- the migration that added this column
    /// backfills existing rows to the same default, so no account needs a
    /// manual repair after upgrade.
    /// </remarks>
    public AudiobookNarrationPreference AudiobookNarrationPreference { get; set; } = AudiobookNarrationPreference.PreferHuman;

    /// <summary>
    /// This account's own "do not disturb" window (HUMAN-ACQ-1 D9). All three quiet-hours
    /// fields are null together, meaning no quiet hours are set. Enforced only by the
    /// Matrix verification alert in this slice -- see <c>NOTIFY-QUIET-1</c> for extending
    /// it to every outbound notification.
    /// </summary>
    public string? QuietHoursTimeZoneId { get; set; }

    /// <summary>Minutes after local midnight (0-1439). Null iff quiet hours are unset.</summary>
    public int? QuietHoursStartMinute { get; set; }

    /// <summary>Minutes after local midnight (0-1439). Null iff quiet hours are unset.</summary>
    public int? QuietHoursEndMinute { get; set; }
}
