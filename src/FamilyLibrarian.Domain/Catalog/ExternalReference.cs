namespace FamilyLibrarian.Domain.Catalog;

public sealed class ExternalReference
{
    private ExternalReference()
    {
    }

    public ExternalReference(
        string providerId,
        ExternalReferenceEntityType entityType,
        Guid entityId,
        string externalId,
        DateTimeOffset observedAtUtc)
    {
        ProviderId = Author.RequireText(providerId, nameof(providerId));
        if (entityId == Guid.Empty)
        {
            throw new ArgumentException("An entity ID is required.", nameof(entityId));
        }

        EntityType = entityType;
        EntityId = entityId;
        ExternalId = Author.RequireText(externalId, nameof(externalId));
        ObservedAtUtc = observedAtUtc;
        CreatedAtUtc = observedAtUtc;
        UpdatedAtUtc = observedAtUtc;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public string ProviderId { get; private set; } = null!;

    public ExternalReferenceEntityType EntityType { get; private set; }

    public Guid EntityId { get; private set; }

    public string ExternalId { get; private set; } = null!;

    public string? SourceUrl { get; private set; }

    public DateTimeOffset ObservedAtUtc { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public uint Version { get; private set; }
}
