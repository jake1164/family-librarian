using System.Text;
using FamilyLibrarian.Application.Publishing;
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
    string privateKeyPem,
    string? passphrase) : ICwaIngestTransport
{
    public Task WriteAsync(Stream content, string targetFilename, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFilename);

        return Task.Run(
            () =>
            {
                using var keyStream = new MemoryStream(Encoding.UTF8.GetBytes(privateKeyPem));
                var keyFile = string.IsNullOrEmpty(passphrase)
                    ? new PrivateKeyFile(keyStream)
                    : new PrivateKeyFile(keyStream, passphrase);
                var authMethod = new PrivateKeyAuthenticationMethod(username, keyFile);
                var connectionInfo = new ConnectionInfo(host, port, username, authMethod);

                using var client = new SftpClient(connectionInfo);
                client.Connect();
                try
                {
                    var temporaryPath = CombineRemotePath(ingestDirectoryPath, $".{Guid.NewGuid():N}.uploading");
                    var destinationPath = CombineRemotePath(ingestDirectoryPath, targetFilename);

                    client.UploadFile(content, temporaryPath, canOverride: false);
                    client.RenameFile(temporaryPath, destinationPath);
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
