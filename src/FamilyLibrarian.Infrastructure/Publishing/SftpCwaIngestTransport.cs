using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Publishing;
using Renci.SshNet;

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
public sealed class SftpCwaIngestTransport(
    string host,
    int port,
    string username,
    string ingestDirectoryPath,
    CwaSftpAuthenticationMode authenticationMode,
    string credential,
    string? privateKeyPassphrase,
    string trustedHostKeyFingerprint) : ICwaIngestTransport
{
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
}
