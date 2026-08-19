using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Domain.Acquisition;

/// <summary>
/// One physical file staged for a request, tracked through the security
/// pipeline's storage zones.
/// </summary>
/// <remarks>
/// A separate aggregate root from <see cref="AcquisitionJob"/>: an asset's
/// storage-state lifecycle (quarantine/processing/trusted/rejected/archived) is
/// independent of the job that produced it, and outlives it. <see cref="StoredFilename"/>
/// is always a generated, non-guessable name — never derived from
/// <see cref="OriginalFilename"/> — so a crafted upload name cannot influence a
/// filesystem path.
/// </remarks>
public sealed class MediaAsset
{
    private MediaAsset()
    {
    }

    public MediaAsset(
        Guid workId,
        Guid? editionId,
        RequestMediaType mediaType,
        string format,
        string originalFilename,
        string storedFilename,
        long sizeBytes,
        string sha256,
        string detectedMimeType,
        Guid associatedRequestFormatId,
        Guid? sourceAcquisitionCandidateId,
        DateTimeOffset createdAtUtc,
        Guid? bundleId = null,
        int? bundleSequence = null,
        int? bundleTrackCount = null)
    {
        if (workId == Guid.Empty)
        {
            throw new ArgumentException("A Work ID is required.", nameof(workId));
        }

        if (string.IsNullOrWhiteSpace(format))
        {
            throw new ArgumentException("A format is required.", nameof(format));
        }

        if (string.IsNullOrWhiteSpace(originalFilename))
        {
            throw new ArgumentException("An original filename is required.", nameof(originalFilename));
        }

        if (string.IsNullOrWhiteSpace(storedFilename))
        {
            throw new ArgumentException("A stored filename is required.", nameof(storedFilename));
        }

        if (sizeBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeBytes), sizeBytes, "Size must be positive.");
        }

        if (string.IsNullOrWhiteSpace(sha256))
        {
            throw new ArgumentException("A checksum is required.", nameof(sha256));
        }

        if (string.IsNullOrWhiteSpace(detectedMimeType))
        {
            throw new ArgumentException("A detected MIME type is required.", nameof(detectedMimeType));
        }

        if (associatedRequestFormatId == Guid.Empty)
        {
            throw new ArgumentException(
                "An associated request format is required.",
                nameof(associatedRequestFormatId));
        }

        Id = Guid.NewGuid();
        WorkId = workId;
        EditionId = editionId;
        MediaType = mediaType;
        Format = format.Trim();
        OriginalFilename = originalFilename.Trim();
        StoredFilename = storedFilename.Trim();
        SizeBytes = sizeBytes;
        Sha256 = sha256.Trim();
        DetectedMimeType = detectedMimeType.Trim();
        AssociatedRequestFormatId = associatedRequestFormatId;
        SourceAcquisitionCandidateId = sourceAcquisitionCandidateId;
        StorageState = MediaAssetStorageTransitions.InitialState;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
        BundleId = bundleId;
        BundleSequence = bundleSequence;
        BundleTrackCount = bundleTrackCount;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public Guid WorkId { get; private set; }

    public Guid? EditionId { get; private set; }

    public RequestMediaType MediaType { get; private set; }

    public string Format { get; private set; } = null!;

    /// <summary>The filename as uploaded/reported. Untrusted; display-only.</summary>
    public string OriginalFilename { get; private set; } = null!;

    /// <summary>The generated on-disk filename within its storage zone.</summary>
    public string StoredFilename { get; private set; } = null!;

    public long SizeBytes { get; private set; }

    public string Sha256 { get; private set; } = null!;

    public string DetectedMimeType { get; private set; } = null!;

    /// <summary>The RequestFormat this asset was staged to fulfill.</summary>
    public Guid AssociatedRequestFormatId { get; private set; }

    public Guid? SourceAcquisitionCandidateId { get; private set; }

    /// <summary>
    /// Groups sibling tracks of one multi-file acquisition (e.g. a
    /// chaptered Gutenberg audiobook) that must be published to their
    /// destination together. <c>null</c> for every ordinary single-file
    /// asset.
    /// </summary>
    public Guid? BundleId { get; private set; }

    /// <summary>1-based ordering of this track within its bundle.</summary>
    public int? BundleSequence { get; private set; }

    /// <summary>How many sibling tracks make up the complete bundle.</summary>
    public int? BundleTrackCount { get; private set; }

    public MediaAssetStorageState StorageState { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public uint Version { get; private set; }

    /// <exception cref="InvalidMediaAssetStorageTransitionException">
    /// The move is not in <see cref="MediaAssetStorageTransitions"/>.
    /// </exception>
    public void TransitionStorageState(MediaAssetStorageState to, DateTimeOffset atUtc)
    {
        if (!MediaAssetStorageTransitions.IsAllowed(StorageState, to))
        {
            throw new InvalidMediaAssetStorageTransitionException(StorageState, to);
        }

        StorageState = to;
        UpdatedAtUtc = atUtc;
    }
}
