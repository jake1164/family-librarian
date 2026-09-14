using FamilyLibrarian.Application.Following;
using FamilyLibrarian.Domain.Following;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.Persistence;

public sealed class FollowRepository(AppDbContext database) : IFollowRepository
{
    public Task<Follow?> FindAsync(
        Guid userId,
        FollowSubjectType subjectType,
        Guid subjectId,
        CancellationToken cancellationToken) =>
        database.Follows.SingleOrDefaultAsync(
            follow => follow.UserId == userId &&
                follow.SubjectType == subjectType &&
                follow.SubjectId == subjectId,
            cancellationToken);

    public async Task<IReadOnlyList<Follow>> ListForUserAsync(
        Guid userId,
        CancellationToken cancellationToken) =>
        await database.Follows
            .Where(follow => follow.UserId == userId)
            .ToArrayAsync(cancellationToken);

    public async Task<IReadOnlyList<Follow>> ListFollowersAsync(
        FollowSubjectType subjectType,
        Guid subjectId,
        CancellationToken cancellationToken) =>
        await database.Follows
            .Where(follow => follow.SubjectType == subjectType && follow.SubjectId == subjectId)
            .ToArrayAsync(cancellationToken);

    public void Add(Follow follow) => database.Follows.Add(follow);

    public void Remove(Follow follow) => database.Follows.Remove(follow);

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        database.SaveChangesAsync(cancellationToken);
}
