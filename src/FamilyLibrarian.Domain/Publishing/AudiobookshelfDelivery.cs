namespace FamilyLibrarian.Domain.Publishing;

/// <summary>
/// One attempt to publish an approved audiobook <c>MediaAsset</c> to
/// Audiobookshelf.
/// </summary>
/// <remarks>
/// Family Librarian retains the trusted source asset regardless of this
/// record's outcome. Every transition here is coordinator-driven (see
/// <c>AudiobookshelfPublishingService</c>), never automatic/background.
/// </remarks>
public sealed class AudiobookshelfDelivery
{
    private AudiobookshelfDelivery()
    {
    }

    public AudiobookshelfDelivery(Guid assetId, DateTimeOffset createdAtUtc)
    {
        if (assetId == Guid.Empty)
        {
            throw new ArgumentException("An asset ID is required.", nameof(assetId));
        }

        Id = Guid.NewGuid();
        AssetId = assetId;
        Status = AudiobookshelfDeliveryStatus.Uploading;
        CreatedAtUtc = createdAtUtc;
    }

    /// <summary>One delivery attempt for every track of a multi-file bundle together.</summary>
    public static AudiobookshelfDelivery ForBundle(Guid bundleId, DateTimeOffset createdAtUtc)
    {
        if (bundleId == Guid.Empty)
        {
            throw new ArgumentException("A bundle ID is required.", nameof(bundleId));
        }

        return new AudiobookshelfDelivery
        {
            Id = Guid.NewGuid(),
            BundleId = bundleId,
            Status = AudiobookshelfDeliveryStatus.Uploading,
            CreatedAtUtc = createdAtUtc
        };
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public Guid? AssetId { get; private set; }

    /// <summary>Set instead of <see cref="AssetId"/> for a multi-file bundle delivery.</summary>
    public Guid? BundleId { get; private set; }

    public AudiobookshelfDeliveryStatus Status { get; private set; }

    /// <summary>The Audiobookshelf library item id (e.g. <c>li_...</c>), once known.</summary>
    public string? ExternalItemId { get; private set; }

    public string? FailureReason { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public uint Version { get; private set; }

    public void MarkVerifying()
    {
        Status = AudiobookshelfDeliveryStatus.Verifying;
        FailureReason = null;
    }

    public void MarkDelivered(string externalItemId, DateTimeOffset atUtc)
    {
        if (string.IsNullOrWhiteSpace(externalItemId))
        {
            throw new ArgumentException("An external item ID is required.", nameof(externalItemId));
        }

        Status = AudiobookshelfDeliveryStatus.Delivered;
        ExternalItemId = externalItemId.Trim();
        FailureReason = null;
        CompletedAtUtc = atUtc;
    }

    public void MarkFailed(string reason, DateTimeOffset atUtc)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A failure reason is required.", nameof(reason));
        }

        Status = AudiobookshelfDeliveryStatus.Failed;
        FailureReason = Truncate(reason, 2000);
        CompletedAtUtc = atUtc;
    }

    /// <summary>Used by a Recheck that retries the whole upload after a prior failure.</summary>
    public void ResetForRetry()
    {
        Status = AudiobookshelfDeliveryStatus.Uploading;
        FailureReason = null;
        CompletedAtUtc = null;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
