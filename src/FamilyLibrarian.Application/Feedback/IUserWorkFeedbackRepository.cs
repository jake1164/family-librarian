using FamilyLibrarian.Domain.Feedback;

namespace FamilyLibrarian.Application.Feedback;

/// <summary>The persistence boundary for private reading feedback.</summary>
public interface IUserWorkFeedbackRepository
{
    Task<bool> WorkExistsAsync(Guid workId, CancellationToken cancellationToken);

    /// <summary>
    /// The caller's feedback entity for a Work, for mutation. Returns
    /// <see langword="null"/> for another user's row, so a caller cannot
    /// distinguish "not yours" from "does not exist".
    /// </summary>
    Task<UserWorkFeedback?> FindOwnedAsync(
        Guid userId,
        Guid workId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<UserWorkFeedbackView>> ListForUserAsync(
        Guid userId,
        CancellationToken cancellationToken);

    Task<UserWorkFeedbackView?> FindViewAsync(
        Guid userId,
        Guid workId,
        CancellationToken cancellationToken);

    void Add(UserWorkFeedback feedback);

    void Remove(UserWorkFeedback feedback);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
