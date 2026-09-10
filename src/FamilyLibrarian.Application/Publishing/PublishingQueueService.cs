using FamilyLibrarian.Application.Delivery;

namespace FamilyLibrarian.Application.Publishing;

/// <summary>
/// Read-only composition of the admin Library Publishing queue: recent CWA
/// imports, Audiobookshelf deliveries, and Kindle delivery attempts.
/// </summary>
public sealed class PublishingQueueService(
    ILibraryImportRepository libraryImports,
    IAudiobookshelfDeliveryRepository deliveries,
    IDeliveryAttemptRepository deliveryAttempts)
{
    public async Task<PublishingQueueSnapshot> ListAsync(CancellationToken cancellationToken)
    {
        var imports = await libraryImports.ListRecentAsync(cancellationToken);
        var deliveryList = await deliveries.ListRecentAsync(cancellationToken);
        var kindleAttempts = await deliveryAttempts.ListRecentAsync(cancellationToken);
        return new PublishingQueueSnapshot(imports, deliveryList, kindleAttempts);
    }
}

public sealed record PublishingQueueSnapshot(
    IReadOnlyList<LibraryImportView> LibraryImports,
    IReadOnlyList<AudiobookshelfDeliveryView> Deliveries,
    IReadOnlyList<DeliveryAttemptView> DeliveryAttempts);
