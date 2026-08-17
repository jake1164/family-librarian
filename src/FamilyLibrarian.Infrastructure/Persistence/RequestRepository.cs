using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Publishing;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.Persistence;

public sealed class RequestRepository(AppDbContext database) : IRequestRepository, IBookRequestFulfillmentStore
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
        CancellationToken cancellationToken)
    {
        var requests = await ProjectViews(database.BookRequests.Where(request => request.UserId == userId))
            .ToArrayAsync(cancellationToken);
        return await AddRequesterProgressAsync(requests, cancellationToken);
    }

    public async Task<BookRequestView?> FindViewAsync(
        Guid requestId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var request = await ProjectViews(database.BookRequests
                .Where(request => request.Id == requestId && request.UserId == userId))
            .SingleOrDefaultAsync(cancellationToken);
        if (request is null)
        {
            return null;
        }

        return (await AddRequesterProgressAsync([request], cancellationToken)).Single();
    }

    public async Task<IReadOnlyList<AdminBookRequestView>> ListForAdminAsync(
        RequestStatus? status,
        CancellationToken cancellationToken)
    {
        var requests = database.BookRequests.AsQueryable();
        if (status is not null)
        {
            requests = requests.Where(request => request.Status == status);
        }

        var views = await ProjectAdminViews(requests).ToArrayAsync(cancellationToken);
        return await AddAdminProgressAsync(views, cancellationToken);
    }

    public Task<BookRequest?> FindRequestForAdminAsync(
        Guid requestId,
        CancellationToken cancellationToken) =>
        database.BookRequests
            .Include(request => request.Formats)
            .Include(request => request.StatusHistory)
            .SingleOrDefaultAsync(request => request.Id == requestId, cancellationToken);

    public Task<BookRequest?> FindByFormatIdAsync(
        Guid requestFormatId,
        CancellationToken cancellationToken) =>
        database.BookRequests
            .Include(request => request.Formats)
            .Include(request => request.StatusHistory)
            .SingleOrDefaultAsync(
                request => request.Formats.Any(format => format.Id == requestFormatId),
                cancellationToken);

    public async Task<AdminBookRequestView?> FindAdminViewAsync(
        Guid requestId,
        CancellationToken cancellationToken)
    {
        var request = await ProjectAdminViews(database.BookRequests.Where(request => request.Id == requestId))
            .SingleOrDefaultAsync(cancellationToken);
        if (request is null)
        {
            return null;
        }

        return (await AddAdminProgressAsync([request], cancellationToken)).Single();
    }

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
    /// Adds the state a requester can safely see for each format without loading
    /// asset metadata, scanner results, or destination failure details.
    /// </summary>
    private async Task<IReadOnlyList<BookRequestView>> AddRequesterProgressAsync(
        IReadOnlyList<BookRequestView> requests,
        CancellationToken cancellationToken)
    {
        var formatIds = requests
            .SelectMany(request => request.Formats)
            .Select(format => format.Id)
            .ToArray();
        if (formatIds.Length == 0)
        {
            return requests;
        }

        // These are separate, bounded projections rather than a large join of
        // collections. That avoids duplicating request rows and keeps each query
        // to the facts used in the requester-facing progress message.
        var assets = await database.MediaAssets
            .AsNoTracking()
            .Where(asset => formatIds.Contains(asset.AssociatedRequestFormatId))
            .Select(asset => new MediaAssetProgressRow(
                asset.Id,
                asset.AssociatedRequestFormatId,
                asset.StorageState,
                asset.CreatedAtUtc))
            .ToArrayAsync(cancellationToken);

        var latestAssets = assets
            .GroupBy(asset => asset.RequestFormatId)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(asset => asset.CreatedAtUtc).First());
        var assetIds = latestAssets.Values.Select(asset => asset.AssetId).ToArray();
        if (assetIds.Length == 0)
        {
            return requests;
        }

        var evaluations = await database.SecurityEvaluations
            .AsNoTracking()
            .Where(evaluation => assetIds.Contains(evaluation.AssetId))
            .OrderByDescending(evaluation => evaluation.CreatedAtUtc)
            .Select(evaluation => new SecurityEvaluationProgressRow(
                evaluation.AssetId,
                evaluation.Status))
            .ToArrayAsync(cancellationToken);
        var latestEvaluations = evaluations
            .GroupBy(evaluation => evaluation.AssetId)
            .ToDictionary(group => group.Key, group => group.First().Status);

        var imports = await database.LibraryImports
            .AsNoTracking()
            .Where(import => assetIds.Contains(import.AssetId))
            .OrderByDescending(import => import.CreatedAtUtc)
            .Select(import => new LibraryImportProgressRow(import.AssetId, import.Status))
            .ToArrayAsync(cancellationToken);
        var latestImports = imports
            .GroupBy(import => import.AssetId)
            .ToDictionary(group => group.Key, group => group.First().Status);

        var deliveries = await database.Deliveries
            .AsNoTracking()
            .Where(delivery => assetIds.Contains(delivery.AssetId))
            .OrderByDescending(delivery => delivery.CreatedAtUtc)
            .Select(delivery => new DeliveryProgressRow(delivery.AssetId, delivery.Status))
            .ToArrayAsync(cancellationToken);
        var latestDeliveries = deliveries
            .GroupBy(delivery => delivery.AssetId)
            .ToDictionary(group => group.Key, group => group.First().Status);

        return requests
            .Select(request => request with
            {
                Formats = request.Formats
                    .Select(format =>
                    {
                        if (!latestAssets.TryGetValue(format.Id, out var asset))
                        {
                            return format;
                        }

                        SecurityEvaluationStatus? securityStatus =
                            latestEvaluations.TryGetValue(asset.AssetId, out var evaluation)
                                ? evaluation
                                : null;
                        LibraryImportStatus? libraryImportStatus =
                            latestImports.TryGetValue(asset.AssetId, out var libraryImport)
                                ? libraryImport
                                : null;
                        DeliveryStatus? deliveryStatus =
                            latestDeliveries.TryGetValue(asset.AssetId, out var delivery)
                                ? delivery
                                : null;

                        return format with
                        {
                            Progress = RequestFormatProgress.Describe(
                                asset.StorageState,
                                securityStatus,
                                libraryImportStatus,
                                deliveryStatus)
                        };
                    })
                    .ToArray()
            })
            .ToArray();
    }

    private async Task<IReadOnlyList<AdminBookRequestView>> AddAdminProgressAsync(
        IReadOnlyList<AdminBookRequestView> requests,
        CancellationToken cancellationToken)
    {
        var progress = await AddRequesterProgressAsync(
            requests.Select(request => request.Request).ToArray(),
            cancellationToken);
        var progressByRequestId = progress.ToDictionary(request => request.Id);

        return requests
            .Select(request => request with { Request = progressByRequestId[request.Request.Id] })
            .ToArray();
    }

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
                .Select(format => new RequestFormatView(format.Id, format.MediaType, format.Status))
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
                    .Select(format => new RequestFormatView(format.Id, format.MediaType, format.Status))
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

    private sealed record MediaAssetProgressRow(
        Guid AssetId,
        Guid RequestFormatId,
        MediaAssetStorageState StorageState,
        DateTimeOffset CreatedAtUtc);

    private sealed record SecurityEvaluationProgressRow(
        Guid AssetId,
        SecurityEvaluationStatus Status);

    private sealed record LibraryImportProgressRow(Guid AssetId, LibraryImportStatus Status);

    private sealed record DeliveryProgressRow(Guid AssetId, DeliveryStatus Status);
}
