namespace FamilyLibrarian.Domain.Catalog;

/// <summary>
/// One source's attributed statement about a Work fact, kept next to every competing statement instead of overwriting it. A
/// claim is self-sufficient evidence: it does not reference an observation snapshot, so pruning snapshots can never remove a
/// claim's source, and its scope is text so no claim depends on rows retirement deletes.
/// </summary>
public sealed class MetadataClaim
{
    /// <summary>The most UTF-8 bytes a stored claim value may hold (16 KiB). A byte budget, not a character count: multibyte text is longer than it looks.</summary>
    public const int MaximumValueBytes = 16 * 1024;

    /// <summary>The most characters a claim scope may hold.</summary>
    public const int MaximumScopeLength = 256;

    private MetadataClaim()
    {
    }

    public MetadataClaim(
        Guid workId,
        MetadataClaimField field,
        string scopeKey,
        string providerId,
        string sourceEntityId,
        string valueJson,
        string valueHash,
        DateTimeOffset seenAtUtc)
    {
        if (workId == Guid.Empty)
        {
            throw new ArgumentException("A Work ID is required.", nameof(workId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(valueJson);
        if (System.Text.Encoding.UTF8.GetByteCount(valueJson) > MaximumValueBytes)
        {
            throw new ArgumentException("A claim value is at most 16 KiB of UTF-8.", nameof(valueJson));
        }

        WorkId = workId;
        Field = field;
        ScopeKey = Author.RequireText(scopeKey, nameof(scopeKey));
        if (ScopeKey.Length > MaximumScopeLength)
        {
            throw new ArgumentException("A claim scope is at most 256 characters.", nameof(scopeKey));
        }

        ProviderId = Author.RequireText(providerId, nameof(providerId));
        SourceEntityId = Author.RequireText(sourceEntityId, nameof(sourceEntityId));
        ValueJson = valueJson;
        ValueHash = Author.RequireText(valueHash, nameof(valueHash));
        FirstSeenAtUtc = seenAtUtc;
        LastSeenAtUtc = seenAtUtc;
        IsCurrent = true;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public Guid WorkId { get; private set; }

    public MetadataClaimField Field { get; private set; }

    /// <summary>A series FL id, an ISBN-13, or <c>unbound:&lt;label&gt;</c>. Text, never a foreign key.</summary>
    public string ScopeKey { get; private set; } = null!;

    public string ProviderId { get; private set; } = null!;

    public string SourceEntityId { get; private set; } = null!;

    public string ValueJson { get; private set; } = null!;

    public string ValueHash { get; private set; } = null!;

    public DateTimeOffset FirstSeenAtUtc { get; private set; }

    public DateTimeOffset LastSeenAtUtc { get; private set; }

    /// <summary>The source's latest statement for this scope. A superseded claim stays as history.</summary>
    public bool IsCurrent { get; private set; }

    /// <summary>The value the catalog currently shows for this fact. At most one per Work, field and scope.</summary>
    public bool IsSelected { get; private set; }

    public uint Version { get; private set; }

    public void SeenAgain(DateTimeOffset at)
    {
        if (at > LastSeenAtUtc)
        {
            LastSeenAtUtc = at;
        }
    }

    /// <summary>The same source stated a different value later: this one becomes history.</summary>
    public void Supersede() => IsCurrent = false;

    /// <summary>The same source returned to this value: it is its latest statement again.</summary>
    public void Reinstate(DateTimeOffset at)
    {
        IsCurrent = true;
        SeenAgain(at);
    }

    public void Select() => IsSelected = true;

    public void Deselect() => IsSelected = false;
}
