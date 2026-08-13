using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Domain;
using FamilyLibrarian.Domain.Accounts;
using FamilyLibrarian.Domain.Audit;

namespace FamilyLibrarian.Application.Accounts;

/// <summary>
/// Issues, withdraws, and redeems invitations to join the family library.
/// </summary>
/// <remarks>
/// The issued token is returned exactly once, from
/// <see cref="CreateAsync"/>. Nothing else on this type or the API behind it can
/// read it back, because only its hash is stored — so an administrator who loses
/// the link revokes the invitation and issues another.
/// </remarks>
public sealed class InvitationService(
    IInvitationRepository invitations,
    IUserAccountStore accounts,
    IInvitationTokenGenerator tokens,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock,
    InvitationPolicy policy)
{
    public const int MinimumPasswordLength = 12;

    /// <summary>How long a new invitation stays usable.</summary>
    public TimeSpan Lifetime => policy.Lifetime;

    public Task<IReadOnlyList<Invitation>> ListAsync(CancellationToken cancellationToken) =>
        invitations.ListAsync(cancellationToken);

    public async Task<CreateInvitationResult> CreateAsync(
        string email,
        bool asAdmin,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } invitedBy)
        {
            return CreateInvitationResult.Failure("You must be signed in to invite someone.");
        }

        if (string.IsNullOrWhiteSpace(email) || email.Length > Invitation.MaxEmailLength)
        {
            return CreateInvitationResult.Failure("Enter the email address to invite.");
        }

        var trimmed = email.Trim();
        var normalized = Invitation.NormalizeEmail(trimmed);

        if (await accounts.FindByEmailAsync(trimmed, cancellationToken) is not null)
        {
            return CreateInvitationResult.Failure("That address already has an account.");
        }

        var now = clock.UtcNow;
        var outstanding = await invitations.FindOutstandingForEmailAsync(
            normalized,
            now,
            cancellationToken);

        // Reissuing would leave two live tokens for one address, so the previous
        // one is withdrawn rather than left usable. The administrator asked for a
        // fresh link, which implies the old one is gone or was never delivered.
        foreach (var previous in outstanding)
        {
            previous.Revoke(invitedBy, now);
        }

        var token = tokens.CreateToken();
        var invitation = new Invitation(
            trimmed,
            tokens.Hash(token),
            asAdmin ? RoleNames.Admin : RoleNames.User,
            invitedBy,
            now,
            now.Add(policy.Lifetime));

        invitations.Add(invitation);
        await invitations.SaveChangesAsync(cancellationToken);

        // The audit records who was invited and by whom, never the token.
        await audit.WriteAsync(
            AuditActions.InvitationCreated,
            AuditSubjectTypes.Invitation,
            invitation.Id.ToString(),
            new { invitation.Id, Email = trimmed, invitation.Role, Replaced = outstanding.Count },
            cancellationToken);

        return CreateInvitationResult.Success(invitation, token);
    }

    /// <summary>
    /// Issues a replacement link for an existing invitation.
    /// </summary>
    /// <remarks>
    /// The original token cannot be shown again — only its hash is kept — so the
    /// normal reason to return to an invitation is that the link was lost or has
    /// expired. This reuses <see cref="CreateAsync"/>, which withdraws the
    /// previous outstanding invitation as part of issuing the new one, so a
    /// mislaid link stops working the moment its replacement exists.
    /// </remarks>
    public async Task<CreateInvitationResult> RegenerateAsync(
        Guid invitationId,
        CancellationToken cancellationToken)
    {
        var existing = await invitations.FindAsync(invitationId, cancellationToken);
        if (existing is null)
        {
            return CreateInvitationResult.Failure("That invitation no longer exists.");
        }

        // A redeemed invitation already produced an account. Reissuing would be
        // refused downstream anyway ("that address already has an account"), but
        // saying so here names the operation the administrator actually wants.
        if (existing.IsRedeemed)
        {
            return CreateInvitationResult.Failure(
                "That invitation was already used. Reset the account's password instead.");
        }

        return await CreateAsync(
            existing.Email,
            existing.Role == RoleNames.Admin,
            cancellationToken);
    }

    public async Task<InvitationCommandResult> RevokeAsync(
        Guid invitationId,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } revokedBy)
        {
            return InvitationCommandResult.Failure("You must be signed in.");
        }

        var invitation = await invitations.FindAsync(invitationId, cancellationToken);
        if (invitation is null)
        {
            return InvitationCommandResult.NotFound();
        }

        try
        {
            invitation.Revoke(revokedBy, clock.UtcNow);
        }
        catch (InvitationNotRedeemableException exception)
        {
            return InvitationCommandResult.Failure(exception.Message);
        }

        await invitations.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(
            AuditActions.InvitationRevoked,
            AuditSubjectTypes.Invitation,
            invitation.Id.ToString(),
            new { invitation.Id },
            cancellationToken);

        return InvitationCommandResult.Success();
    }

    /// <summary>
    /// Reports whether a presented token can still be redeemed, so the redemption
    /// page can show the address being claimed before asking for a password.
    /// </summary>
    public async Task<InvitationPreview?> PreviewAsync(
        string token,
        CancellationToken cancellationToken)
    {
        var invitation = await FindByTokenAsync(token, cancellationToken);
        if (invitation is null)
        {
            return null;
        }

        var now = clock.UtcNow;
        return new InvitationPreview(
            invitation.Email,
            invitation.CanBeRedeemedAt(now),
            invitation.DescribeState(now));
    }

    /// <summary>
    /// Creates the account the invitation authorised.
    /// </summary>
    /// <remarks>
    /// The account is created <see cref="UserStatus.Active"/> with no further
    /// approval: an administrator issuing the invitation is the approval. The
    /// address comes from the invitation rather than from the caller, so a
    /// redeemer cannot claim an identity that was not invited.
    /// </remarks>
    public async Task<RedeemInvitationResult> RedeemAsync(
        string token,
        string displayName,
        string password,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < MinimumPasswordLength)
        {
            return RedeemInvitationResult.Failure(
                $"Choose a password of at least {MinimumPasswordLength} characters.");
        }

        if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 256)
        {
            return RedeemInvitationResult.Failure("Enter the name to show on your requests.");
        }

        return await invitations.InTransactionAsync(
            async innerToken =>
            {
                var invitation = await FindByTokenAsync(token, innerToken);
                var now = clock.UtcNow;

                // One message for "no such token" and "token no longer usable":
                // distinguishing them would tell someone probing tokens that they
                // had found a real one.
                if (invitation is null || !invitation.CanBeRedeemedAt(now))
                {
                    return RedeemInvitationResult.Invalid();
                }

                var created = await accounts.CreateAsync(
                    invitation.Email,
                    displayName.Trim(),
                    password,
                    UserStatus.Active,
                    invitation.Role == RoleNames.Admin,
                    innerToken);

                if (!created.Succeeded)
                {
                    return RedeemInvitationResult.Failure(
                        created.Error ?? "That account could not be created.");
                }

                invitation.Redeem(created.UserId, now);
                await invitations.SaveChangesAsync(innerToken);

                await audit.WriteAsync(
                    AuditActions.InvitationRedeemed,
                    AuditSubjectTypes.Invitation,
                    invitation.Id.ToString(),
                    new { invitation.Id, created.UserId },
                    innerToken);

                return RedeemInvitationResult.Success(invitation.Email);
            },
            cancellationToken);
    }

    private async Task<Invitation?> FindByTokenAsync(
        string token,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 512)
        {
            return null;
        }

        return await invitations.FindByTokenHashAsync(tokens.Hash(token.Trim()), cancellationToken);
    }
}

public sealed record InvitationPreview(string Email, bool CanBeRedeemed, string State);

public sealed record CreateInvitationResult(
    bool Succeeded,
    Invitation? Invitation,
    string? Token,
    string? Error)
{
    public static CreateInvitationResult Success(Invitation invitation, string token) =>
        new(true, invitation, token, null);

    public static CreateInvitationResult Failure(string error) =>
        new(false, null, null, error);
}

public sealed record InvitationCommandResult(
    InvitationCommandOutcome Outcome,
    string? Error)
{
    public static InvitationCommandResult Success() =>
        new(InvitationCommandOutcome.Success, null);

    public static InvitationCommandResult NotFound() =>
        new(InvitationCommandOutcome.NotFound, null);

    public static InvitationCommandResult Failure(string error) =>
        new(InvitationCommandOutcome.Invalid, error);
}

public enum InvitationCommandOutcome
{
    Success,
    NotFound,
    Invalid
}

public sealed record RedeemInvitationResult(
    RedeemInvitationOutcome Outcome,
    string? Email,
    string? Error)
{
    public static RedeemInvitationResult Success(string email) =>
        new(RedeemInvitationOutcome.Success, email, null);

    public static RedeemInvitationResult Invalid() =>
        new(RedeemInvitationOutcome.InvalidInvitation, null, null);

    public static RedeemInvitationResult Failure(string error) =>
        new(RedeemInvitationOutcome.Rejected, null, error);
}

public enum RedeemInvitationOutcome
{
    Success,
    InvalidInvitation,
    Rejected
}
