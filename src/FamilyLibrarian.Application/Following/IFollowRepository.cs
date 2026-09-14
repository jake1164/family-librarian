using FamilyLibrarian.Domain.Following;

namespace FamilyLibrarian.Application.Following;

/// <summary>The persistence boundary for series/author subscriptions.</summary>
public interface IFollowRepository
{
    Task<Follow?> FindAsync(
        Guid userId,
        FollowSubjectType subjectType,
        Guid subjectId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<Follow>> ListForUserAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Every user following a subject — the release-monitoring
    /// notification fan-out reads this.</summary>
    Task<IReadOnlyList<Follow>> ListFollowersAsync(
        FollowSubjectType subjectType,
        Guid subjectId,
        CancellationToken cancellationToken);

    void Add(Follow follow);

    void Remove(Follow follow);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
