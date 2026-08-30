using System.Text.RegularExpressions;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Publishing;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace FamilyLibrarian.Infrastructure.Publishing;

/// <summary>
/// Uploads to a remote host's ingest folder over SFTP — the default transport
/// when CWA doesn't share a filesystem with this host (e.g. a NAS on another
/// machine). Chosen over a mounted network share for its tighter blast
/// radius: a chrootable, scoped SFTP account rather than a standing,
/// always-writable mount, and no extra container mount privileges.
/// </summary>
/// <remarks>
/// A fresh connection per call, not a pooled/long-lived client: publishing
/// happens once per admin approval, not at a volume that would justify
/// connection-pooling complexity. SSH.NET's synchronous API is used directly
/// under <see cref="Task.Run(Action, CancellationToken)"/> rather than guessed
/// async overloads, since this is an infrequent, one-shot operation.
/// </remarks>
public sealed partial class SftpCwaIngestTransport(
    string host,
    int port,
    string username,
    string ingestDirectoryPath,
    CwaSftpAuthenticationMode authenticationMode,
    string credential,
    string? privateKeyPassphrase,
    string trustedHostKeyFingerprint,
    IClock clock,
    ILogger<SftpCwaIngestTransport> logger) : ICwaIngestTransport
{
    // A connection dropped mid-upload (or the host process crashing between
    // UploadFile and RenameFile) leaves a .{guid}.uploading temp file behind
    // with no in-process exception to catch -- there is nothing left running
    // to catch it. That file then sits in the ingest folder forever: CWA's
    // watcher ignores dotfiles, so it's invisible to CWA but not to whatever
    // eventually lists the directory. Swept here, before every new upload,
    // rather than on a separate timer -- this transport already opens a fresh
    // connection per call, so there is no idle connection to hang a scheduled
    // sweep off of. 15 minutes is comfortably longer than any single ebook
    // upload should take, so a temp file still present past that age was
    // never going to be renamed into place by whatever created it.
    private static readonly TimeSpan OrphanedUploadAge = TimeSpan.FromMinutes(15);

    [GeneratedRegex(@"^\.[0-9a-f]{32}\.uploading$")]
    private static partial Regex OrphanedUploadPattern();

    public Task WriteAsync(Stream content, string targetFilename, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFilename);

        return Task.Run(
            () =>
            {
                var authMethod = SftpAuthentication.Create(
                    authenticationMode, username, credential, privateKeyPassphrase);
                var connectionInfo = new ConnectionInfo(host, port, username, authMethod);

                using var client = new SftpClient(connectionInfo);
                SftpHostKeyTrust.RequireTrustedFingerprint(client, trustedHostKeyFingerprint, _ => { });
                client.Connect();
                try
                {
                    SweepOrphanedUploads(client);

                    var temporaryPath = CombineRemotePath(ingestDirectoryPath, $".{Guid.NewGuid():N}.uploading");
                    var destinationPath = CombineRemotePath(ingestDirectoryPath, targetFilename);

                    client.UploadFile(content, temporaryPath, canOverride: false);
                    // isPosix: true is load-bearing, not cosmetic. Without it, SSH.NET's
                    // RenameFile falls back (confirmed for real against atmoz/sftp) to a
                    // hardlink-then-unlink pair instead of an actual rename() syscall: the
                    // new path gets a bare inotify CREATE with no CLOSE_WRITE/MOVED_TO ever
                    // following it, which CWA's ingest watcher never reacts to -- the file
                    // sits in the ingest folder forever, undetected. The posix-rename
                    // extension forces a real atomic rename, producing the MOVED_FROM/
                    // MOVED_TO pair CWA's watcher actually listens for.
                    client.RenameFile(temporaryPath, destinationPath, isPosix: true);
                }
                finally
                {
                    client.Disconnect();
                }
            },
            cancellationToken);
    }

    public Task TouchAsync(string targetFilename, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFilename);

        return Task.Run(
            () =>
            {
                var authMethod = SftpAuthentication.Create(
                    authenticationMode, username, credential, privateKeyPassphrase);
                var connectionInfo = new ConnectionInfo(host, port, username, authMethod);

                using var client = new SftpClient(connectionInfo);
                SftpHostKeyTrust.RequireTrustedFingerprint(client, trustedHostKeyFingerprint, _ => { });
                client.Connect();
                try
                {
                    var destinationPath = CombineRemotePath(ingestDirectoryPath, targetFilename);
                    if (!client.Exists(destinationPath))
                    {
                        // Nothing delivered yet (or it failed outright) -- a later
                        // successful write will produce its own fresh watcher event.
                        return;
                    }

                    // Same rename-out-and-back as the local transport, and isPosix: true
                    // for the same reason WriteAsync's rename needs it -- without it,
                    // SSH.NET's fallback rename produces a bare CREATE with no
                    // CLOSE_WRITE/MOVED_TO, which CWA's watcher never reacts to.
                    var relocatedPath = CombineRemotePath(ingestDirectoryPath, $".{Guid.NewGuid():N}.rescan");
                    client.RenameFile(destinationPath, relocatedPath, isPosix: true);
                    client.RenameFile(relocatedPath, destinationPath, isPosix: true);
                }
                finally
                {
                    client.Disconnect();
                }
            },
            cancellationToken);
    }

    private static string CombineRemotePath(string directory, string filename) =>
        $"{directory.TrimEnd('/')}/{filename}";

    /// <summary>
    /// Best-effort: a sweep failure (e.g. the listing call itself errors) must
    /// never block the upload it runs ahead of. The next call gets another
    /// chance at the same stale files.
    /// </summary>
    private void SweepOrphanedUploads(SftpClient client)
    {
        try
        {
            DeleteOrphanedUploads(new SftpDirectoryClientAdapter(client), ingestDirectoryPath, clock.UtcNow);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogOrphanSweepFailed(exception);
        }
    }

    /// <summary>
    /// The sweep's actual decision logic, separated from <see cref="SftpClient"/>
    /// itself -- a sealed type with no test double -- behind
    /// <see cref="ISftpDirectoryClient"/> instead, so it can be exercised
    /// directly against a fake directory listing.
    /// </summary>
    internal static void DeleteOrphanedUploads(ISftpDirectoryClient client, string directoryPath, DateTimeOffset now)
    {
        foreach (var entry in client.ListDirectory(directoryPath))
        {
            if (entry.IsRegularFile &&
                OrphanedUploadPattern().IsMatch(entry.Name) &&
                now.UtcDateTime - entry.LastWriteTimeUtc > OrphanedUploadAge)
            {
                client.DeleteFile(entry.FullName);
            }
        }
    }

    [LoggerMessage(EventId = 1001, Level = LogLevel.Warning, Message = "sftp.ingest.orphan_sweep.failed")]
    private partial void LogOrphanSweepFailed(Exception exception);

    private sealed class SftpDirectoryClientAdapter(SftpClient client) : ISftpDirectoryClient
    {
        public IEnumerable<ISftpFile> ListDirectory(string path) => client.ListDirectory(path, null);

        public void DeleteFile(string path) => client.DeleteFile(path);
    }
}

/// <summary>
/// The minimal surface <see cref="SftpCwaIngestTransport"/>'s orphan sweep
/// needs from a real SFTP client. Small enough to fake directly in tests,
/// since SSH.NET's own <see cref="ISftpFile"/> implementation
/// (<c>Renci.SshNet.Sftp.SftpFile</c>) has no public constructor.
/// </summary>
internal interface ISftpDirectoryClient
{
    IEnumerable<ISftpFile> ListDirectory(string path);

    void DeleteFile(string path);
}
