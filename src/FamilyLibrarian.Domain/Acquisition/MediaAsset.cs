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
        int? bundleTrackCount = null,
        bool identityPreConfirmed = false,
        AssetOrigin origin = AssetOrigin.Unknown,
        Guid? originUserId = null)
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

        if (!Enum.IsDefined(origin) || origin == AssetOrigin.Legacy)
        {
            throw new ArgumentOutOfRangeException(nameof(origin), origin, "A new asset needs a concrete origin (or Unknown); Legacy is reserved for rows that predate it.");
        }

        if (origin == AssetOrigin.ManualUpload && originUserId is null)
        {
            throw new ArgumentException("A manual upload records the authenticated user who made it.", nameof(originUserId));
        }

        if (origin != AssetOrigin.ManualUpload && originUserId is not null)
        {
            throw new ArgumentException("Only a manual upload carries a user; other origins must not claim one.", nameof(originUserId));
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
        IdentityPreConfirmed = identityPreConfirmed;
        Origin = origin;
        OriginUserId = originUserId;
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

    /// <summary>
    /// The identity verifier's explanation of what it compared and why it
    /// didn't match, set alongside a transition into <see cref="MediaAssetStorageState.Unmatched"/>
    /// and cleared once the asset leaves that state. Without this, a librarian
    /// facing "identity review required" has no way to tell a cosmetic title
    /// difference from genuinely the wrong book.
    /// </summary>
    public string? IdentityMismatchReason { get; private set; }

    /// <summary>
    /// Whether the title/author identity was already confirmed by structured
    /// evidence (ISBN, or an exact title+author match) <em>before</em> this
    /// file was fetched -- true for every built-in-provider acquisition
    /// (Gutenberg/LibriVox verify title/author with their own matcher before
    /// ever offering a candidate) and for a manual upload (an admin chose the
    /// specific file). False only for an admin-registered external provider's
    /// candidate that needed <c>confirmLowConfidenceMatch</c> to be fetched at
    /// all -- title/author alone, no stronger evidence.
    /// </summary>
    /// <remarks>
    /// <b>Attestation is explicit (alpha.6 M1-4b).</b> The in-code default is <see langword="false"/>:
    /// a creation path that does not say otherwise attests nothing, so a future path cannot
    /// silently vouch for a file it never examined. Every creation site passes the value
    /// itself. The database default for rows created before this change stays <c>true</c>
    /// so existing history is untouched. This flag is a statement about the <em>source</em>;
    /// it is never content verification (see <c>ArtifactEvidence</c>).
    /// <para>
    /// The application layer's identity verifier only covers EPUB/KEPUB
    /// content today (a known, documented scope limit, not a secret gap).
    /// For every other format, "no verifier supports this file"
    /// used to be treated as a pass unconditionally -- which was safe for a
    /// pre-confirmed match (nothing left to prove) but meant a knowingly
    /// unconfirmed MOBI/AZW3 from an external provider sailed straight to the
    /// library with nothing having actually checked it. This field lets the
    /// verification dispatcher tell the two cases apart instead of treating
    /// "cannot check" as "checked and fine" for the one case where that
    /// mattered.
    /// </para>
    /// </remarks>
    public bool IdentityPreConfirmed { get; private set; }

    /// <summary>
    /// Where this asset came from, set once at creation by the creating code path and never reassigned (there is
    /// deliberately no method that changes it, and the database rejects an update). Rows that predate it were marked
    /// <see cref="AssetOrigin.Legacy"/> once, explicitly, by the migration that introduced the column. The in-code default
    /// is <see cref="AssetOrigin.Unknown"/> (fails closed): nothing that is merely unset can become trusted legacy
    /// provenance, and the database has no default to fall back on either.
    /// </summary>
    public AssetOrigin Origin { get; private set; } = AssetOrigin.Unknown;

    /// <summary>The authenticated administrator behind a <see cref="AssetOrigin.ManualUpload"/>; otherwise <see langword="null"/>.</summary>
    public Guid? OriginUserId { get; private set; }

    /// <summary>
    /// A stable <c>identity.hold.*</c> reason code while the asset is held as <see cref="MediaAssetStorageState.Unmatched"/>,
    /// cleared when the hold ends. Null for a hold recorded before codes existed.
    /// </summary>
    public string? IdentityHoldCode { get; private set; }

    /// <summary>
    /// When the automatic search judged this copy unusable and went on to another one, or null. Such a held copy no
    /// longer needs a decision; it is kept, releasable, only until its request format is settled.
    /// </summary>
    public DateTimeOffset? RuledOutAtUtc { get; private set; }

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

    /// <summary>
    /// Records why an identity check held this asset as <see cref="MediaAssetStorageState.Unmatched"/>,
    /// or clears the reason once it leaves that state (a stale reason from a
    /// prior hold must never linger and be read as current).
    /// </summary>
    public void SetIdentityMismatchReason(string? reason) => IdentityMismatchReason = reason;

    /// <summary>Records the stable code for the current identity hold, or clears it with the reason.</summary>
    public void SetIdentityHoldCode(string? code) => IdentityHoldCode = string.IsNullOrWhiteSpace(code) ? null : code.Trim();

    /// <summary>Records that the automatic search ruled this copy out; the first time stands.</summary>
    public void MarkRuledOut(DateTimeOffset atUtc) => RuledOutAtUtc ??= atUtc;

    /// <summary>
    /// Why the most recent security evaluation was interrupted and the asset
    /// returned to <see cref="MediaAssetStorageState.Quarantine"/>. Cleared when
    /// the next evaluation begins so a stale failure is never read as current.
    /// Without it a librarian sees "Retry required" with no cause short of
    /// digging through the audit log.
    /// </summary>
    public string? ScanFailureReason { get; private set; }

    public void SetScanFailureReason(string? reason) => ScanFailureReason = reason;
}
