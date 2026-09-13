namespace FamilyLibrarian.Domain.Following;

/// <summary>What a <see cref="Follow"/> subscribes to — a Series or an Author,
/// the same mechanism either way.</summary>
public enum FollowSubjectType
{
    Series,
    Author
}

/// <summary>
/// One user's subscription to being told about new entries in a catalog
/// <c>Series</c> or new works by a catalog <c>Author</c>.
/// </summary>
/// <remarks>
/// Deliberately immutable once created: there is nothing to correct about a
/// follow — you either follow a subject or you don't. Unfollowing removes the
/// row entirely rather than disabling it, the same as <c>UserWorkFeedback</c>
/// being removable, unlike <c>DeliveryTarget</c> which is only ever disabled.
/// </remarks>
public sealed class Follow
{
    private Follow()
    {
    }

    public Follow(
        Guid userId,
        FollowSubjectType subjectType,
        Guid subjectId,
        DateTimeOffset createdAtUtc)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A user ID is required.", nameof(userId));
        }

        if (subjectId == Guid.Empty)
        {
            throw new ArgumentException("A subject ID is required.", nameof(subjectId));
        }

        UserId = userId;
        SubjectType = subjectType;
        SubjectId = subjectId;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public Guid UserId { get; private set; }

    public FollowSubjectType SubjectType { get; private set; }

    public Guid SubjectId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public uint Version { get; private set; }
}
