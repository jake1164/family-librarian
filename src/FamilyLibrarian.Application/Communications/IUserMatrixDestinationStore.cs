using FamilyLibrarian.Domain.Communications;

namespace FamilyLibrarian.Application.Communications;

/// <summary>
/// Persistence for <see cref="UserMatrixDestination"/> -- the identity-link
/// aggregate behind COMM-1 §C (linking) and §D (the inbound router resolving
/// an incoming DM's room back to an FL user).
/// </summary>
public interface IUserMatrixDestinationStore
{
    Task<UserMatrixDestination?> FindByUserIdAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>The inbound router's lookup: which FL user, if any, owns this DM room.</summary>
    Task<UserMatrixDestination?> FindByRoomIdAsync(string roomId, CancellationToken cancellationToken);

    Task<UserMatrixDestination> GetOrCreateForUserAsync(Guid userId, DateTimeOffset createdAtUtc, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
