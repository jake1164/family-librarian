using FamilyLibrarian.Application.Publishing;

namespace FamilyLibrarian.Infrastructure.Publishing;

/// <summary>
/// Writes into a locally reachable ingest path — a same-host Docker volume, or
/// a host-mounted NFS/CIFS share; the code makes no distinction.
/// </summary>
/// <remarks>
/// The temp name is written in the same directory as the final target, not a
/// separate staging folder: <see cref="File.Move(string, string)"/> is only
/// atomic within one filesystem, and the ingest directory itself is that
/// filesystem. The leading dot plus a non-ebook extension keeps CWA's watcher,
/// which filters by known ebook extensions, from noticing the file mid-write.
/// </remarks>
public sealed class LocalCwaIngestTransport(string ingestDirectoryPath) : ICwaIngestTransport
{
    public async Task WriteAsync(Stream content, string targetFilename, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFilename);

        Directory.CreateDirectory(ingestDirectoryPath);

        var temporaryPath = Path.Combine(ingestDirectoryPath, $".{Guid.NewGuid():N}.uploading");
        var destinationPath = Path.Combine(ingestDirectoryPath, targetFilename);

        try
        {
            await using (var destination = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 81_920, useAsync: true))
            {
                await content.CopyToAsync(destination, cancellationToken);
            }

            File.Move(temporaryPath, destinationPath, overwrite: false);
        }
        catch
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            throw;
        }
    }
}
