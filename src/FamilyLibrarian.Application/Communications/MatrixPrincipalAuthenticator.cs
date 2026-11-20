using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Accounts;
using FamilyLibrarian.Domain.Accounts;

namespace FamilyLibrarian.Application.Communications;

public enum MatrixAuthenticationOutcome
{
    Authenticated,

    /// <summary>The room is not a linked household member's direct room.</summary>
    UnknownRoom,

    /// <summary>The link exists but the verification-code exchange was never completed, or was removed.</summary>
    LinkNotVerified,

    /// <summary>The message was sent by someone other than the Matrix account verified for this link.</summary>
    SenderMismatch,

    /// <summary>The linked Family Librarian account no longer exists.</summary>
    AccountMissing,

    /// <summary>The linked account is invited, pending approval or disabled.</summary>
    AccountNotActive
}

public sealed record MatrixAuthenticationResult(MatrixAuthenticationOutcome Outcome, OperationPrincipal? Principal)
{
    public bool IsAuthenticated => Outcome == MatrixAuthenticationOutcome.Authenticated && Principal is not null;
}

/// <summary>
/// Turns one inbound Matrix message into an authenticated operation principal, or refuses (alpha.6 M2-1). Identity is
/// derived from stored state read now, never from the message: the room must belong to a verified link, the sender
/// must be the Matrix account verified on that link, and the linked Family Librarian account must still exist and be
/// able to sign in. Roles are read from the account on every call, so a downgrade or disable takes effect on the next
/// message with no cached privilege. On success the principal is placed in the current scope's holder, where
/// <see cref="ICurrentUser"/> and <see cref="ICurrentAdministrator"/> consumers find it.
/// </summary>
/// <remarks>
/// Call once per scope. A second attempt in the same scope fails rather than switching identity.
/// </remarks>
public sealed class MatrixPrincipalAuthenticator(
    IUserMatrixDestinationStore destinations,
    IUserAccountStore accounts,
    OperationPrincipalHolder holder)
{
    public async Task<MatrixAuthenticationResult> AuthenticateAsync(MatrixInboundMessage message, CancellationToken cancellationToken)
    {
        var (outcome, account) = await ResolveAsync(message, cancellationToken);
        if (outcome != MatrixAuthenticationOutcome.Authenticated || account is null) return Refused(outcome);

        var principal = new OperationPrincipal(account.Id, account.DisplayName, account.IsAdmin, "matrix");
        holder.Establish(principal);
        return new MatrixAuthenticationResult(MatrixAuthenticationOutcome.Authenticated, principal);
    }

    /// <summary>
    /// Whether the message's sender is still the verified Matrix account of an account that can still sign in, read now. It
    /// establishes nothing: it is the check made again after a long turn, before the reply is sent into the room, so a link that
    /// was removed or an account that was withdrawn while the answer was being prepared receives nothing.
    /// </summary>
    public async Task<bool> StillAuthorizedAsync(MatrixInboundMessage message, CancellationToken cancellationToken) =>
        (await ResolveAsync(message, cancellationToken)).Outcome == MatrixAuthenticationOutcome.Authenticated;

    private async Task<(MatrixAuthenticationOutcome Outcome, UserAccount? Account)> ResolveAsync(
        MatrixInboundMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (string.IsNullOrWhiteSpace(message.RoomId) || string.IsNullOrWhiteSpace(message.SenderUserId))
        {
            return (MatrixAuthenticationOutcome.UnknownRoom, null);
        }

        var link = await destinations.FindByRoomIdAsync(message.RoomId, cancellationToken);
        if (link is null)
        {
            return (MatrixAuthenticationOutcome.UnknownRoom, null);
        }

        if (!link.IsVerified || string.IsNullOrWhiteSpace(link.MatrixUserId))
        {
            return (MatrixAuthenticationOutcome.LinkNotVerified, null);
        }

        // A room can hold more than the linked member; only the verified Matrix account speaks as the FL user.
        if (!string.Equals(link.MatrixUserId, message.SenderUserId, StringComparison.Ordinal))
        {
            return (MatrixAuthenticationOutcome.SenderMismatch, null);
        }

        var account = await accounts.FindAsync(link.UserId, cancellationToken);
        if (account is null)
        {
            return (MatrixAuthenticationOutcome.AccountMissing, null);
        }

        return UserStatuses.CanSignIn(account.Status)
            ? (MatrixAuthenticationOutcome.Authenticated, account)
            : (MatrixAuthenticationOutcome.AccountNotActive, null);
    }

    private static MatrixAuthenticationResult Refused(MatrixAuthenticationOutcome outcome) => new(outcome, null);
}
