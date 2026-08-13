namespace FamilyLibrarian.Domain.Accounts;

/// <summary>
/// A single-use, time-limited invitation for one email address to create one account.
/// </summary>
/// <remarks>
/// The invitation <em>is</em> the approval: redeeming a valid one produces an
/// <see cref="UserStatus.Active"/> account with no further administrator step.
/// That is only sound because the token is unguessable and the invitation names
/// the address it will create, so a redeemer cannot choose a different identity
/// than the one the administrator authorised.
/// <para>
/// Only <see cref="TokenHash"/> is stored. Reading this table must not yield a
/// usable invitation, for the same reason it must not yield a password.
/// </para>
/// </remarks>
public sealed class Invitation
{
    public const int MaxEmailLength = 256;

    private Invitation()
    {
    }

    public Invitation(
        string email,
        string tokenHash,
        string role,
        Guid invitedByUserId,
        DateTimeOffset createdAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        Email = RequireEmail(email);
        NormalizedEmail = NormalizeEmail(email);
        TokenHash = RequireText(tokenHash, nameof(tokenHash));
        Role = RequireText(role, nameof(role));

        if (invitedByUserId == Guid.Empty)
        {
            throw new ArgumentException("An inviting user is required.", nameof(invitedByUserId));
        }

        if (expiresAtUtc <= createdAtUtc)
        {
            throw new ArgumentException(
                "An invitation must expire after it is created.",
                nameof(expiresAtUtc));
        }

        InvitedByUserId = invitedByUserId;
        CreatedAtUtc = createdAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    /// <summary>
    /// The address this invitation will create, as the administrator typed it.
    /// </summary>
    /// <remarks>
    /// This is the display form and the value the redeemed account is created
    /// with, so a family member's address reads the way they wrote it rather
    /// than being shouted back at them in upper case.
    /// </remarks>
    public string Email { get; private set; } = null!;

    /// <summary>Upper-cased form, used only for matching.</summary>
    public string NormalizedEmail { get; private set; } = null!;

    /// <summary>A hash of the issued token. The token itself is never stored.</summary>
    public string TokenHash { get; private set; } = null!;

    /// <summary>The role the redeemed account receives in addition to <c>User</c>.</summary>
    public string Role { get; private set; } = null!;

    public Guid InvitedByUserId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset ExpiresAtUtc { get; private set; }

    public DateTimeOffset? RedeemedAtUtc { get; private set; }

    public Guid? RedeemedByUserId { get; private set; }

    public DateTimeOffset? RevokedAtUtc { get; private set; }

    public Guid? RevokedByUserId { get; private set; }

    public uint Version { get; private set; }

    public bool IsRedeemed => RedeemedAtUtc is not null;

    public bool IsRevoked => RevokedAtUtc is not null;

    public bool IsExpiredAt(DateTimeOffset atUtc) => atUtc >= ExpiresAtUtc;

    /// <summary>
    /// Whether this invitation can still be redeemed at the given moment.
    /// </summary>
    public bool CanBeRedeemedAt(DateTimeOffset atUtc) =>
        !IsRedeemed && !IsRevoked && !IsExpiredAt(atUtc);

    /// <summary>Marks the invitation used. A second call is refused.</summary>
    /// <exception cref="InvitationNotRedeemableException">
    /// The invitation was already used, revoked, or has expired.
    /// </exception>
    public void Redeem(Guid redeemedByUserId, DateTimeOffset atUtc)
    {
        if (redeemedByUserId == Guid.Empty)
        {
            throw new ArgumentException("A redeeming user is required.", nameof(redeemedByUserId));
        }

        if (!CanBeRedeemedAt(atUtc))
        {
            throw new InvitationNotRedeemableException(DescribeState(atUtc));
        }

        RedeemedAtUtc = atUtc;
        RedeemedByUserId = redeemedByUserId;
    }

    /// <summary>
    /// Withdraws an unredeemed invitation. Revoking an already-revoked one is a
    /// no-op so a double click cannot fail; revoking a redeemed one is refused,
    /// because the account it created already exists and disabling that account
    /// is the operation the administrator actually wants.
    /// </summary>
    public void Revoke(Guid revokedByUserId, DateTimeOffset atUtc)
    {
        if (IsRedeemed)
        {
            throw new InvitationNotRedeemableException("it has already been redeemed");
        }

        if (IsRevoked)
        {
            return;
        }

        RevokedAtUtc = atUtc;
        RevokedByUserId = revokedByUserId;
    }

    /// <summary>Why the invitation cannot be redeemed, in plain language.</summary>
    public string DescribeState(DateTimeOffset atUtc)
    {
        if (IsRedeemed)
        {
            return "it has already been used";
        }

        if (IsRevoked)
        {
            return "it was withdrawn";
        }

        return IsExpiredAt(atUtc) ? "it has expired" : "it is valid";
    }

    public static string NormalizeEmail(string email) => RequireEmail(email).ToUpperInvariant();

    private static string RequireEmail(string email)
    {
        var trimmed = RequireText(email, nameof(email));
        if (trimmed.Length > MaxEmailLength)
        {
            throw new ArgumentException(
                $"An email address may not exceed {MaxEmailLength} characters.",
                nameof(email));
        }

        return trimmed;
    }

    private static string RequireText(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }
}

public sealed class InvitationNotRedeemableException(string reason)
    : InvalidOperationException($"The invitation cannot be used because {reason}.")
{
    public string Reason { get; } = reason;
}
