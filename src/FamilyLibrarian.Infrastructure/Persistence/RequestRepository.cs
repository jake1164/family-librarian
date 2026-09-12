using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Delivery;
using FamilyLibrarian.Domain.Publishing;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.Persistence;

public sealed class RequestRepository(
    AppDbContext database,
    ICwaSettingsStore cwaSettingsStore,
    IAudiobookshelfSettingsStore audiobookshelfSettingsStore) : IRequestRepository, IBookRequestFulfillmentStore
{
    public Task<bool> WorkExistsAsync(Guid workId, CancellationToken cancellationToken) =>
        database.Works.AnyAsync(work => work.Id == workId && !work.IsRetired, cancellationToken);

    public async Task<IReadOnlyList<BookRequest>> GetActiveRequestsForWorkAsync(
        Guid userId,
        Guid workId,
        CancellationToken cancellationToken) =>
        await database.BookRequests
            .Include(request => request.Participants)
            .Include(request => request.Formats)
            .Include(request => request.ReviewCandidates)
            .Where(request => request.WorkId == workId &&
                (request.Status == RequestStatus.PendingAcquisition ||
                    request.Status == RequestStatus.NeedsReview))
            .OrderByDescending(request => request.RequestedAtUtc)
            .ToArrayAsync(cancellationToken);

    public Task<BookRequest?> FindOwnedRequestAsync(
        Guid requestId,
        Guid userId,
        CancellationToken cancellationToken) =>
        database.BookRequests
            .Include(request => request.Participants)
            .Include(request => request.Formats)
            .Include(request => request.StatusHistory)
            .Include(request => request.ReviewCandidates)
            .SingleOrDefaultAsync(
                request => request.Id == requestId && request.Participants.Any(participant => participant.UserId == userId),
                cancellationToken);

    public async Task<IReadOnlyList<BookRequestView>> ListForUserAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var requests = await ProjectViews(database.BookRequests.Where(request => request.Participants.Any(participant => participant.UserId == userId)), userId)
            .ToArrayAsync(cancellationToken);
        return await AddRequesterProgressAsync(requests, cancellationToken);
    }

    public async Task<BookRequestView?> FindViewAsync(
        Guid requestId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var request = await ProjectViews(database.BookRequests
                .Where(request => request.Id == requestId && request.Participants.Any(participant => participant.UserId == userId)), userId)
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

    public Task<int> CountForAdminAsync(
        RequestStatus status,
        CancellationToken cancellationToken) =>
        database.BookRequests.CountAsync(request => request.Status == status, cancellationToken);

    public Task<BookRequest?> FindRequestForAdminAsync(
        Guid requestId,
        CancellationToken cancellationToken) =>
        database.BookRequests
            .Include(request => request.Participants)
            .Include(request => request.Formats)
            .Include(request => request.StatusHistory)
            .Include(request => request.ReviewCandidates)
            .SingleOrDefaultAsync(request => request.Id == requestId, cancellationToken);

    public async Task<IReadOnlyList<BookRequest>> ListPendingForAutomaticFulfillmentAsync(
        int maximumCount,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCount);

        return await database.BookRequests
            .Include(request => request.Participants)
            .Include(request => request.Formats)
            .Include(request => request.StatusHistory)
            .Include(request => request.ReviewCandidates)
            .Where(request => request.Status == RequestStatus.PendingAcquisition && !request.RequiresManualFulfillment)
            .OrderBy(request => request.RequestedAtUtc)
            .Take(maximumCount)
            .ToArrayAsync(cancellationToken);
    }

    public Task<bool> HasAcquiredArtifactAsync(
        Guid requestFormatId,
        CancellationToken cancellationToken) =>
        database.MediaAssets.AnyAsync(
            asset => asset.AssociatedRequestFormatId == requestFormatId,
            cancellationToken);

    public Task<BookRequest?> FindByFormatIdAsync(
        Guid requestFormatId,
        CancellationToken cancellationToken) =>
        database.BookRequests
            .Include(request => request.Participants)
            .Include(request => request.Formats)
            .Include(request => request.StatusHistory)
            .Include(request => request.ReviewCandidates)
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

    public async Task<IReadOnlyList<BookRequest>> ListForManualRecheckAsync(
        RequestStatus status,
        string? providerId,
        CancellationToken cancellationToken)
    {
        var query = database.BookRequests
            .Include(request => request.Participants)
            .Include(request => request.Formats)
            .Include(request => request.StatusHistory)
            .Include(request => request.ReviewCandidates)
            .Where(request => request.Status == status);

        if (providerId is not null)
        {
            query = query.Where(request =>
                database.ProviderAttempts.Any(attempt =>
                    attempt.RequestId == request.Id && attempt.ProviderId == providerId));
        }

        return await query
            .OrderBy(request => request.RequestedAtUtc)
            .ToArrayAsync(cancellationToken);
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
            await AcquireLockAsync(workId, cancellationToken);
            return await operation(cancellationToken);
        }

        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await AcquireLockAsync(workId, cancellationToken);
        var result = await operation(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    /// <summary>
    /// Takes a transaction-scoped advisory lock keyed on Work across all requesters.
    /// </summary>
    /// <remarks>
    /// <c>pg_advisory_xact_lock</c> releases on commit or rollback, so a failed
    /// create cannot strand the lock. The keys are a namespace and a Work hash:
    /// a collision only makes two unrelated works serialize with each
    /// other, which costs a little concurrency and breaks nothing.
    /// </remarks>
    private async Task AcquireLockAsync(Guid workId, CancellationToken cancellationToken) =>
        _ = await database.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock({831704}, {workId.GetHashCode()})",
            cancellationToken);

    /// <summary>
    /// Adds the state a requester can safely see for each format without loading
    /// asset metadata, scanner results, or destination failure details.
    /// </summary>
    private async Task<IReadOnlyList<BookRequestView>> AddRequesterProgressAsync(
        IReadOnlyList<BookRequestView> requests,
        CancellationToken cancellationToken)
    {
        // Independent of the format-progress lookups below (a Kindle delivery
        // attempt can exist -- and this needs to keep showing it -- even once
        // its request has no outstanding formats left).
        var kindleDeliveries = await LoadKindleDeliveriesAsync(requests, cancellationToken);
        var needsReview = await LoadNeedsReviewAsync(requests, cancellationToken);
        requests = ApplyNeedsReview(requests, needsReview);

        var formatIds = requests
            .SelectMany(request => request.Formats)
            .Select(format => format.Id)
            .ToArray();
        if (formatIds.Length == 0)
        {
            return ApplyKindleDeliveries(requests, kindleDeliveries);
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
                asset.CreatedAtUtc,
                asset.BundleId))
            .ToArrayAsync(cancellationToken);

        var latestAssets = assets
            .GroupBy(asset => asset.RequestFormatId)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(asset => asset.CreatedAtUtc).First());
        var assetIds = latestAssets.Values.Select(asset => asset.AssetId).ToArray();
        if (assetIds.Length == 0)
        {
            return ApplyKindleDeliveries(requests, kindleDeliveries);
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
            .Select(import => new LibraryImportProgressRow(import.AssetId, import.Status, import.ExternalBookId))
            .ToArrayAsync(cancellationToken);
        var latestImports = imports
            .GroupBy(import => import.AssetId)
            .ToDictionary(group => group.Key, group => group.First());

        // A bundle's Delivery (e.g. a chaptered audiobook) is keyed by
        // BundleId rather than any single track's AssetId, so its lookup
        // needs both.
        var bundleIds = latestAssets.Values
            .Where(asset => asset.BundleId.HasValue)
            .Select(asset => asset.BundleId!.Value)
            .Distinct()
            .ToArray();

        var deliveries = await database.Deliveries
            .AsNoTracking()
            .Where(delivery =>
                (delivery.AssetId != null && assetIds.Contains(delivery.AssetId.Value)) ||
                (delivery.BundleId != null && bundleIds.Contains(delivery.BundleId.Value)))
            .OrderByDescending(delivery => delivery.CreatedAtUtc)
            .Select(delivery => new DeliveryProgressRow(
                delivery.AssetId, delivery.BundleId, delivery.Status, delivery.ExternalItemId))
            .ToArrayAsync(cancellationToken);
        var latestDeliveriesByAsset = deliveries
            .Where(delivery => delivery.AssetId.HasValue)
            .GroupBy(delivery => delivery.AssetId!.Value)
            .ToDictionary(group => group.Key, group => group.First());
        var latestDeliveriesByBundle = deliveries
            .Where(delivery => delivery.BundleId.HasValue)
            .GroupBy(delivery => delivery.BundleId!.Value)
            .ToDictionary(group => group.Key, group => group.First());

        // Deep links are only ever built once, off the settings row (not per
        // format) -- and only queried at all when something in this batch
        // actually reached the terminal state that carries a usable external
        // id (an empty/all-pending batch never touches these stores).
        var cwaSettings = latestImports.Values.Any(import =>
                import.Status == LibraryImportStatus.Available && import.ExternalBookId is not null)
            ? await cwaSettingsStore.FindAsync(cancellationToken)
            : null;
        var audiobookshelfSettings = latestDeliveriesByAsset.Values.Concat(latestDeliveriesByBundle.Values).Any(
                delivery => delivery.Status == AudiobookshelfDeliveryStatus.Delivered && delivery.ExternalItemId is not null)
            ? await audiobookshelfSettingsStore.FindAsync(cancellationToken)
            : null;

        var enriched = requests
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
                        LibraryImportProgressRow? libraryImport =
                            latestImports.TryGetValue(asset.AssetId, out var libraryImportRow)
                                ? libraryImportRow
                                : null;
                        DeliveryProgressRow? delivery = asset.BundleId.HasValue
                            ? (latestDeliveriesByBundle.TryGetValue(asset.BundleId.Value, out var bundleDelivery)
                                ? bundleDelivery
                                : null)
                            : (latestDeliveriesByAsset.TryGetValue(asset.AssetId, out var assetDelivery)
                                ? assetDelivery
                                : null);

                        // Read the external id only once the underlying row
                        // has reached its terminal Available/Delivered state --
                        // a retry (see LibraryImport.ResetForRetry) resets
                        // Status but leaves a prior ExternalBookId in place,
                        // so this gate is what stops a mid-retry format from
                        // linking to a superseded import.
                        var externalActionUri = libraryImport is { Status: LibraryImportStatus.Available }
                            ? ExternalLibraryLinks.BuildCwaBookLink(cwaSettings, libraryImport.ExternalBookId)
                            : delivery is { Status: AudiobookshelfDeliveryStatus.Delivered }
                                ? ExternalLibraryLinks.BuildAudiobookshelfItemLink(
                                    audiobookshelfSettings, delivery.ExternalItemId)
                                : null;

                        return format with
                        {
                            Progress = RequestFormatProgress.Describe(
                                asset.StorageState,
                                securityStatus,
                                libraryImport?.Status,
                                delivery?.Status),
                            ExternalActionUri = externalActionUri
                        };
                    })
                    .ToArray()
            })
            .ToArray();

        return ApplyKindleDeliveries(enriched, kindleDeliveries);
    }

    /// <summary>
    /// The viewer's own most recent Kindle delivery attempt per request, keyed
    /// by <c>RequestId</c>. Matched by <c>(RequestId, DeliveryTargetId)</c> --
    /// <see cref="BookRequestView.DeliveryTargetId"/> is already viewer-scoped
    /// (set by <see cref="ProjectViews"/>), and a <see cref="DeliveryTarget"/>
    /// belongs to exactly one user, so this never leaks another participant's
    /// delivery status on a shared request.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, RequestKindleDeliveryView>> LoadKindleDeliveriesAsync(
        IReadOnlyList<BookRequestView> requests, CancellationToken cancellationToken)
    {
        var targetByRequest = requests
            .Where(request => request.DeliveryTargetId.HasValue)
            .ToDictionary(request => request.Id, request => request.DeliveryTargetId!.Value);
        if (targetByRequest.Count == 0)
        {
            return new Dictionary<Guid, RequestKindleDeliveryView>();
        }

        var requestIds = targetByRequest.Keys.ToArray();
        var attempts = await database.DeliveryAttempts
            .AsNoTracking()
            .Where(attempt => attempt.RequestId != null && requestIds.Contains(attempt.RequestId!.Value))
            .OrderByDescending(attempt => attempt.AttemptNumber)
            .Select(attempt => new KindleDeliveryProgressRow(
                attempt.RequestId!.Value, attempt.DeliveryTargetId, attempt.Id, attempt.Status,
                attempt.FailureReason, attempt.AttemptNumber, attempt.ConfirmationStatus))
            .ToArrayAsync(cancellationToken);

        return attempts
            .Where(attempt => targetByRequest[attempt.RequestId] == attempt.DeliveryTargetId)
            .GroupBy(attempt => attempt.RequestId)
            .ToDictionary(
                group => group.Key,
                group => new RequestKindleDeliveryView(
                    group.First().AttemptId, group.First().Status, group.First().FailureReason,
                    group.First().AttemptNumber, group.First().ConfirmationStatus));
    }

    private static IReadOnlyList<BookRequestView> ApplyKindleDeliveries(
        IReadOnlyList<BookRequestView> requests, IReadOnlyDictionary<Guid, RequestKindleDeliveryView> deliveries) =>
        deliveries.Count == 0
            ? requests
            : requests
                .Select(request => deliveries.TryGetValue(request.Id, out var kindle)
                    ? request with { KindleDelivery = kindle }
                    : request)
                .ToArray();

    /// <summary>
    /// SELFSERV-1: only a <see cref="RequestReviewCategory.PreferenceAmbiguity"/>
    /// review is ever loaded here -- <see cref="RequestReviewCategory.ProviderDisagreement"/>/
    /// <see cref="RequestReviewCategory.SecurityOrIdentityFailure"/> stay
    /// admin-only and unchanged, with nothing new for the owner to see.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, RequestNeedsReviewView>> LoadNeedsReviewAsync(
        IReadOnlyList<BookRequestView> requests, CancellationToken cancellationToken)
    {
        var needsReviewIds = requests
            .Where(request => request.Status == RequestStatus.NeedsReview)
            .Select(request => request.Id)
            .ToArray();
        if (needsReviewIds.Length == 0)
        {
            return new Dictionary<Guid, RequestNeedsReviewView>();
        }

        var preferenceAmbiguityIds = await database.BookRequests
            .AsNoTracking()
            .Where(request => needsReviewIds.Contains(request.Id) &&
                request.ReviewCategory == RequestReviewCategory.PreferenceAmbiguity)
            .Select(request => request.Id)
            .ToArrayAsync(cancellationToken);
        if (preferenceAmbiguityIds.Length == 0)
        {
            return new Dictionary<Guid, RequestNeedsReviewView>();
        }

        var candidates = await database.RequestReviewCandidates
            .AsNoTracking()
            .Where(candidate => preferenceAmbiguityIds.Contains(candidate.RequestId))
            .OrderBy(candidate => candidate.DisplayOrder)
            .Select(candidate => new RequestReviewCandidateRow(
                candidate.RequestId, candidate.Id, candidate.Title, candidate.Author, candidate.Language))
            .ToArrayAsync(cancellationToken);
        var candidatesByRequest = candidates
            .GroupBy(candidate => candidate.RequestId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<RequestReviewCandidateView>)group
                    .Select(candidate => new RequestReviewCandidateView(
                        candidate.CandidateId, candidate.Title, candidate.Author, candidate.Language))
                    .ToArray());

        return preferenceAmbiguityIds.ToDictionary(
            requestId => requestId,
            requestId => new RequestNeedsReviewView(
                RequestReviewCategory.PreferenceAmbiguity,
                candidatesByRequest.TryGetValue(requestId, out var list) ? list : []));
    }

    private static IReadOnlyList<BookRequestView> ApplyNeedsReview(
        IReadOnlyList<BookRequestView> requests, IReadOnlyDictionary<Guid, RequestNeedsReviewView> reviews) =>
        reviews.Count == 0
            ? requests
            : requests
                .Select(request => reviews.TryGetValue(request.Id, out var review)
                    ? request with { NeedsReview = review }
                    : request)
                .ToArray();

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
    private IQueryable<BookRequestView> ProjectViews(IQueryable<BookRequest> requests, Guid userId) =>
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
            request.Participants.Any(participant => participant.UserId == userId && participant.WithdrawnAtUtc != null)
                ? RequestStatus.Cancelled
                : request.Formats.Where(format => request.Participants.Any(participant => participant.UserId == userId &&
                    ((format.MediaType == RequestMediaType.Ebook && participant.WantsEbook) ||
                     (format.MediaType == RequestMediaType.Audiobook && participant.WantsAudiobook))))
                    .All(format => format.Status == RequestFormatStatus.Available) ? RequestStatus.Available : request.Status,
            request.Formats
                .Where(format => request.Participants.Any(participant => participant.UserId == userId &&
                    ((format.MediaType == RequestMediaType.Ebook && participant.WantsEbook) ||
                     (format.MediaType == RequestMediaType.Audiobook && participant.WantsAudiobook))))
                .OrderBy(format => format.MediaType)
                .Select(format => new RequestFormatView(format.Id, format.MediaType, format.Status))
                .ToList(),
            request.Participants.Where(participant => participant.UserId == userId).Select(participant => participant.Note).FirstOrDefault(),
            request.AdminNote,
            request.RequestedAtUtc,
            request.StatusChangedAtUtc,
            request.Version,
            request.Participants.Count(participant => participant.WithdrawnAtUtc == null),
            request.RequiresManualFulfillment,
            request.VersionKind,
            request.VersionDetails,
            request.Participants.Where(participant => participant.UserId == userId)
                .Select(participant => participant.DeliveryTargetId).FirstOrDefault());

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
                request.Version,
                request.Participants.Count(participant => participant.WithdrawnAtUtc == null),
                request.RequiresManualFulfillment,
                request.VersionKind,
                request.VersionDetails),
            user.DisplayName,
            user.Email!,
            request.StatusHistory
                .OrderBy(history => history.OccurredAtUtc)
                .Select(history => new RequestStatusHistoryView(
                    history.FromStatus,
                    history.ToStatus,
                    history.Reason,
                    history.OccurredAtUtc))
                .ToList(),
            request.Participants.Select(participant => new RequestParticipantView(
                database.Users.Where(member => member.Id == participant.UserId).Select(member => member.DisplayName).First(),
                database.Users.Where(member => member.Id == participant.UserId).Select(member => member.Email!).First(),
                participant.Note,
                participant.WithdrawnAtUtc != null)).ToList());

    private sealed record MediaAssetProgressRow(
        Guid AssetId,
        Guid RequestFormatId,
        MediaAssetStorageState StorageState,
        DateTimeOffset CreatedAtUtc,
        Guid? BundleId);

    private sealed record SecurityEvaluationProgressRow(
        Guid AssetId,
        SecurityEvaluationStatus Status);

    private sealed record LibraryImportProgressRow(Guid AssetId, LibraryImportStatus Status, string? ExternalBookId);

    private sealed record DeliveryProgressRow(
        Guid? AssetId, Guid? BundleId, AudiobookshelfDeliveryStatus Status, string? ExternalItemId);

    private sealed record KindleDeliveryProgressRow(
        Guid RequestId, Guid DeliveryTargetId, Guid AttemptId, DeliveryAttemptStatus Status,
        string? FailureReason, int AttemptNumber, DeliveryConfirmationStatus ConfirmationStatus);

    private sealed record RequestReviewCandidateRow(
        Guid RequestId, Guid CandidateId, string Title, string? Author, string? Language);
}
