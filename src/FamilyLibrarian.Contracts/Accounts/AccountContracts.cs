namespace FamilyLibrarian.Contracts.Accounts;

public sealed record FamilyAccountResponse(
    Guid Id,
    string Email,
    string DisplayName,
    string Status,
    bool IsAdmin,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastLoginAtUtc);

public sealed record FamilyAccountListResponse(IReadOnlyList<FamilyAccountResponse> Accounts);

public sealed record SetAccountStatusRequest(string Status);

public sealed record SetAccountAdminRequest(bool IsAdmin);

public sealed record ResetAccountPasswordRequest(string Password);

public sealed record CreateInvitationRequest(string Email, bool AsAdmin);

/// <param name="Token">
/// The invitation token, returned only here and never readable afterwards. The
/// administrator delivers <paramref name="RedeemUrl"/> to the invitee.
/// </param>
public sealed record CreatedInvitationResponse(
    Guid Id,
    string Email,
    string Role,
    DateTimeOffset ExpiresAtUtc,
    string Token,
    string RedeemUrl);

public sealed record InvitationResponse(
    Guid Id,
    string Email,
    string Role,
    string State,
    bool IsOutstanding,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? RedeemedAtUtc,
    DateTimeOffset? RevokedAtUtc);

public sealed record InvitationListResponse(IReadOnlyList<InvitationResponse> Invitations);

/// <summary>What the redemption page may show before asking for a password.</summary>
public sealed record InvitationPreviewResponse(string Email, bool CanBeRedeemed, string State);

public sealed record RedeemInvitationRequest(
    string Token,
    string DisplayName,
    string Password);
