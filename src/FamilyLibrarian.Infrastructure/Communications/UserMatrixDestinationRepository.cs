using FamilyLibrarian.Application.Communications;
using FamilyLibrarian.Domain.Communications;
using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.Communications;

public sealed class UserMatrixDestinationRepository(AppDbContext database)
    : IUserMatrixDestinationStore, IUserMatrixDestinationLookup
{
    public Task<UserMatrixDestination?> FindByUserIdAsync(Guid userId, CancellationToken cancellationToken) =>
        database.UserMatrixDestinations.FirstOrDefaultAsync(destination => destination.UserId == userId, cancellationToken);

    public Task<UserMatrixDestination?> FindByRoomIdAsync(string roomId, CancellationToken cancellationToken) =>
        database.UserMatrixDestinations.FirstOrDefaultAsync(destination => destination.RoomId == roomId, cancellationToken);

    public async Task<UserMatrixDestination> GetOrCreateForUserAsync(
        Guid userId, DateTimeOffset createdAtUtc, CancellationToken cancellationToken)
    {
        var existing = await database.UserMatrixDestinations
            .FirstOrDefaultAsync(destination => destination.UserId == userId, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var created = new UserMatrixDestination(userId, createdAtUtc);
        database.UserMatrixDestinations.Add(created);
        return created;
    }

    public async Task<string?> GetVerifiedRoomIdAsync(Guid userId, CancellationToken cancellationToken)
    {
        var destination = await database.UserMatrixDestinations
            .AsNoTracking()
            .FirstOrDefaultAsync(destination => destination.UserId == userId, cancellationToken);
        return destination is { IsVerified: true } ? destination.RoomId : null;
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) => database.SaveChangesAsync(cancellationToken);
}
