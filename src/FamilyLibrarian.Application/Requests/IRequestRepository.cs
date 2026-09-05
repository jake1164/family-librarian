using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Application.Requests;

/// <summary>The persistence boundary for book requests.</summary>
public interface IRequestRepository
{
    Task<bool> WorkExistsAsync(Guid workId, CancellationToken cancellationToken);

    /// <summary>
    /// All household outstanding requests for one Work, used by shared-request resolution.
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

    Task<IReadOnlyList<AdminBookRequestView>> ListForAdminAsync(
        RequestStatus? status,
        CancellationToken cancellationToken);

    /// <summary>Counts a status for the administrator attention summary.</summary>
    Task<int> CountForAdminAsync(
        RequestStatus status,
        CancellationToken cancellationToken) =>
        Task.FromResult(0);

    Task<BookRequest?> FindRequestForAdminAsync(
        Guid requestId,
        CancellationToken cancellationToken);

    /// <summary>
    /// A small, bounded batch of requests that have not yet left the automatic
    /// acquisition queue. This is a server-only background-work query; it never
    /// exposes requester identity to a provider.
    /// </summary>
    Task<IReadOnlyList<BookRequest>> ListPendingForAutomaticFulfillmentAsync(
        int maximumCount,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<BookRequest>>([]);

    /// <summary>Whether this requested format already has an acquired artifact.</summary>
    Task<bool> HasAcquiredArtifactAsync(
        Guid requestFormatId,
        CancellationToken cancellationToken) =>
        Task.FromResult(false);

    Task<AdminBookRequestView?> FindAdminViewAsync(
        Guid requestId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Full entities in <paramref name="status"/>, optionally restricted to ones
    /// with a prior <see cref="Domain.Acquisition.ProviderAttempt"/> against
    /// <paramref name="providerId"/>. Backs the admin manual-recheck action —
    /// never exposed to requesters.
    /// </summary>
    Task<IReadOnlyList<BookRequest>> ListForManualRecheckAsync(
        RequestStatus status,
        string? providerId,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<BookRequest>>([]);

    void AddRequest(BookRequest request);

    Task SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Runs <paramref name="operation"/> in a transaction that holds a lock scoped
    /// to this Work across all users.
    /// </summary>
    /// <remarks>
    /// Serializes create, join, and withdrawal for all requesters of the same Work.
    /// A unique partial index also prevents two active ordinary aggregates.
    /// </remarks>
    Task<TResult> InCreateRequestScopeAsync<TResult>(
        Guid userId,
        Guid workId,
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken);
}
