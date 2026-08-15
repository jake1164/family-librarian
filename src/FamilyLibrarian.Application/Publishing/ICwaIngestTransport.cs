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
}
