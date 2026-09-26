using Microsoft.Extensions.Options;

namespace FamilyLibrarian.Infrastructure.Acquisition;

/// <summary>Durable local workspace for an in-progress LibriVox archive fetch.</summary>
public sealed class LibriVoxDownloadWorkspace(IOptions<StorageOptions> options)
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    private readonly string root = Path.Combine(options.Value.RootPath, "acquisition-work", "librivox");

    public string DirectoryFor(Guid requestFormatId) => Path.Combine(root, requestFormatId.ToString("N"));

    public FileStream AcquireLock(Guid requestFormatId)
    {
        var directory = DirectoryFor(requestFormatId);
        Directory.CreateDirectory(directory);
        return new FileStream(
            Path.Combine(directory, "resume.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
            bufferSize: 1, FileOptions.None);
    }

    public void Delete(Guid requestFormatId) => DeleteDirectory(DirectoryFor(requestFormatId));

    /// <summary>Removes stale workspaces at host startup; the normal data volume is retained across container recreation.</summary>
    public int CleanupExpired(DateTimeOffset now)
    {
        if (!Directory.Exists(root)) return 0;

        var removed = 0;
        foreach (var path in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(path);
            if (!Guid.TryParseExact(name, "N", out _)) continue;
            try
            {
                var latestWriteUtc = Directory.GetLastWriteTimeUtc(path);
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly))
                    latestWriteUtc = Max(latestWriteUtc, File.GetLastWriteTimeUtc(file));
                if (latestWriteUtc >= (now - Retention).UtcDateTime) continue;
                Directory.Delete(path, recursive: true);
                removed++;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return removed;
    }

    private static DateTime Max(DateTime left, DateTime right) => left >= right ? left : right;

    public static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
