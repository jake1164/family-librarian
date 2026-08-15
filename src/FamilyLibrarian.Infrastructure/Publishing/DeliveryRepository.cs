using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Publishing;
using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.Publishing;

public sealed class DeliveryRepository(AppDbContext database) : IDeliveryRepository
{
    public Task<Delivery?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        database.Deliveries.FirstOrDefaultAsync(delivery => delivery.Id == id, cancellationToken);

    public Task<Delivery?> FindByAssetIdAsync(Guid assetId, CancellationToken cancellationToken) =>
        database.Deliveries.FirstOrDefaultAsync(delivery => delivery.AssetId == assetId, cancellationToken);

    public async Task<IReadOnlyList<DeliveryView>> ListRecentAsync(CancellationToken cancellationToken)
    {
        var query =
            from delivery in database.Deliveries
            join asset in database.MediaAssets on delivery.AssetId equals asset.Id
            join format in database.RequestFormats on asset.AssociatedRequestFormatId equals format.Id
            join work in database.Works on asset.WorkId equals work.Id
            orderby delivery.CreatedAtUtc descending
            select new DeliveryView(
                delivery.Id,
                delivery.AssetId,
                format.RequestId,
                asset.WorkId,
                work.CanonicalTitle,
                asset.OriginalFilename,
                delivery.Status,
                delivery.ExternalItemId,
                delivery.FailureReason,
                delivery.CreatedAtUtc,
                delivery.CompletedAtUtc);

        return await query.ToArrayAsync(cancellationToken);
    }

    public void Add(Delivery delivery) => database.Deliveries.Add(delivery);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => database.SaveChangesAsync(cancellationToken);
}
