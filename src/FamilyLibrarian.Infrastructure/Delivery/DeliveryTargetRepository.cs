using FamilyLibrarian.Application.Delivery;
using FamilyLibrarian.Domain.Delivery;
using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.Delivery;

public sealed class DeliveryTargetRepository(AppDbContext database) : IDeliveryTargetRepository
{
    public Task<DeliveryTarget?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        database.DeliveryTargets.FirstOrDefaultAsync(target => target.Id == id, cancellationToken);

    public async Task<IReadOnlyList<DeliveryTarget>> ListForUserAsync(Guid userId, CancellationToken cancellationToken) =>
        await database.DeliveryTargets
            .Where(target => target.UserId == userId)
            .OrderBy(target => target.CreatedAtUtc)
            .ToArrayAsync(cancellationToken);

    public async Task<IReadOnlyList<DeliveryTarget>> ListAllAsync(CancellationToken cancellationToken) =>
        await database.DeliveryTargets
            .OrderBy(target => target.UserId)
            .ToArrayAsync(cancellationToken);

    public void Add(DeliveryTarget target) => database.DeliveryTargets.Add(target);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => database.SaveChangesAsync(cancellationToken);
}
