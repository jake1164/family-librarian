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
}

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
