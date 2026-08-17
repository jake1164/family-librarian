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
    }

    private static async Task<IResult> GetPublishingQueueAsync(
        PublishingQueueService service, CancellationToken cancellationToken)
    {
        var snapshot = await service.ListAsync(cancellationToken);
        return Results.Ok(new PublishingQueueResponse(
            snapshot.LibraryImports.Select(ToLibraryImportResponse).ToArray(),
            snapshot.Deliveries.Select(ToDeliveryResponse).ToArray()));
    }

    private static async Task<IResult> RecheckLibraryImportAsync(
        Guid id, CwaPublishingService service, CancellationToken cancellationToken) =>
        await service.RecheckAsync(id, cancellationToken) ? Results.NoContent() : Results.NotFound();

    private static async Task<IResult> RecheckDeliveryAsync(
        Guid id, AudiobookshelfPublishingService service, CancellationToken cancellationToken) =>
        await service.RecheckAsync(id, cancellationToken) ? Results.NoContent() : Results.NotFound();

    private static LibraryImportResponse ToLibraryImportResponse(LibraryImportView view) => new(
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

    private static DeliveryResponse ToDeliveryResponse(DeliveryView view) => new(
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
}
