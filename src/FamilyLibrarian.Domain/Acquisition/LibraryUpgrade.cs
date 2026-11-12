namespace FamilyLibrarian.Domain.Acquisition;

public enum LibraryUpgradeState
{
    /// <summary>A better copy was found for a delivered copy that looks incomplete. Nothing has changed yet.</summary>
    Proposed,

    /// <summary>The better copy is being fetched and checked.</summary>
    Acquiring,

    /// <summary>The better copy passed its checks and is being delivered next to the old one.</summary>
    Publishing,

    /// <summary>The better copy is confirmed in the destination; the old one can be retracted.</summary>
    Verified,

    /// <summary>The old copy was retracted and the better one is the only copy.</summary>
    Completed,

    /// <summary>An administrator declined it. The same candidate is not proposed again.</summary>
    Dismissed,

    /// <summary>The attempt stopped; both copies were left as they were (or the old copy remains if retraction failed).</summary>
    Failed
}

/// <summary>
/// A better copy of a book that was already delivered, and what has been done about it (A6-UPGRADE-1 U-2). At most one is open per
/// request format. It records ids and codes only, never file names or text.
/// </summary>
public sealed class LibraryUpgrade
{
    public const int MaxReasonsLength = 512;

    private LibraryUpgrade()
    {
    }

    public LibraryUpgrade(
        Guid requestId, Guid requestFormatId, Guid deliveredAssetId, string providerId, string candidateReference,
        string? candidateFingerprint, IEnumerable<string> reasons, DateTimeOffset createdAtUtc)
    {
        if (requestId == Guid.Empty || requestFormatId == Guid.Empty || deliveredAssetId == Guid.Empty)
            throw new ArgumentException("Request, format and delivered asset ids are required.");
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateReference);
        var joined = string.Join(',', reasons ?? []);
        RequestId = requestId;
        RequestFormatId = requestFormatId;
        DeliveredAssetId = deliveredAssetId;
        ProviderId = providerId.Trim();
        CandidateReference = candidateReference.Trim();
        CandidateFingerprint = candidateFingerprint;
        Reasons = joined.Length > MaxReasonsLength ? joined[..MaxReasonsLength] : joined;
        State = LibraryUpgradeState.Proposed;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public Guid RequestId { get; private set; }

    public Guid RequestFormatId { get; private set; }

    /// <summary>The copy now in the library that this would replace.</summary>
    public Guid DeliveredAssetId { get; private set; }

    public string ProviderId { get; private set; } = string.Empty;

    public string CandidateReference { get; private set; } = string.Empty;

    public string? CandidateFingerprint { get; private set; }

    /// <summary>Comma-separated evidence codes that made the delivered copy worth replacing.</summary>
    public string Reasons { get; private set; } = string.Empty;

    public LibraryUpgradeState State { get; private set; }

    /// <summary>The better copy's asset, once it exists.</summary>
    public Guid? NewAssetId { get; private set; }

    public string? FailureReason { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public uint Version { get; private set; }

    public bool IsOpen => State is LibraryUpgradeState.Proposed or LibraryUpgradeState.Acquiring
        or LibraryUpgradeState.Publishing or LibraryUpgradeState.Verified;

    public void Dismiss(DateTimeOffset atUtc)
    {
        if (State != LibraryUpgradeState.Proposed)
            throw new InvalidOperationException("Only a proposal that has not started can be dismissed.");
        State = LibraryUpgradeState.Dismissed;
        UpdatedAtUtc = atUtc;
        CompletedAtUtc = atUtc;
    }

    public void MarkAcquiring(DateTimeOffset atUtc)
    {
        if (State != LibraryUpgradeState.Proposed)
            throw new InvalidOperationException("Only a proposal can start acquiring.");
        State = LibraryUpgradeState.Acquiring;
        UpdatedAtUtc = atUtc;
    }

    public void MarkPublishing(Guid newAssetId, DateTimeOffset atUtc)
    {
        if (State != LibraryUpgradeState.Acquiring)
            throw new InvalidOperationException("Only an upgrade that is acquiring can start publishing.");
        State = LibraryUpgradeState.Publishing;
        NewAssetId = newAssetId;
        UpdatedAtUtc = atUtc;
    }

    public void MarkVerified(DateTimeOffset atUtc)
    {
        if (State != LibraryUpgradeState.Publishing)
            throw new InvalidOperationException("Only an upgrade that is publishing can be verified.");
        State = LibraryUpgradeState.Verified;
        UpdatedAtUtc = atUtc;
    }

    public void MarkCompleted(DateTimeOffset atUtc)
    {
        if (State != LibraryUpgradeState.Verified)
            throw new InvalidOperationException("Only a verified upgrade can complete.");
        State = LibraryUpgradeState.Completed;
        UpdatedAtUtc = atUtc;
        CompletedAtUtc = atUtc;
    }

    public void MarkFailed(string reason, DateTimeOffset atUtc)
    {
        if (!IsOpen) return;
        State = LibraryUpgradeState.Failed;
        FailureReason = reason.Length > 512 ? reason[..512] : reason;
        UpdatedAtUtc = atUtc;
        CompletedAtUtc = atUtc;
    }
}
