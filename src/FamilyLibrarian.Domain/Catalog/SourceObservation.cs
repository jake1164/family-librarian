namespace FamilyLibrarian.Domain.Catalog;

/// <summary>
/// A bounded, normalized debug snapshot of one successful provider observation of a Work. Never a raw provider payload or a
/// credential. Provider and external ids are historical text, not references, so removing a provider's configuration cannot
/// affect a snapshot, and retiring a Work keeps its snapshots.
/// </summary>
public sealed class SourceObservation
{
    /// <summary>The most UTF-8 bytes a stored snapshot may hold (64 KiB). A byte budget, not a character count.</summary>
    public const int MaximumSnapshotBytes = 64 * 1024;

    private SourceObservation()
    {
    }

    public SourceObservation(
        Guid workId,
        string providerId,
        string externalId,
        string contentHash,
        string snapshot,
        int schemaVersion,
        DateTimeOffset observedAtUtc)
    {
        if (workId == Guid.Empty)
        {
            throw new ArgumentException("A Work ID is required.", nameof(workId));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(schemaVersion, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot);
        if (System.Text.Encoding.UTF8.GetByteCount(snapshot) > MaximumSnapshotBytes)
        {
            throw new ArgumentException("A snapshot is at most 64 KiB of UTF-8.", nameof(snapshot));
        }

        WorkId = workId;
        ProviderId = Author.RequireText(providerId, nameof(providerId));
        ExternalId = Author.RequireText(externalId, nameof(externalId));
        ContentHash = Author.RequireText(contentHash, nameof(contentHash));
        Snapshot = snapshot;
        SchemaVersion = schemaVersion;
        ObservedAtUtc = observedAtUtc;
        LastSeenAtUtc = observedAtUtc;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public Guid WorkId { get; private set; }

    public string ProviderId { get; private set; } = null!;

    public string ExternalId { get; private set; } = null!;

    /// <summary>The first time exactly this content was seen.</summary>
    public DateTimeOffset ObservedAtUtc { get; private set; }

    /// <summary>The latest time an identical observation was seen again.</summary>
    public DateTimeOffset LastSeenAtUtc { get; private set; }

    public int SchemaVersion { get; private set; }

    public string ContentHash { get; private set; } = null!;

    /// <summary>The normalized snapshot, as JSON text stored in a jsonb column.</summary>
    public string Snapshot { get; private set; } = null!;

    public uint Version { get; private set; }

    /// <summary>An identical observation was seen again: only the last-seen time advances.</summary>
    public void SeenAgain(DateTimeOffset at)
    {
        if (at > LastSeenAtUtc)
        {
            LastSeenAtUtc = at;
        }
    }
}
