namespace FamilyLibrarian.Domain.Publishing;

/// <summary>
/// One attempt to publish an approved ebook <c>MediaAsset</c> into the
/// configured CWA library.
/// </summary>
/// <remarks>
/// Family Librarian retains the trusted source asset regardless of this
/// record's outcome — CWA is a distribution destination, never the system of
/// record. Every transition here is coordinator-driven (see
/// <c>CwaPublishingService</c>), never automatic/background: <see cref="Status"/>
/// only moves on an explicit publish or recheck call.
/// </remarks>
public sealed class LibraryImport
{
    private LibraryImport()
    {
    }

    public LibraryImport(Guid assetId, DateTimeOffset createdAtUtc)
    {
        if (assetId == Guid.Empty)
        {
            throw new ArgumentException("An asset ID is required.", nameof(assetId));
        }

        Id = Guid.NewGuid();
        AssetId = assetId;
        Status = LibraryImportStatus.Publishing;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public Guid AssetId { get; private set; }

    public LibraryImportStatus Status { get; private set; }

    /// <summary>The Calibre book id, populated only once found via the OPDS catalog.</summary>
    public string? ExternalBookId { get; private set; }

    /// <summary>
    /// The exact filename delivered to CWA's ingest folder for this attempt --
    /// includes a random component (see <c>PublishingFilenames.BuildTargetFilename</c>),
    /// so it must be recorded rather than recomputed. A recheck that needs to
    /// re-signal the same already-delivered file to CWA's watcher (see
    /// <c>ICwaIngestTransport.TouchAsync</c>) targets this name, never a freshly
    /// generated one -- recomputing would silently touch a file that doesn't exist.
    /// </summary>
    public string? TargetFilename { get; private set; }

    public string? FailureReason { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public uint Version { get; private set; }

    public void MarkAwaitingVerification(string targetFilename)
    {
        if (string.IsNullOrWhiteSpace(targetFilename))
        {
            throw new ArgumentException("A target filename is required.", nameof(targetFilename));
        }

        Status = LibraryImportStatus.AwaitingVerification;
        TargetFilename = targetFilename;
        FailureReason = null;
    }

    public void MarkAvailable(string externalBookId, DateTimeOffset atUtc)
    {
        if (string.IsNullOrWhiteSpace(externalBookId))
        {
            throw new ArgumentException("An external book ID is required.", nameof(externalBookId));
        }

        Status = LibraryImportStatus.Available;
        ExternalBookId = externalBookId.Trim();
        FailureReason = null;
        CompletedAtUtc = atUtc;
    }

    public void MarkFailed(string reason, DateTimeOffset atUtc)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A failure reason is required.", nameof(reason));
        }

        Status = LibraryImportStatus.Failed;
        FailureReason = Truncate(reason, 2000);
        CompletedAtUtc = atUtc;
    }

    /// <summary>Used by a Recheck that retries the whole handoff after a prior failure.</summary>
    public void ResetForRetry()
    {
        Status = LibraryImportStatus.Publishing;
        FailureReason = null;
        CompletedAtUtc = null;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
