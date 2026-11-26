using FamilyLibrarian.Domain.Communications;

namespace FamilyLibrarian.Application.Communications;

public interface IOutboundCommunicationStore
{
    Task EnqueueAsync(OutboundCommunication communication, CancellationToken cancellationToken);

    /// <summary>
    /// The oldest not-yet-processed communications, oldest first, up to <paramref name="maxCount"/>,
    /// leaving out any addressed to <paramref name="heldRecipientUserIds"/>.
    /// </summary>
    Task<IReadOnlyList<OutboundCommunication>> GetUnprocessedBatchAsync(
        int maxCount, IReadOnlyCollection<Guid> heldRecipientUserIds, CancellationToken cancellationToken);

    /// <summary>
    /// The newest communication of <paramref name="communicationType"/> already
    /// sent to <paramref name="recipientUserId"/> -- the Matrix inbound
    /// router's (COMM-1 §D) "most recent outstanding promptable" lookup. One
    /// still queued (held for quiet hours, say) is excluded, because a reply
    /// cannot be answering a message its recipient has not received.
    /// </summary>
    Task<OutboundCommunication?> FindMostRecentByTypeAsync(
        Guid recipientUserId, string communicationType, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
