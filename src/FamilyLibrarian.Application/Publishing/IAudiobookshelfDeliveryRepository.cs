using FamilyLibrarian.Domain.Publishing;

namespace FamilyLibrarian.Application.Publishing;

public interface IAudiobookshelfDeliveryRepository
{
    Task<AudiobookshelfDelivery?> FindAsync(Guid id, CancellationToken cancellationToken);

    Task<AudiobookshelfDelivery?> FindByAssetIdAsync(Guid assetId, CancellationToken cancellationToken);

    Task<AudiobookshelfDelivery?> FindByBundleIdAsync(Guid bundleId, CancellationToken cancellationToken);

    Task<IReadOnlyList<AudiobookshelfDeliveryView>> ListRecentAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<Guid>> ListAwaitingVerificationIdsAsync(CancellationToken cancellationToken);

    void Add(AudiobookshelfDelivery delivery);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
