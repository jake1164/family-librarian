using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Domain;
using FamilyLibrarian.Domain.Accounts;
using FamilyLibrarian.Domain.Audit;

namespace FamilyLibrarian.Application.Accounts;

/// <summary>
/// Decides what a successfully-authenticated external identity means for a
/// Family Librarian account: link, create, or refuse. Pure decision logic —
/// no ASP.NET Core types — so it is testable with fakes and deterministic
/// identities, per the plan's own requirement that ordinary tests never need
/// a live identity provider.
/// </summary>
public sealed class ExternalSignInService(
    IExternalLoginStore externalLogins,
    IUserAccountStore accounts,
    AccountAdminService accountAdmin,
    IInvitationRepository invitations,
    IAuditWriter audit,
    IClock clock)
{
    public async Task<ExternalSignInResult> SignInAsync(
        ExternalIdentity identity, bool autoCreateAccounts, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(identity.Issuer) || string.IsNullOrWhiteSpace(identity.Subject))
        {
            return ExternalSignInResult.Rejected("The identity provider did not return a usable identity.");
        }

        var linkedUserId = await externalLogins.FindLinkedUserIdAsync(
            identity.Issuer, identity.Subject, cancellationToken);
        if (linkedUserId is { } existingId)
        {
            await SyncAdminRoleAsync(existingId, identity.IsAdminClaimMatched, cancellationToken);
            return await ResolveAsync(existingId, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(identity.Email))
        {
            return ExternalSignInResult.Rejected(
                "The identity provider did not supply the configured matching claim.");
        }

        var existingAccount = await accounts.FindByEmailAsync(identity.Email, cancellationToken);
        if (existingAccount is not null)
        {
            await externalLogins.LinkAsync(
                existingAccount.Id, identity.Issuer, identity.Subject, identity.DisplayName, cancellationToken);
            await audit.WriteAsync(
                AuditActions.ExternalLoginLinked, AuditSubjectTypes.Account, existingAccount.Id.ToString(),
                new { existingAccount.Id }, cancellationToken);

            await SyncAdminRoleAsync(existingAccount.Id, identity.IsAdminClaimMatched, cancellationToken);
            return await ResolveAsync(existingAccount.Id, cancellationToken);
        }

        var now = clock.UtcNow;
        var normalizedEmail = Invitation.NormalizeEmail(identity.Email);
        var outstanding = await invitations.FindOutstandingForEmailAsync(normalizedEmail, now, cancellationToken);
        var invitation = outstanding.FirstOrDefault(candidate => candidate.CanBeRedeemedAt(now));

        if (invitation is not null)
        {
            var invitedUserId = await externalLogins.CreatePasswordlessAsync(
                identity.Email,
                identity.DisplayName ?? identity.Email,
                UserStatus.Active,
                invitation.Role == RoleNames.Admin || identity.IsAdminClaimMatched,
                identity.Issuer,
                identity.Subject,
                cancellationToken);

            invitation.Redeem(invitedUserId, now);
            await invitations.SaveChangesAsync(cancellationToken);

            await audit.WriteAsync(
                AuditActions.InvitationRedeemed, AuditSubjectTypes.Invitation, invitation.Id.ToString(),
                new { invitation.Id, UserId = invitedUserId }, cancellationToken);

            return ExternalSignInResult.SignedIn(invitedUserId);
        }

        var status = autoCreateAccounts ? UserStatus.Active : UserStatus.PendingApproval;
        var newUserId = await externalLogins.CreatePasswordlessAsync(
            identity.Email,
            identity.DisplayName ?? identity.Email,
            status,
            identity.IsAdminClaimMatched,
            identity.Issuer,
            identity.Subject,
            cancellationToken);

        await audit.WriteAsync(
            AuditActions.ExternalAccountCreated, AuditSubjectTypes.Account, newUserId.ToString(),
            new { UserId = newUserId, Status = status.ToString() }, cancellationToken);

        return status == UserStatus.Active
            ? ExternalSignInResult.SignedIn(newUserId)
            : ExternalSignInResult.NotActive(
                "Your account was created and is awaiting administrator approval.");
    }

    private async Task SyncAdminRoleAsync(Guid userId, bool shouldBeAdmin, CancellationToken cancellationToken)
    {
        var account = await accounts.FindAsync(userId, cancellationToken);
        if (account is null || account.IsAdmin == shouldBeAdmin)
        {
            return;
        }

        // Best-effort: a failure here (e.g. "cannot remove the last administrator")
        // must not block sign-in itself — the account still signs in with
        // whatever role it already had.
        await accountAdmin.SetAdminAsync(userId, shouldBeAdmin, cancellationToken);
    }

    private async Task<ExternalSignInResult> ResolveAsync(Guid userId, CancellationToken cancellationToken)
    {
        var account = await accounts.FindAsync(userId, cancellationToken);
        if (account is null)
        {
            return ExternalSignInResult.Rejected("That account no longer exists.");
        }

        return UserStatuses.CanSignIn(account.Status)
            ? ExternalSignInResult.SignedIn(userId)
            : ExternalSignInResult.NotActive(DescribeUnusable(account.Status));
    }

    private static string DescribeUnusable(UserStatus status) => status switch
    {
        UserStatus.PendingApproval => "Your account is awaiting administrator approval.",
        UserStatus.Invited => "Your invitation has not been redeemed yet.",
        UserStatus.Disabled => "This account has been disabled.",
        _ => "This account cannot sign in right now."
    };
}

/// <summary>
/// The claims a successful external sign-in produced, already reduced to the
/// facts this service needs — never a raw <c>ClaimsPrincipal</c>, so this type
/// (and this service) carries no ASP.NET Core dependency.
/// </summary>
public sealed record ExternalIdentity(
    string Issuer, string Subject, string? Email, string? DisplayName, bool IsAdminClaimMatched);

public sealed record ExternalSignInResult(ExternalSignInOutcome Outcome, Guid? UserId, string? Message)
{
    public static ExternalSignInResult SignedIn(Guid userId) => new(ExternalSignInOutcome.SignedIn, userId, null);

    public static ExternalSignInResult NotActive(string message) =>
        new(ExternalSignInOutcome.NotActive, null, message);

    public static ExternalSignInResult Rejected(string message) =>
        new(ExternalSignInOutcome.Rejected, null, message);
}

public enum ExternalSignInOutcome
{
    SignedIn,
    NotActive,
    Rejected
}
