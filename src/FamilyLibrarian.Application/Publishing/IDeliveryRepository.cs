using FamilyLibrarian.Domain.Publishing;

namespace FamilyLibrarian.Application.Publishing;

public interface IDeliveryRepository
{
    Task<Delivery?> FindAsync(Guid id, CancellationToken cancellationToken);

    Task<Delivery?> FindByAssetIdAsync(Guid assetId, CancellationToken cancellationToken);

    Task<IReadOnlyList<DeliveryView>> ListRecentAsync(CancellationToken cancellationToken);

    void Add(Delivery delivery);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
