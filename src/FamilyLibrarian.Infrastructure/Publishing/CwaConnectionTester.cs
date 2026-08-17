using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Publishing;
using Renci.SshNet;

namespace FamilyLibrarian.Infrastructure.Publishing;

public sealed class CwaConnectionTester(ICredentialProtector protector, IHttpClientFactory httpClientFactory)
    : ICwaConnectionTester
{
    public async Task<ConnectionTestOutcome> TestAsync(
        CwaSettings settings,
        CwaConnectionTestTarget target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (target == CwaConnectionTestTarget.Opds)
        {
            return await TestOpdsAsync(settings, cancellationToken);
        }

        var transportOutcome = settings.TransportMode == CwaTransportMode.Local
            ? TestLocal(settings)
            : await TestSftpAsync(settings, cancellationToken);

        if (target == CwaConnectionTestTarget.Ingest || !transportOutcome.Succeeded)
        {
            return transportOutcome;
        }

        var opdsOutcome = await TestOpdsAsync(settings, cancellationToken);
        return opdsOutcome.Succeeded
            ? new ConnectionTestOutcome(true, $"{transportOutcome.Message} {opdsOutcome.Message}")
            : opdsOutcome;
    }

    private static ConnectionTestOutcome TestLocal(CwaSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.LocalIngestPath))
        {
            return new ConnectionTestOutcome(false, "No local ingest path is configured.");
        }

        try
        {
            Directory.CreateDirectory(settings.LocalIngestPath);
            var probePath = Path.Combine(settings.LocalIngestPath, $".family-librarian-probe-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probePath, "probe");
            File.Delete(probePath);
            return new ConnectionTestOutcome(true, "The local ingest path is writable.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new ConnectionTestOutcome(false, $"The local ingest path is not writable: {exception.Message}");
        }
    }

    private Task<ConnectionTestOutcome> TestSftpAsync(CwaSettings settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.SftpHost) ||
            string.IsNullOrWhiteSpace(settings.SftpUsername) ||
            string.IsNullOrWhiteSpace(settings.SftpIngestPath))
        {
            return Task.FromResult(
                new ConnectionTestOutcome(false, "An SFTP host, username, and ingest path are required."));
        }

        return Task.Run(
            () =>
            {
                string? observedFingerprint = null;
                try
                {
                    var missingCredentialMessage = settings.SftpAuthenticationMode switch
                    {
                        CwaSftpAuthenticationMode.Password when !settings.HasSftpPassword =>
                            "No SFTP password is saved. Enter a password, then save and test again.",
                        CwaSftpAuthenticationMode.PrivateKey when !settings.HasSftpPrivateKey =>
                            "No SFTP private key is saved. Enter a private key, then save and test again.",
                        _ => null
                    };
                    if (missingCredentialMessage is not null)
                    {
                        return new ConnectionTestOutcome(false, missingCredentialMessage);
                    }

                    var credential = ResolveCredential(settings);
                    if (credential is null)
                    {
                        return new ConnectionTestOutcome(
                            false,
                            settings.SftpAuthenticationMode == CwaSftpAuthenticationMode.Password
                                ? "The saved SFTP password can no longer be decrypted. Enter it again, then save and test."
                                : "The saved SFTP private key can no longer be decrypted. Enter it again, then save and test.");
                    }

                    var passphrase = settings.SftpAuthenticationMode == CwaSftpAuthenticationMode.PrivateKey && settings.HasSftpPassphrase
                        ? protector.Unprotect(
                            PublishingSecretPurposes.CwaSftpPassphrase,
                            settings.ProtectedSftpPassphrase!,
                            settings.SftpPassphraseFormatVersion)
                        : null;

                    var authMethod = SftpAuthentication.Create(
                        settings.SftpAuthenticationMode, settings.SftpUsername, credential, passphrase);
                    var connectionInfo = new ConnectionInfo(
                        settings.SftpHost, settings.SftpPort ?? 22, settings.SftpUsername, authMethod);

                    using var client = new SftpClient(connectionInfo);
                    SftpHostKeyTrust.RequireTrustedFingerprint(
                        client,
                        settings.SftpHostKeyFingerprint,
                        fingerprint => observedFingerprint = fingerprint);
                    client.Connect();
                    try
                    {
                        return TestSftpIngestPath(client, settings.SftpIngestPath);
                    }
                    finally
                    {
                        client.Disconnect();
                    }
                }
                catch (Exception exception)
                {
                    if (exception is Renci.SshNet.Common.SshConnectionException &&
                        !string.IsNullOrWhiteSpace(observedFingerprint) &&
                        !string.Equals(observedFingerprint, settings.SftpHostKeyFingerprint, StringComparison.Ordinal))
                    {
                        return new ConnectionTestOutcome(
                            false,
                            string.IsNullOrWhiteSpace(settings.SftpHostKeyFingerprint)
                                ? "Trust this SFTP server's SSH host key before connecting."
                                : "The SFTP server's SSH host key has changed. Review and trust the new fingerprint before connecting.",
                            true,
                            observedFingerprint);
                    }

                    if (IsHostResolutionFailure(exception))
                    {
                        return new ConnectionTestOutcome(
                            false,
                            "The configured SFTP host cannot be resolved from the Family Librarian container. " +
                            "Use a LAN IP address or a fully qualified host name that Docker can resolve.");
                    }

                    return new ConnectionTestOutcome(false, $"The SFTP connection failed: {exception.Message}");
                }
            },
            cancellationToken);
    }

    private static bool IsHostResolutionFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException
                {
                    SocketErrorCode: SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain
                })
            {
                return true;
            }
        }

        return false;
    }

    private static ConnectionTestOutcome TestSftpIngestPath(SftpClient client, string ingestPath)
    {
        if (!client.Exists(ingestPath))
        {
            return new ConnectionTestOutcome(false, "Connected over SFTP, but the ingest path does not exist.");
        }

        var probePath = $"{ingestPath.TrimEnd('/')}/.family-librarian-sftp-write-probe-{Guid.NewGuid():N}.tmp";
        var probeMayExist = false;
        try
        {
            using var content = new MemoryStream(Encoding.UTF8.GetBytes("Family Librarian SFTP write probe."));
            probeMayExist = true;
            client.UploadFile(content, probePath, canOverride: false);

            if (!client.Exists(probePath))
            {
                return new ConnectionTestOutcome(
                    false,
                    "Connected over SFTP, but the server did not report the temporary write probe after upload.");
            }

            client.DeleteFile(probePath);
            probeMayExist = false;
            return new ConnectionTestOutcome(
                true,
                "Connected over SFTP, found the ingest path, and wrote then removed a temporary probe file.");
        }
        catch (Exception exception)
        {
            if (probeMayExist)
            {
                try
                {
                    if (client.Exists(probePath))
                    {
                        client.DeleteFile(probePath);
                    }
                }
                catch (Exception cleanupException)
                {
                    return new ConnectionTestOutcome(
                        false,
                        "SFTP could not complete or clean up its temporary write probe. " +
                        $"The probe path was {probePath}. Upload error: {exception.Message}. " +
                        $"Cleanup error: {cleanupException.Message}");
                }
            }

            return new ConnectionTestOutcome(
                false,
                $"Connected over SFTP, but could not write and remove a temporary probe file: {exception.Message}");
        }
    }

    private string? ResolveCredential(CwaSettings settings) => settings.SftpAuthenticationMode switch
    {
        CwaSftpAuthenticationMode.Password when settings.HasSftpPassword => protector.Unprotect(
            PublishingSecretPurposes.CwaSftpPassword,
            settings.ProtectedSftpPassword!,
            settings.SftpPasswordFormatVersion),
        CwaSftpAuthenticationMode.PrivateKey when settings.HasSftpPrivateKey => protector.Unprotect(
            PublishingSecretPurposes.CwaSftpPrivateKey,
            settings.ProtectedSftpPrivateKey!,
            settings.SftpPrivateKeyFormatVersion),
        _ => null
    };

    private async Task<ConnectionTestOutcome> TestOpdsAsync(CwaSettings settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.OpdsBaseUrl))
        {
            return new ConnectionTestOutcome(true, "No OPDS URL is configured; import verification will be skipped.");
        }

        try
        {
            var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{settings.OpdsBaseUrl.TrimEnd('/')}/opds/");
            if (!string.IsNullOrWhiteSpace(settings.OpdsUsername))
            {
                if (!settings.HasOpdsPassword)
                {
                    return new ConnectionTestOutcome(
                        false,
                        "An OPDS username is configured, but no OPDS password is saved.");
                }

                var password = settings.HasOpdsPassword
                    ? protector.Unprotect(
                        PublishingSecretPurposes.CwaOpdsPassword,
                        settings.ProtectedOpdsPassword!,
                        settings.OpdsPasswordFormatVersion)
                    : null;
                if (password is null)
                {
                    return new ConnectionTestOutcome(
                        false,
                        "The saved OPDS password can no longer be decrypted. Enter it again, then save and test.");
                }

                var raw = $"{settings.OpdsUsername}:{password}";
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)));
            }

            using var response = await client.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode
                ? new ConnectionTestOutcome(true, "The OPDS catalog is reachable.")
                : new ConnectionTestOutcome(false, $"The OPDS catalog responded with status {(int)response.StatusCode}.");
        }
        catch (HttpRequestException exception)
        {
            return new ConnectionTestOutcome(false, $"The OPDS catalog is unreachable: {exception.Message}");
        }
    }
}
