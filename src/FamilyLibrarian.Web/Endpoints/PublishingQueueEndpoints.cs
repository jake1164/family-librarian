using FamilyLibrarian.Application.Delivery;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Contracts.Publishing;

namespace FamilyLibrarian.Web.Endpoints;

/// <summary>
/// What happened after an approved file was handed to CWA or Audiobookshelf,
/// plus a recheck for anything not yet confirmed.
/// </summary>
internal static class PublishingQueueEndpoints
{
    public static void MapPublishingQueueEndpoints(this IEndpointRouteBuilder app)
    {
        var publishingQueue = app.MapGroup("/api/v1/admin/publishing")
            .RequireAuthorization("Admin")
            .AddEndpointFilter<AntiforgeryEndpointFilter>();

        publishingQueue.MapGet("/queue", GetPublishingQueueAsync);
        publishingQueue.MapPost("/library-imports/{id:guid}/recheck", RecheckLibraryImportAsync);
        publishingQueue.MapPost("/deliveries/{id:guid}/recheck", RecheckDeliveryAsync);
        publishingQueue.MapPost("/delivery-attempts/{id:guid}/retry", RetryDeliveryAttemptAsync);
    }

    private static async Task<IResult> GetPublishingQueueAsync(
        PublishingQueueService service, CancellationToken cancellationToken)
    {
        var snapshot = await service.ListAsync(cancellationToken);
        return Results.Ok(new PublishingQueueResponse(
            snapshot.LibraryImports.Select(ToLibraryImportResponse).ToArray(),
            snapshot.Deliveries.Select(ToDeliveryResponse).ToArray(),
            snapshot.DeliveryAttempts.Select(ToDeliveryAttemptResponse).ToArray()));
    }

    private static async Task<IResult> RecheckLibraryImportAsync(
        Guid id, CwaPublishingService service, CancellationToken cancellationToken) =>
        await service.RecheckAsync(id, cancellationToken) ? Results.NoContent() : Results.NotFound();

    private static async Task<IResult> RecheckDeliveryAsync(
        Guid id, AudiobookshelfPublishingService service, CancellationToken cancellationToken) =>
        await service.RecheckAsync(id, cancellationToken) ? Results.NoContent() : Results.NotFound();

    private static async Task<IResult> RetryDeliveryAttemptAsync(
        Guid id, DeliveryAttemptService service, CancellationToken cancellationToken,
        bool confirmPossibleDuplicate = false) =>
        await service.AdminRetryAsync(id, cancellationToken, confirmPossibleDuplicate) ? Results.NoContent() : Results.NotFound();

    internal static LibraryImportResponse ToLibraryImportResponse(LibraryImportView view) => new(
        view.Id,
        view.RequestId,
        view.WorkId,
        view.WorkTitle,
        view.OriginalFilename,
        view.Status.ToString(),
        view.ExternalBookId,
        view.FailureReason,
        view.CreatedAtUtc,
        view.CompletedAtUtc);

    internal static AudiobookshelfDeliveryResponse ToDeliveryResponse(AudiobookshelfDeliveryView view) => new(
        view.Id,
        view.RequestId,
        view.WorkId,
        view.WorkTitle,
        view.OriginalFilename,
        view.Status.ToString(),
        view.ExternalItemId,
        view.FailureReason,
        view.CreatedAtUtc,
        view.CompletedAtUtc);

    internal static DeliveryAttemptResponse ToDeliveryAttemptResponse(DeliveryAttemptView view) => new(
        view.Id,
        view.RequestId,
        view.WorkId,
        view.WorkTitle,
        view.RequesterDisplayName,
        view.RequesterEmail,
        view.ExternalBookId,
        view.Status.ToString(),
        view.AttemptNumber,
        view.FailureReason,
        view.CreatedAtUtc,
        view.CompletedAtUtc,
        view.DeliveryId,
        view.ConfirmationStatus.ToString(),
        view.ConfirmedAtUtc,
        view.IsLatest,
        view.CanRetry,
        view.NextAutomaticRetryAtUtc,
        view.AutomaticRetriesExhausted);
}
