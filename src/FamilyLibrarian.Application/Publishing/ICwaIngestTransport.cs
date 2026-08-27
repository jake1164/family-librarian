namespace FamilyLibrarian.Application.Publishing;

/// <summary>
/// Hands a complete file to CWA's watched ingest folder — locally mounted, or
/// over SFTP to a remote host.
/// </summary>
/// <remarks>
/// Every implementation must never let CWA's watcher see a partially written
/// file: CWA documents that partial files can create duplicate imports or
/// database corruption. The contract is upload/write to a temporary name,
/// then an atomic rename into the watched directory.
/// </remarks>
public interface ICwaIngestTransport
{
    Task WriteAsync(Stream content, string targetFilename, CancellationToken cancellationToken);

    /// <summary>
    /// Re-signals an already-delivered file to CWA's watcher, without
    /// re-transporting its content.
    /// </summary>
    /// <remarks>
    /// CWA's ingest watcher only reacts to filesystem events that occur after
    /// it starts watching -- it does not scan for pre-existing files on
    /// startup. A file delivered while CWA was stopped (or mid-restart) is
    /// otherwise invisible to it forever once it comes back up. Implementations
    /// give CWA a fresh MOVED_FROM/MOVED_TO pair for the same file (the same
    /// event class the original write already produces), never a second
    /// upload. A no-op is acceptable when the target file no longer exists at
    /// the destination -- there is nothing to re-signal.
    /// </remarks>
    Task TouchAsync(string targetFilename, CancellationToken cancellationToken);
}
