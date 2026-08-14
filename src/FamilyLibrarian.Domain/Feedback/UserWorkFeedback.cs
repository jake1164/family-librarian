namespace FamilyLibrarian.Domain.Feedback;

/// <summary>
/// One user's private completion record for one Work: a calendar date and a
/// 1-5 star rating.
/// </summary>
/// <remarks>
/// Deliberately not a review: there is no title, no free text, no visibility to
/// anyone but the owner and — indirectly — a future recommendation query. A
/// user may hold at most one feedback row per Work; correcting it updates that
/// row rather than growing a history, because reading-history-over-time is an
/// explicit, not-yet-made product decision (see the plan's M7 section), not
/// something to back into by accident.
/// <para>
/// Feedback is independent of <c>BookRequest</c>: a user can mark a Work
/// complete whether or not they ever requested it through this app.
/// </para>
/// </remarks>
public sealed class UserWorkFeedback
{
    public const int MinRating = 1;
    public const int MaxRating = 5;

    private UserWorkFeedback()
    {
    }

    public UserWorkFeedback(
        Guid userId,
        Guid workId,
        DateOnly completedOn,
        int rating,
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
        Rating = RequireValidRating(rating);
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public Guid UserId { get; private set; }

    public Guid WorkId { get; private set; }

    public DateOnly CompletedOn { get; private set; }

    public int Rating { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public uint Version { get; private set; }

    public void Correct(DateOnly completedOn, int rating, DateTimeOffset atUtc)
    {
        Rating = RequireValidRating(rating);
        CompletedOn = completedOn;
        UpdatedAtUtc = atUtc;
    }

    private static int RequireValidRating(int rating)
    {
        if (rating is < MinRating or > MaxRating)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rating),
                rating,
                $"A rating must be between {MinRating} and {MaxRating}.");
        }

        return rating;
    }
}
