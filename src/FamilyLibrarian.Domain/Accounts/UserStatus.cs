namespace FamilyLibrarian.Domain.Accounts;

/// <summary>
/// Whether an account may be used, and how it came to exist.
/// </summary>
/// <remarks>
/// Account provisioning is one operation with several trust anchors: an
/// administrator's invitation, an identity provider's assertion, or nobody at
/// all. This status is where those paths converge, which is why
/// <see cref="PendingApproval"/> exists before anything produces it — OIDC
/// just-in-time provisioning without an allowlist, and local self-registration
/// if it is ever enabled, both land there rather than growing a parallel user
/// system.
/// </remarks>
public enum UserStatus
{
    /// <summary>Invited but not yet redeemed. Cannot sign in.</summary>
    Invited = 1,

    /// <summary>Usable.</summary>
    Active = 2,

    /// <summary>Provisioned by an untrusted path and awaiting an administrator.</summary>
    PendingApproval = 3,

    /// <summary>Retained for its request history, but refused at sign-in.</summary>
    Disabled = 4
}

public static class UserStatuses
{
    /// <summary>
    /// Whether an account in this status may hold a session. Everything else is
    /// refused at sign-in and has its existing sessions invalidated.
    /// </summary>
    public static bool CanSignIn(UserStatus status) => status is UserStatus.Active;
}
