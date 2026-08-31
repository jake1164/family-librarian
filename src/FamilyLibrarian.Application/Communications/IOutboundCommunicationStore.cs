using FamilyLibrarian.Domain.Communications;

namespace FamilyLibrarian.Application.Communications;

public interface IOutboundCommunicationStore
{
    Task EnqueueAsync(OutboundCommunication communication, CancellationToken cancellationToken);

    /// <summary>The oldest not-yet-processed communications, oldest first, up to <paramref name="maxCount"/>.</summary>
    Task<IReadOnlyList<OutboundCommunication>> GetUnprocessedBatchAsync(int maxCount, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
