using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Application.Requests;

/// <summary>The persistence boundary for book requests.</summary>
public interface IRequestRepository
{
    Task<bool> WorkExistsAsync(Guid workId, CancellationToken cancellationToken);

    /// <summary>
    /// The caller's outstanding requests for one Work, used by duplicate detection.
    /// </summary>
    Task<IReadOnlyList<BookRequest>> GetActiveRequestsForWorkAsync(
        Guid userId,
        Guid workId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Loads a request the given user owns, with its formats and history. Returns
    /// <see langword="null"/> for another user's request, so a caller cannot
    /// distinguish "not yours" from "does not exist".
    /// </summary>
    Task<BookRequest?> FindOwnedRequestAsync(
        Guid requestId,
        Guid userId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<BookRequestView>> ListForUserAsync(
        Guid userId,
        CancellationToken cancellationToken);

    Task<BookRequestView?> FindViewAsync(
        Guid requestId,
        Guid userId,
        CancellationToken cancellationToken);

    void AddRequest(BookRequest request);

    Task SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Runs <paramref name="operation"/> in a transaction that holds a lock scoped
    /// to this (user, Work) pair.
    /// </summary>
    /// <remarks>
    /// Duplicate detection is a read followed by a write, so two simultaneous
    /// submissions would otherwise both find nothing and both insert. The formats
    /// live in a child table, so a single unique index cannot express the rule;
    /// the lock is the deliberate first implementation named in the plan, and a
    /// partial/exclusion constraint can replace it once the repeat-request policy
    /// is settled.
    /// </remarks>
    Task<TResult> InCreateRequestScopeAsync<TResult>(
        Guid userId,
        Guid workId,
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken);
}
