namespace FamilyLibrarian.Application.Communications;

/// <summary>
/// Resolves a user's verified Matrix DM room for the Matrix provider. Kept
/// separate from <see cref="Domain.Communications.OutboundCommunication"/> so
/// the normalized message never carries a provider-specific destination.
/// Returns <see langword="null"/> for a user with no destination, or one that
/// has not completed the verification-code exchange yet (COMM-1 §C) --
/// an unverified link is never a valid send target.
/// </summary>
public interface IUserMatrixDestinationLookup
{
    Task<string?> GetVerifiedRoomIdAsync(Guid userId, CancellationToken cancellationToken);
}
