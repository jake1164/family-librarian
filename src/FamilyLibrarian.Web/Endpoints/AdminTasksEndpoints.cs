using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Contracts.Operations;
using FamilyLibrarian.Domain.Providers;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Web.Readiness;

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
        tasks.MapGet("/sources", GetSourcesAsync);
    }

    private static async Task<IResult> GetSourcesAsync(
        ProviderAdminService builtIns, ExternalProviderAdminService external,
        IProviderAttemptRepository attempts, IProviderAcquisitionJobStore jobs,
        SourceSearchTracker searches, ActiveAcquisitionTracker acquisitions,
        IClock clock, CancellationToken cancellationToken)
    {
        // All reads share a scoped context; no network probe or search is started by this endpoint.
        var installed = await builtIns.GetStatusesAsync(cancellationToken);
        var configured = await external.ListAsync(cancellationToken);
        var latest = await attempts.ListLatestByProviderAsync(cancellationToken);
        var remote = await jobs.ListSourceActivityAsync(cancellationToken);
        var local = acquisitions.Snapshot();
        var searching = searches.Snapshot();
        var sources = new List<AdminSourceStatusResponse>();

        foreach (var source in installed.Where(source => source.Capabilities.Contains(ProviderCapability.DirectAcquisition)))
        {
            var state = !source.IsEnabled ? "Disabled" : source.IsMisconfigured ? "NeedsSetup"
                : source.LastTestSucceeded == false ? "Unavailable" : "Ready";
            sources.Add(Project(source.ProviderId, source.DisplayName, state, null));
        }
        foreach (var source in configured)
        {
            var limited = source.CachedHealthStatus is not (null or "Healthy") ||
                source.CachedSearchOperationStatus is not (null or "Available") ||
                source.CachedAcquireOperationStatus is not (null or "Available");
            var state = !source.IsEnabled ? "Disabled"
                : source.CachedHealthStatus == "Unhealthy" ? "Unavailable"
                : limited ? "Limited" : source.LastTestSucceeded == false ? "Unavailable"
                : source.LastTestedAtUtc is null ? "Unchecked" : "Ready";
            sources.Add(Project(source.ProviderId, source.DisplayName, state, source.RecheckSchedule));
        }

        return Results.Ok(new AdminSourcesResponse(clock.UtcNow,
            sources.OrderBy(source => source.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray()));

        AdminSourceStatusResponse Project(string id, string name, string availability, string? schedule)
        {
            bool Matches(string other) => string.Equals(id, other, StringComparison.OrdinalIgnoreCase);
            var direct = local.Where(activity => Matches(activity.ProviderId)).ToArray();
            var stages = searching.Where(Matches).Select(_ => "Searching")
                .Concat(direct.Select(activity => activity.Stage switch
                {
                    "Downloading" => "Downloading",
                    "Processing files" => "Processing",
                    _ => "Checking"
                }))
                // A local transfer may be collecting a remote job's output: count it once.
                .Concat(remote.Where(job => Matches(job.ProviderId) &&
                    !direct.Any(activity => activity.RequestFormatId == job.RequestFormatId))
                    .Select(job => job.State switch
                    {
                        ProviderAcquisitionJobLifecycleState.Queued => "Queued",
                        ProviderAcquisitionJobLifecycleState.Waiting => job.NeedsInteraction ? "WaitingForAction" : "Waiting",
                        _ => string.Equals(job.Phase, "downloading", StringComparison.OrdinalIgnoreCase)
                            ? "Downloading" : "Working"
                    }))
                .GroupBy(stage => stage)
                .Select(group => new AdminSourceActivityResponse(group.Key, group.Count()))
                .OrderBy(activity => activity.Stage, StringComparer.Ordinal)
                .ToArray();
            var detail = availability switch
            {
                "Disabled" => "Disabled for new work. Existing jobs may still finish.",
                "NeedsSetup" => "Configuration is incomplete. Review Sources.",
                "Unavailable" => "The last connection or health check failed. Review Sources.",
                "Limited" => "The last health check reported limited operations. Review Sources.",
                "Unchecked" => "No connection check has been recorded yet.",
                _ => stages.Length == 0 ? "No active searches or acquisition jobs." : "Current searches and acquisition jobs."
            };
            return new(id, name, availability, detail, stages,
                latest.FirstOrDefault(attempt => Matches(attempt.ProviderId))?.AttemptedAtUtc, schedule);
        }
    }

    private static async Task<IResult> GetTasksAsync(
        BookRequestService requests,
        IProviderAttemptRepository providerAttempts,
        IProviderAcquisitionJobStore providerJobs,
        MediaAssetQueueService mediaAssets,
        PublishingQueueService publishing,
        SystemReadinessService readiness,
        IClock clock,
        CancellationToken cancellationToken)
    {
        // These services share the scoped DbContext. Keep their read queries
        // sequential because EF Core forbids parallel operations on one context.
        var requestList = await requests.ListForAdminAsync(status: null, cancellationToken);
        var attemptList = await providerAttempts.ListRecentAsync(RecentActivityLimit, cancellationToken);
        // The newest N rows are enough to tell what is current: any row newer than a listed one is listed too.
        var displayOutcomes = ProviderAttemptDisplay.Derive(
            attemptList,
            await providerJobs.ListAttemptJobFactsAsync(
                [.. attemptList.Where(attempt => attempt.ProviderAcquisitionJobId != null).Select(attempt => attempt.ProviderAcquisitionJobId!.Value)],
                cancellationToken));
        var securityList = await mediaAssets.ListRecentAsync(RecentActivityLimit, cancellationToken);
        // The dashboard's activity feed only ever shows the latest N rows, but the
        // "needs attention" count must reflect every pending asset, not just the
        // ones recent enough to still be in that window — otherwise an older item
        // stuck in review can silently vanish from the tile while still sitting in
        // the actual Security Queue.
        var activeSecurityList = await mediaAssets.ListAsync(cancellationToken);
        var publishingSnapshot = await publishing.ListAsync(cancellationToken);
        // A destination can be fully down (CWA/Audiobookshelf failing its saved
        // connection test) with zero in-flight imports or deliveries queued
        // against it -- the per-item counts below would silently read zero in
        // exactly that case, so the destination-level signal is folded in too.
        var systemReadiness = await readiness.GetReadinessAsync(cancellationToken);
        var publishingIssues = systemReadiness.DegradedComponents
            .Where(component => component.Category == SystemReadinessCategories.Publishing)
            .ToArray();

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
                attempt.NextEligibleCheckAtUtc,
                displayOutcomes[attempt.Id]);
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
        var kindleDeliveryResponses = publishingSnapshot.DeliveryAttempts
            .Select(PublishingQueueEndpoints.ToDeliveryAttemptResponse)
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
            deliveryResponses.Count(delivery => delivery.Status != "Delivered") +
            kindleDeliveryResponses.Count(attempt => attempt.Status == "Failed") +
            publishingIssues.Length);

        return Results.Ok(new AdminTasksResponse(
            clock.UtcNow,
            summary,
            requestResponses.Select(item => item.Response).ToArray(),
            providerResponses,
            securityResponses,
            importResponses,
            deliveryResponses,
            kindleDeliveryResponses,
            publishingIssues));
    }
}
