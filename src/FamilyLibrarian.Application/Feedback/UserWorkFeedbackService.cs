using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Domain.Feedback;

namespace FamilyLibrarian.Application.Feedback;

/// <summary>
/// The commands and queries behind My Reading and the completion/rating action
/// on Work detail.
/// </summary>
/// <remarks>
/// Ownership is enforced here, not at the endpoint: every method resolves the
/// caller from <see cref="ICurrentUser"/>, and a row that belongs to someone
/// else is reported as not found rather than forbidden, so the API never
/// confirms that another family member's feedback exists.
/// <para>
/// Deliberately unaudited: unlike request-status changes or account changes,
/// nobody but the owner ever sees this, and there is no administrative action
/// it could inform. See <c>AuditActions</c> for the events that do warrant a
/// secret-free admin trail.
/// </para>
/// </remarks>
public sealed class UserWorkFeedbackService(
    IUserWorkFeedbackRepository repository,
    ICurrentUser currentUser,
    IClock clock)
{
    public async Task<IReadOnlyList<UserWorkFeedbackView>> ListMineAsync(
        CancellationToken cancellationToken) =>
        currentUser.UserId is { } userId
            ? await repository.ListForUserAsync(userId, cancellationToken)
            : [];

    public async Task<UserWorkFeedbackView?> FindMineAsync(
        Guid workId,
        CancellationToken cancellationToken) =>
        currentUser.UserId is { } userId
            ? await repository.FindViewAsync(userId, workId, cancellationToken)
            : null;

    /// <summary>
    /// Records a new completion/rating, or corrects the caller's existing one
    /// for the same Work.
    /// </summary>
    /// <param name="expectedVersion">
    /// <see langword="null"/> when creating; the row's current <c>Version</c>
    /// when correcting, read from a prior <see cref="FindMineAsync"/>. A stale
    /// or missing value where one was required answers <see cref="SetFeedbackOutcome.Conflict"/>
    /// rather than silently overwriting a change the caller has not seen.
    /// </param>
    public async Task<SetFeedbackResult> SetFeedbackAsync(
        Guid workId,
        DateOnly completedOn,
        int rating,
        uint? expectedVersion,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return SetFeedbackResult.Unauthenticated();
        }

        if (rating is < UserWorkFeedback.MinRating or > UserWorkFeedback.MaxRating)
        {
            return SetFeedbackResult.Invalid(
                $"Choose a rating between {UserWorkFeedback.MinRating} and {UserWorkFeedback.MaxRating} stars.");
        }

        if (!await repository.WorkExistsAsync(workId, cancellationToken))
        {
            return SetFeedbackResult.WorkNotFound();
        }

        var existing = await repository.FindOwnedAsync(userId, workId, cancellationToken);

        if (existing is null)
        {
            if (expectedVersion is not null)
            {
                // The client's expectedVersion came from a row that is gone now
                // (deleted from another tab, say) - not a blind create.
                return SetFeedbackResult.Conflict();
            }

            var created = new UserWorkFeedback(userId, workId, completedOn, rating, clock.UtcNow);
            repository.Add(created);
        }
        else
        {
            if (existing.Version != expectedVersion)
            {
                return SetFeedbackResult.Conflict();
            }

            existing.Correct(completedOn, rating, clock.UtcNow);
        }

        await repository.SaveChangesAsync(cancellationToken);

        var view = await repository.FindViewAsync(userId, workId, cancellationToken);
        return SetFeedbackResult.Success(view!);
    }

    public async Task<RemoveFeedbackResult> RemoveFeedbackAsync(
        Guid workId,
        uint expectedVersion,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return RemoveFeedbackResult.Unauthenticated();
        }

        var existing = await repository.FindOwnedAsync(userId, workId, cancellationToken);
        if (existing is null)
        {
            return RemoveFeedbackResult.NotFound();
        }

        if (existing.Version != expectedVersion)
        {
            return RemoveFeedbackResult.Conflict();
        }

        repository.Remove(existing);
        await repository.SaveChangesAsync(cancellationToken);

        return RemoveFeedbackResult.Success();
    }
}

public sealed record SetFeedbackResult(SetFeedbackOutcome Outcome, UserWorkFeedbackView? Feedback, string? Error)
{
    public static SetFeedbackResult Success(UserWorkFeedbackView feedback) =>
        new(SetFeedbackOutcome.Success, feedback, null);

    public static SetFeedbackResult WorkNotFound() =>
        new(SetFeedbackOutcome.WorkNotFound, null, null);

    public static SetFeedbackResult Conflict() =>
        new(SetFeedbackOutcome.Conflict, null, "This has changed since you loaded it. Reload and try again.");

    public static SetFeedbackResult Invalid(string error) =>
        new(SetFeedbackOutcome.Invalid, null, error);

    public static SetFeedbackResult Unauthenticated() =>
        new(SetFeedbackOutcome.Unauthenticated, null, null);
}

public enum SetFeedbackOutcome
{
    Success,
    WorkNotFound,
    Conflict,
    Invalid,
    Unauthenticated
}

public sealed record RemoveFeedbackResult(RemoveFeedbackOutcome Outcome, string? Error)
{
    public static RemoveFeedbackResult Success() => new(RemoveFeedbackOutcome.Success, null);

    public static RemoveFeedbackResult NotFound() => new(RemoveFeedbackOutcome.NotFound, null);

    public static RemoveFeedbackResult Conflict() =>
        new(RemoveFeedbackOutcome.Conflict, "This has changed since you loaded it. Reload and try again.");

    public static RemoveFeedbackResult Unauthenticated() => new(RemoveFeedbackOutcome.Unauthenticated, null);
}

public enum RemoveFeedbackOutcome
{
    Success,
    NotFound,
    Conflict,
    Unauthenticated
}
