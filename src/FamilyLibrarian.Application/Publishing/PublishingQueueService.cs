namespace FamilyLibrarian.Application.Publishing;

/// <summary>Read-only composition of the admin Library Publishing queue: recent CWA imports and Audiobookshelf deliveries.</summary>
public sealed class PublishingQueueService(ILibraryImportRepository libraryImports, IDeliveryRepository deliveries)
{
    public async Task<PublishingQueueSnapshot> ListAsync(CancellationToken cancellationToken)
    {
        var imports = await libraryImports.ListRecentAsync(cancellationToken);
        var deliveryList = await deliveries.ListRecentAsync(cancellationToken);
        return new PublishingQueueSnapshot(imports, deliveryList);
    }
}

public sealed record PublishingQueueSnapshot(
    IReadOnlyList<LibraryImportView> LibraryImports,
    IReadOnlyList<DeliveryView> Deliveries);
