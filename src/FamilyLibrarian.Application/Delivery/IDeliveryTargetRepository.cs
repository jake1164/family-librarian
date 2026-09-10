using FamilyLibrarian.Domain.Delivery;

namespace FamilyLibrarian.Application.Delivery;

public interface IDeliveryTargetRepository
{
    Task<DeliveryTarget?> FindAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<DeliveryTarget>> ListForUserAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Every delivery target across every user -- the admin accounts page's Kindle column.</summary>
    Task<IReadOnlyList<DeliveryTarget>> ListAllAsync(CancellationToken cancellationToken);

    void Add(DeliveryTarget target);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
