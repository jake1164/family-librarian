using FamilyLibrarian.Domain.Acquisition;

namespace FamilyLibrarian.Application.Acquisition;

/// <summary>
/// Writes an untrusted upload into the quarantine storage zone, and moves it
/// between zones as its <see cref="MediaAssetStorageState"/> changes.
/// </summary>
/// <remarks>
/// No method here — or anywhere in this interface's implementations — ever
/// returns a browser-servable URL or filesystem path. Only the host process
/// opens a staged file; the WebAssembly client and any future external provider
/// never get direct access to quarantine or processing storage.
/// </remarks>
public interface IAssetStagingStore
{
    /// <summary>
    /// Streams <paramref name="content"/> into quarantine under a generated
    /// filename, computing its checksum and sniffing its real content type in
    /// the same pass.
    /// </summary>
    /// <param name="maxSizeBytes">
    /// Enforced while copying, independent of any caller-declared size — a
    /// stream that exceeds this is aborted mid-copy rather than trusted to stop
    /// on its own.
    /// </param>
    /// <exception cref="AssetTooLargeException">The stream exceeded <paramref name="maxSizeBytes"/>.</exception>
    Task<StagedFile> WriteToQuarantineAsync(
        Stream content,
        string originalFilename,
        long maxSizeBytes,
        CancellationToken cancellationToken);

    /// <summary>
    /// Bytes still writable to the volume that holds quarantine storage, or
    /// <c>null</c> when that cannot be determined (the caller then skips its
    /// pre-download space check rather than blocking on an unknown).
    /// </summary>
    long? GetAvailableFreeBytes() => null;

    /// <summary>Opens a staged file for host-only reading, from its current zone.</summary>
    Task<Stream> OpenAsync(
        MediaAssetStorageState zone,
        string storedFilename,
        CancellationToken cancellationToken);

    /// <summary>
    /// Physically relocates a staged file between zone folders, keeping disk
    /// layout in sync with the owning <c>MediaAsset</c>'s logical
    /// <see cref="MediaAssetStorageState"/>.
    /// </summary>
    Task MoveAsync(
        MediaAssetStorageState fromZone,
        MediaAssetStorageState toZone,
        string storedFilename,
        CancellationToken cancellationToken);

    /// <summary>
    /// Permanently removes a staged artifact from its current storage zone.
    /// The audit record and the owning <c>MediaAsset</c> remain, so removal is
    /// traceable without retaining unsafe bytes.
    /// </summary>
    Task DeleteAsync(
        MediaAssetStorageState zone,
        string storedFilename,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("This staging store does not support deletion.");

    /// <summary>
    /// The files physically present in one storage zone, with their sizes and last-write times. Used to see what the disk actually
    /// holds (and to find files no record accounts for); it never opens or reads a file. Defaults to none for stores that cannot list.
    /// </summary>
    IReadOnlyList<StoredFileInfo> ListStoredFiles(MediaAssetStorageState zone) => [];

    /// <summary>Free and total bytes of the volume holding the storage zones, or nulls when they cannot be determined.</summary>
    StorageVolumeInfo GetVolumeInfo() => new(GetAvailableFreeBytes(), null);
}

/// <summary>A file in a storage zone. The name is the generated stored name, never the original filename.</summary>
public sealed record StoredFileInfo(string StoredFilename, long SizeBytes, DateTimeOffset LastWriteUtc);

public sealed record StorageVolumeInfo(long? FreeBytes, long? TotalBytes);

public sealed record StagedFile(
    string StoredFilename,
    long SizeBytes,
    string Sha256,
    string DetectedMimeType);

public sealed class AssetTooLargeException(long maxSizeBytes)
    : InvalidOperationException($"The upload exceeded the {maxSizeBytes}-byte limit.")
{
    public long MaxSizeBytes { get; } = maxSizeBytes;
}
