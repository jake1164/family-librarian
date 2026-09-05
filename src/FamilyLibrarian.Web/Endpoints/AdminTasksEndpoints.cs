using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Contracts.Operations;

namespace FamilyLibrarian.Web.Endpoints;

/// <summary>
/// A single, administrator-only reading surface over Family Librarian's
/// existing background-work ledgers. It intentionally composes source,
/// security, and publishing records rather than inventing a second task queue.
/// </summary>
internal static class AdminTasksEndpoints
{
    private const int RecentActivityLimit = 50;

    public static void MapAdminTasksEndpoints(this IEndpointRouteBuilder app)
    {
        var tasks = app.MapGroup("/api/v1/admin/tasks")
            .RequireAuthorization("Admin")
            .AddEndpointFilter<AntiforgeryEndpointFilter>();

        tasks.MapGet("/", GetTasksAsync);
    }

    private static async Task<IResult> GetTasksAsync(
        BookRequestService requests,
        IProviderAttemptRepository providerAttempts,
        MediaAssetQueueService mediaAssets,
        PublishingQueueService publishing,
        IClock clock,
        CancellationToken cancellationToken)
    {
        // These services share the scoped DbContext. Keep their read queries
        // sequential because EF Core forbids parallel operations on one context.
        var requestList = await requests.ListForAdminAsync(status: null, cancellationToken);
        var attemptList = await providerAttempts.ListRecentAsync(RecentActivityLimit, cancellationToken);
        var securityList = await mediaAssets.ListRecentAsync(RecentActivityLimit, cancellationToken);
        // The dashboard's activity feed only ever shows the latest N rows, but the
        // "needs attention" count must reflect every pending asset, not just the
        // ones recent enough to still be in that window — otherwise an older item
        // stuck in review can silently vanish from the tile while still sitting in
        // the actual Security Queue.
        var activeSecurityList = await mediaAssets.ListAsync(cancellationToken);
        var publishingSnapshot = await publishing.ListAsync(cancellationToken);

        var requestResponses = requestList
            .Select(request => new
            {
                Request = request,
                Response = new FamilyLibrarian.Contracts.Requests.AdminBookRequestResponse(
                    RequestEndpoints.ToRequestResponse(
                        request.Request,
                        BookRequestService.AdminTransitionsFrom(request.Request.Status)),
                    request.RequesterDisplayName,
                    request.RequesterEmail,
                    request.StatusHistory.Select(history => new FamilyLibrarian.Contracts.Requests.BookRequestStatusHistoryResponse(
                        history.FromStatus?.ToString(),
                        history.ToStatus.ToString(),
                        history.Reason,
                        history.OccurredAtUtc)).ToArray())
            })
            .ToArray();
        var requestsById = requestResponses.ToDictionary(item => item.Request.Request.Id);
        var formatsById = requestResponses
            .SelectMany(item => item.Request.Request.Formats.Select(format => new { format.Id, format.MediaType }))
            .ToDictionary(item => item.Id, item => item.MediaType);

        var providerResponses = attemptList.Select(attempt =>
        {
            var request = requestsById.GetValueOrDefault(attempt.RequestId)?.Request;
            var mediaType = formatsById.TryGetValue(attempt.RequestFormatId, out var mappedMediaType)
                ? mappedMediaType.ToString()
                : "Unknown format";
            return new AdminProviderTaskResponse(
                attempt.Id,
                attempt.RequestId,
                request?.Request.WorkTitle ?? "Request no longer available",
                request?.RequesterDisplayName ?? "Unknown requester",
                mediaType,
                attempt.ProviderId,
                attempt.Outcome.ToString(),
                attempt.Summary,
                attempt.AttemptedAtUtc,
                attempt.NextEligibleCheckAtUtc);
        }).ToArray();

        var securityResponses = securityList
            .Select(SecurityQueueEndpoints.ToMediaAssetAdminResponse)
            .ToArray();
        var importResponses = publishingSnapshot.LibraryImports
            .Select(PublishingQueueEndpoints.ToLibraryImportResponse)
            .ToArray();
        var deliveryResponses = publishingSnapshot.Deliveries
            .Select(PublishingQueueEndpoints.ToDeliveryResponse)
            .ToArray();

        var summary = new AdminTaskSummaryResponse(
            requestResponses.Count(item => item.Request.Request.IsActive),
            requestResponses.Count(item => item.Request.Request.Status == FamilyLibrarian.Domain.Requests.RequestStatus.NeedsReview),
            activeSecurityList.Count(entry => entry.Asset.StorageState is
                FamilyLibrarian.Domain.Acquisition.MediaAssetStorageState.Quarantine or
                FamilyLibrarian.Domain.Acquisition.MediaAssetStorageState.Processing or
                FamilyLibrarian.Domain.Acquisition.MediaAssetStorageState.Unmatched or
                FamilyLibrarian.Domain.Acquisition.MediaAssetStorageState.Rejected),
            providerResponses.Count(attempt => attempt.Outcome is "Failed" or "Blocked"),
            importResponses.Count(import => import.Status != "Available") +
            deliveryResponses.Count(delivery => delivery.Status != "Delivered"));

        return Results.Ok(new AdminTasksResponse(
            clock.UtcNow,
            summary,
            requestResponses.Select(item => item.Response).ToArray(),
            providerResponses,
            securityResponses,
            importResponses,
            deliveryResponses));
    }
}
