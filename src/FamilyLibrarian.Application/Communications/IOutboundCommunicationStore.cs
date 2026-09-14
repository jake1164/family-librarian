using FamilyLibrarian.Domain.Communications;

namespace FamilyLibrarian.Application.Communications;

public interface IOutboundCommunicationStore
{
    Task EnqueueAsync(OutboundCommunication communication, CancellationToken cancellationToken);

    /// <summary>The oldest not-yet-processed communications, oldest first, up to <paramref name="maxCount"/>.</summary>
    Task<IReadOnlyList<OutboundCommunication>> GetUnprocessedBatchAsync(int maxCount, CancellationToken cancellationToken);

    /// <summary>
    /// The newest communication of <paramref name="communicationType"/> ever
    /// queued for <paramref name="recipientUserId"/> -- the Matrix inbound
    /// router's (COMM-1 §D) "most recent outstanding promptable" lookup.
    /// Dispatch/processed state is irrelevant here: a reply can arrive over
    /// Matrix before or after the same ask's SMTP copy is even sent.
    /// </summary>
    Task<OutboundCommunication?> FindMostRecentByTypeAsync(
        Guid recipientUserId, string communicationType, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
