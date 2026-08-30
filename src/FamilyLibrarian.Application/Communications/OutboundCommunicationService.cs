using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Domain.Communications;

namespace FamilyLibrarian.Application.Communications;

/// <summary>
/// The seam FL features call to queue a communication without knowing which
/// provider, if any, will eventually deliver it.
/// </summary>
public sealed class OutboundCommunicationService(IOutboundCommunicationStore store, IClock clock)
{
    public async Task EnqueueAsync(
        Guid recipientUserId,
        string communicationType,
        string body,
        string? subject,
        string? relatedEntityType,
        Guid? relatedEntityId,
        CancellationToken cancellationToken)
    {
        var communication = new OutboundCommunication(
            recipientUserId,
            communicationType,
            body,
            subject,
            relatedEntityType,
            relatedEntityId,
            link: null,
            clock.UtcNow);

        await store.EnqueueAsync(communication, cancellationToken);
        await store.SaveChangesAsync(cancellationToken);
    }
}
