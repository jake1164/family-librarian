using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Domain.Requests;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.Persistence;

public sealed class RequestRepository(AppDbContext database) : IRequestRepository
{
    public Task<bool> WorkExistsAsync(Guid workId, CancellationToken cancellationToken) =>
        database.Works.AnyAsync(work => work.Id == workId && !work.IsRetired, cancellationToken);

    public async Task<IReadOnlyList<BookRequest>> GetActiveRequestsForWorkAsync(
        Guid userId,
        Guid workId,
        CancellationToken cancellationToken) =>
        await database.BookRequests
            .Include(request => request.Formats)
            .Where(request => request.UserId == userId &&
                request.WorkId == workId &&
                (request.Status == RequestStatus.PendingAcquisition ||
                    request.Status == RequestStatus.NeedsReview))
            .OrderByDescending(request => request.RequestedAtUtc)
            .ToArrayAsync(cancellationToken);

    public Task<BookRequest?> FindOwnedRequestAsync(
        Guid requestId,
        Guid userId,
        CancellationToken cancellationToken) =>
        database.BookRequests
            .Include(request => request.Formats)
            .Include(request => request.StatusHistory)
            .SingleOrDefaultAsync(
                request => request.Id == requestId && request.UserId == userId,
                cancellationToken);

    public async Task<IReadOnlyList<BookRequestView>> ListForUserAsync(
        Guid userId,
        CancellationToken cancellationToken) =>
        await ProjectViews(database.BookRequests.Where(request => request.UserId == userId))
            .ToArrayAsync(cancellationToken);

    public Task<BookRequestView?> FindViewAsync(
        Guid requestId,
        Guid userId,
        CancellationToken cancellationToken) =>
        ProjectViews(database.BookRequests
                .Where(request => request.Id == requestId && request.UserId == userId))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<AdminBookRequestView>> ListForAdminAsync(
        RequestStatus? status,
        CancellationToken cancellationToken)
    {
        var requests = database.BookRequests.AsQueryable();
        if (status is not null)
        {
            requests = requests.Where(request => request.Status == status);
        }

        return await ProjectAdminViews(requests).ToArrayAsync(cancellationToken);
    }

    public Task<BookRequest?> FindRequestForAdminAsync(
        Guid requestId,
        CancellationToken cancellationToken) =>
        database.BookRequests
            .Include(request => request.Formats)
            .Include(request => request.StatusHistory)
            .SingleOrDefaultAsync(request => request.Id == requestId, cancellationToken);

    public Task<AdminBookRequestView?> FindAdminViewAsync(
        Guid requestId,
        CancellationToken cancellationToken) =>
        ProjectAdminViews(database.BookRequests.Where(request => request.Id == requestId))
            .SingleOrDefaultAsync(cancellationToken);

    public void AddRequest(BookRequest request) => database.BookRequests.Add(request);

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        database.SaveChangesAsync(cancellationToken);

    public async Task<TResult> InCreateRequestScopeAsync<TResult>(
        Guid userId,
        Guid workId,
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        // A caller may already have a transaction open (the web host does not, but
        // a test or a future composite command might); nesting one here would
        // throw, so join the existing scope instead.
        if (database.Database.CurrentTransaction is not null)
        {
            await AcquireLockAsync(userId, workId, cancellationToken);
            return await operation(cancellationToken);
        }

        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await AcquireLockAsync(userId, workId, cancellationToken);
        var result = await operation(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    /// <summary>
    /// Takes a transaction-scoped advisory lock keyed on (user, Work).
    /// </summary>
    /// <remarks>
    /// <c>pg_advisory_xact_lock</c> releases on commit or rollback, so a failed
    /// create cannot strand the lock. The two 32-bit keys are hashes of the two
    /// GUIDs: a collision only makes two unrelated pairs serialize with each
    /// other, which costs a little concurrency and breaks nothing.
    /// </remarks>
    private async Task AcquireLockAsync(Guid userId, Guid workId, CancellationToken cancellationToken) =>
        _ = await database.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock({userId.GetHashCode()}, {workId.GetHashCode()})",
            cancellationToken);

    /// <summary>
    /// Projects requests with the Work facts My Requests displays.
    /// </summary>
    /// <remarks>
    /// The Work is joined by key rather than reached through a navigation:
    /// <see cref="BookRequest"/> deliberately holds no reference into the catalog
    /// graph, because a request means "this book", not "this row and everything
    /// hanging off it".
    /// <para>
    /// The ordering belongs inside the projection: applying it to the projected
    /// records afterwards is not translatable, and sorting most-recent-first in
    /// the database keeps My Requests from paging through everything later.
    /// </para>
    /// </remarks>
    private IQueryable<BookRequestView> ProjectViews(IQueryable<BookRequest> requests) =>
        from request in requests
        join work in database.Works on request.WorkId equals work.Id
        orderby request.StatusChangedAtUtc descending
        select new BookRequestView(
            request.Id,
            request.WorkId,
            work.CanonicalTitle,
            work.Authors
                .OrderBy(author => author.Ordinal)
                .Select(author => author.Author.CanonicalName)
                .ToList(),
            work.CoverUrl,
            request.Status,
            request.Formats
                .OrderBy(format => format.MediaType)
                .Select(format => new RequestFormatView(format.MediaType, format.Status))
                .ToList(),
            request.RequesterNote,
            request.AdminNote,
            request.RequestedAtUtc,
            request.StatusChangedAtUtc,
            request.Version);

    /// <summary>
    /// The queue projection explicitly joins the Identity user. This association
    /// is deliberately limited to the administrator-only query: a requester's
    /// identity never crosses into their family member's My Requests response.
    /// </summary>
    private IQueryable<AdminBookRequestView> ProjectAdminViews(IQueryable<BookRequest> requests) =>
        from request in requests
        join work in database.Works on request.WorkId equals work.Id
        join user in database.Users on request.UserId equals user.Id
        orderby request.StatusChangedAtUtc descending
        select new AdminBookRequestView(
            new BookRequestView(
                request.Id,
                request.WorkId,
                work.CanonicalTitle,
                work.Authors
                    .OrderBy(author => author.Ordinal)
                    .Select(author => author.Author.CanonicalName)
                    .ToList(),
                work.CoverUrl,
                request.Status,
                request.Formats
                    .OrderBy(format => format.MediaType)
                    .Select(format => new RequestFormatView(format.MediaType, format.Status))
                    .ToList(),
                request.RequesterNote,
                request.AdminNote,
                request.RequestedAtUtc,
                request.StatusChangedAtUtc,
                request.Version),
            user.DisplayName,
            user.Email!,
            request.StatusHistory
                .OrderBy(history => history.OccurredAtUtc)
                .Select(history => new RequestStatusHistoryView(
                    history.FromStatus,
                    history.ToStatus,
                    history.Reason,
                    history.OccurredAtUtc))
                .ToList());
}
