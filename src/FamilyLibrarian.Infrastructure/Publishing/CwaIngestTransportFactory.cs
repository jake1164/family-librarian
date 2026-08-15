using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Publishing;

namespace FamilyLibrarian.Infrastructure.Publishing;

public sealed class CwaIngestTransportFactory(ICredentialProtector protector) : ICwaIngestTransportFactory
{
    public ICwaIngestTransport Create(CwaSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.TransportMode == CwaTransportMode.Sftp)
        {
            if (string.IsNullOrWhiteSpace(settings.SftpHost) ||
                string.IsNullOrWhiteSpace(settings.SftpUsername) ||
                string.IsNullOrWhiteSpace(settings.SftpIngestPath) ||
                !settings.HasSftpPrivateKey)
            {
                throw new InvalidOperationException("The CWA SFTP transport is not fully configured.");
            }

            var privateKey = protector.Unprotect(
                PublishingSecretPurposes.CwaSftpPrivateKey,
                settings.ProtectedSftpPrivateKey!,
                settings.SftpPrivateKeyFormatVersion)
                ?? throw new InvalidOperationException("The stored CWA SFTP private key could not be decrypted.");

            var passphrase = settings.HasSftpPassphrase
                ? protector.Unprotect(
                    PublishingSecretPurposes.CwaSftpPassphrase,
                    settings.ProtectedSftpPassphrase!,
                    settings.SftpPassphraseFormatVersion)
                : null;

            return new SftpCwaIngestTransport(
                settings.SftpHost,
                settings.SftpPort ?? 22,
                settings.SftpUsername,
                settings.SftpIngestPath,
                privateKey,
                passphrase);
        }

        if (string.IsNullOrWhiteSpace(settings.LocalIngestPath))
        {
            throw new InvalidOperationException("The CWA local ingest path is not configured.");
        }

        return new LocalCwaIngestTransport(settings.LocalIngestPath);
    }
}
