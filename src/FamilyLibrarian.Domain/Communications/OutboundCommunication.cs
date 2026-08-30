namespace FamilyLibrarian.Domain.Communications;

/// <summary>
/// A single normalized message FL wants delivered to a user, independent of
/// which transport (SMTP, Matrix, or a future provider) ends up sending it.
/// </summary>
/// <remarks>
/// This is the durable outbox row: a feature enqueues one of these in the same
/// transaction as the business change it describes, and a background
/// dispatcher attempts delivery afterwards, recording one
/// <see cref="OutboundCommunicationDelivery"/> per provider it tried. A
/// provider failure therefore can never roll back the change that queued the
/// communication.
/// </remarks>
public sealed class OutboundCommunication
{
    private readonly List<OutboundCommunicationDelivery> _deliveries = [];

    private OutboundCommunication()
    {
    }

    public OutboundCommunication(
        Guid recipientUserId,
        string communicationType,
        string body,
        string? subject,
        string? relatedEntityType,
        Guid? relatedEntityId,
        string? link,
        DateTimeOffset createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(communicationType))
        {
            throw new ArgumentException("A communication type is required.", nameof(communicationType));
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            throw new ArgumentException("A message body is required.", nameof(body));
        }

        RecipientUserId = recipientUserId;
        CommunicationType = communicationType.Trim();
        Body = body.Trim();
        Subject = string.IsNullOrWhiteSpace(subject) ? null : subject.Trim();
        RelatedEntityType = string.IsNullOrWhiteSpace(relatedEntityType) ? null : relatedEntityType.Trim();
        RelatedEntityId = relatedEntityId;
        Link = string.IsNullOrWhiteSpace(link) ? null : link.Trim();
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public Guid RecipientUserId { get; private set; }

    /// <summary>A free-form key describing what happened, e.g. <c>"request.status_changed"</c>.</summary>
    public string CommunicationType { get; private set; } = null!;

    public string? Subject { get; private set; }

    public string Body { get; private set; } = null!;

    public string? RelatedEntityType { get; private set; }

    public Guid? RelatedEntityId { get; private set; }

    public string? Link { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>
    /// Set once the dispatcher has attempted every provider that was enabled
    /// for this pass. <see langword="null"/> means still queued.
    /// </summary>
    public DateTimeOffset? ProcessedAtUtc { get; private set; }

    public IReadOnlyCollection<OutboundCommunicationDelivery> Deliveries => _deliveries;

    public void RecordDelivery(string providerId, bool succeeded, string? error, DateTimeOffset attemptedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            throw new ArgumentException("A provider id is required.", nameof(providerId));
        }

        _deliveries.Add(new OutboundCommunicationDelivery(Id, providerId, succeeded, error, attemptedAtUtc));
    }

    public void MarkProcessed(DateTimeOffset processedAtUtc) => ProcessedAtUtc = processedAtUtc;
}
