using System.Security.Cryptography;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Domain.Acquisition;
using Microsoft.Extensions.Options;

namespace FamilyLibrarian.Infrastructure.Acquisition;

public sealed class FileSystemAssetStagingStore(IOptions<StorageOptions> options) : IAssetStagingStore
{
    private readonly StorageOptions _options = options.Value;

    public async Task<StagedFile> WriteToQuarantineAsync(
        Stream content,
        string originalFilename,
        long maxSizeBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(originalFilename);

        var quarantineDirectory = ZoneDirectory(MediaAssetStorageState.Quarantine);
        Directory.CreateDirectory(quarantineDirectory);

        // Never derived from the caller-supplied original filename: a crafted
        // name (e.g. containing "../") must have no influence on the resulting
        // path.
        var storedFilename = $"{Guid.NewGuid():N}{Path.GetExtension(originalFilename)}";
        var destinationPath = Path.Combine(quarantineDirectory, storedFilename);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81_920];
        var header = new byte[SignatureFileTypeDetector.RequiredHeaderLength];
        var headerBytesRead = 0;
        long totalBytesRead = 0;
        var exceeded = false;

        await using (var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81_920,
            useAsync: true))
        {
            int bytesRead;
            while (!exceeded && (bytesRead = await content.ReadAsync(buffer, cancellationToken)) > 0)
            {
                totalBytesRead += bytesRead;
                if (totalBytesRead > maxSizeBytes)
                {
                    // Stop reading immediately; the caller-declared size is never
                    // trusted on its own. The partially written file is cleaned
                    // up below, once the handle above is closed.
                    exceeded = true;
                    break;
                }

                if (headerBytesRead < header.Length)
                {
                    var toCopy = Math.Min(bytesRead, header.Length - headerBytesRead);
                    Array.Copy(buffer, 0, header, headerBytesRead, toCopy);
                    headerBytesRead += toCopy;
                }

                hash.AppendData(buffer, 0, bytesRead);
                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            }
        }

        if (exceeded)
        {
            File.Delete(destinationPath);
            throw new AssetTooLargeException(maxSizeBytes);
        }

        var checksum = Convert.ToHexStringLower(hash.GetHashAndReset());
        var detectedMimeType = SignatureFileTypeDetector.Detect(header, headerBytesRead);

        return new StagedFile(storedFilename, totalBytesRead, checksum, detectedMimeType);
    }

    public Task<Stream> OpenAsync(
        MediaAssetStorageState zone,
        string storedFilename,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storedFilename);

        var path = Path.Combine(ZoneDirectory(zone), storedFilename);
        Stream stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81_920, useAsync: true);
        return Task.FromResult(stream);
    }

    public Task MoveAsync(
        MediaAssetStorageState fromZone,
        MediaAssetStorageState toZone,
        string storedFilename,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storedFilename);

        var destinationDirectory = ZoneDirectory(toZone);
        Directory.CreateDirectory(destinationDirectory);

        var sourcePath = Path.Combine(ZoneDirectory(fromZone), storedFilename);
        var destinationPath = Path.Combine(destinationDirectory, storedFilename);
        File.Move(sourcePath, destinationPath, overwrite: false);

        return Task.CompletedTask;
    }

    private string ZoneDirectory(MediaAssetStorageState zone) =>
        Path.Combine(_options.RootPath, ZoneFolderName(zone));

    private static string ZoneFolderName(MediaAssetStorageState zone) => zone switch
    {
        MediaAssetStorageState.Quarantine => "quarantine",
        MediaAssetStorageState.Processing => "processing",
        MediaAssetStorageState.Rejected => "rejected",
        MediaAssetStorageState.Trusted => "trusted",
        MediaAssetStorageState.Archived => "archived",
        _ => throw new ArgumentOutOfRangeException(nameof(zone), zone, "Unknown storage zone.")
    };
}
