namespace FamilyLibrarian.Domain.Communications;

/// <summary>
/// One provider's attempt to deliver an <see cref="OutboundCommunication"/>.
/// </summary>
/// <remarks>
/// A communication can end up with more than one of these — one per provider
/// that was enabled when it was dispatched — so that a future second provider
/// (e.g. Matrix) is just another row on the same schema, not a redesign.
/// </remarks>
public sealed class OutboundCommunicationDelivery
{
    private OutboundCommunicationDelivery()
    {
    }

    internal OutboundCommunicationDelivery(
        Guid outboundCommunicationId,
        string providerId,
        bool succeeded,
        string? error,
        DateTimeOffset attemptedAtUtc)
    {
        OutboundCommunicationId = outboundCommunicationId;
        ProviderId = providerId;
        Succeeded = succeeded;
        Error = string.IsNullOrWhiteSpace(error) ? null : error.Trim();
        AttemptedAtUtc = attemptedAtUtc;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public Guid OutboundCommunicationId { get; private set; }

    public OutboundCommunication Communication { get; private set; } = null!;

    public string ProviderId { get; private set; } = null!;

    public bool Succeeded { get; private set; }

    public string? Error { get; private set; }

    public DateTimeOffset AttemptedAtUtc { get; private set; }
}
