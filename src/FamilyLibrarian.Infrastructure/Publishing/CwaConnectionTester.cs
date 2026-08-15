using System.Net.Http.Headers;
using System.Text;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Publishing;
using Renci.SshNet;

namespace FamilyLibrarian.Infrastructure.Publishing;

public sealed class CwaConnectionTester(ICredentialProtector protector, IHttpClientFactory httpClientFactory)
    : ICwaConnectionTester
{
    public async Task<ConnectionTestOutcome> TestAsync(CwaSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var transportOutcome = settings.TransportMode == CwaTransportMode.Local
            ? TestLocal(settings)
            : await TestSftpAsync(settings, cancellationToken);

        if (!transportOutcome.Succeeded)
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
            string.IsNullOrWhiteSpace(settings.SftpIngestPath) ||
            !settings.HasSftpPrivateKey)
        {
            return Task.FromResult(
                new ConnectionTestOutcome(false, "SFTP host, username, ingest path, and a private key are all required."));
        }

        return Task.Run(
            () =>
            {
                try
                {
                    var privateKey = protector.Unprotect(
                        PublishingSecretPurposes.CwaSftpPrivateKey,
                        settings.ProtectedSftpPrivateKey!,
                        settings.SftpPrivateKeyFormatVersion);
                    if (privateKey is null)
                    {
                        return new ConnectionTestOutcome(false, "The stored SFTP private key could not be decrypted.");
                    }

                    var passphrase = settings.HasSftpPassphrase
                        ? protector.Unprotect(
                            PublishingSecretPurposes.CwaSftpPassphrase,
                            settings.ProtectedSftpPassphrase!,
                            settings.SftpPassphraseFormatVersion)
                        : null;

                    using var keyStream = new MemoryStream(Encoding.UTF8.GetBytes(privateKey));
                    var keyFile = string.IsNullOrEmpty(passphrase)
                        ? new PrivateKeyFile(keyStream)
                        : new PrivateKeyFile(keyStream, passphrase);
                    var authMethod = new PrivateKeyAuthenticationMethod(settings.SftpUsername, keyFile);
                    var connectionInfo = new ConnectionInfo(
                        settings.SftpHost, settings.SftpPort ?? 22, settings.SftpUsername, authMethod);

                    using var client = new SftpClient(connectionInfo);
                    client.Connect();
                    try
                    {
                        return client.Exists(settings.SftpIngestPath)
                            ? new ConnectionTestOutcome(true, "Connected over SFTP and found the ingest path.")
                            : new ConnectionTestOutcome(false, "Connected over SFTP, but the ingest path does not exist.");
                    }
                    finally
                    {
                        client.Disconnect();
                    }
                }
                catch (Exception exception)
                {
                    return new ConnectionTestOutcome(false, $"The SFTP connection failed: {exception.Message}");
                }
            },
            cancellationToken);
    }

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
                var password = settings.HasOpdsPassword
                    ? protector.Unprotect(
                        PublishingSecretPurposes.CwaOpdsPassword,
                        settings.ProtectedOpdsPassword!,
                        settings.OpdsPasswordFormatVersion)
                    : null;
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
