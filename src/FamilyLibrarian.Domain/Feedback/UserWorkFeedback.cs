namespace FamilyLibrarian.Domain.Feedback;

/// <summary>
/// One user's private record that they have read a Work, and when.
/// </summary>
/// <remarks>
/// Deliberately not a review: there is no title, no free text, no visibility to
/// anyone but the owner. There is also no native rating — FL never asks anyone
/// to type one in; a rating is only ever displayed when a connected metadata
/// provider supplies one for that Work. A user may hold at most one record per
/// Work; correcting it updates that row rather than growing a history, because
/// reading-history-over-time is an explicit, not-yet-made product decision, not
/// something to back into by accident.
/// <para>
/// Independent of <c>BookRequest</c>: a user can mark a Work read whether or
/// not they ever requested it through this app.
/// </para>
/// </remarks>
public sealed class UserWorkFeedback
{
    private UserWorkFeedback()
    {
    }

    public UserWorkFeedback(
        Guid userId,
        Guid workId,
        DateOnly completedOn,
        DateTimeOffset createdAtUtc)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A user ID is required.", nameof(userId));
        }

        if (workId == Guid.Empty)
        {
            throw new ArgumentException("A Work ID is required.", nameof(workId));
        }

        UserId = userId;
        WorkId = workId;
        CompletedOn = completedOn;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public Guid UserId { get; private set; }

    public Guid WorkId { get; private set; }

    public DateOnly CompletedOn { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public uint Version { get; private set; }

    public void Correct(DateOnly completedOn, DateTimeOffset atUtc)
    {
        CompletedOn = completedOn;
        UpdatedAtUtc = atUtc;
    }
}
