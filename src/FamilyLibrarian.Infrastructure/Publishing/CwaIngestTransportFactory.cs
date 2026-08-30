using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Publishing;
using Microsoft.Extensions.Logging;

namespace FamilyLibrarian.Infrastructure.Publishing;

public sealed class CwaIngestTransportFactory(
    ICredentialProtector protector,
    IClock clock,
    ILogger<SftpCwaIngestTransport> logger) : ICwaIngestTransportFactory
{
    public ICwaIngestTransport Create(CwaSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.TransportMode == CwaTransportMode.Sftp)
        {
            if (string.IsNullOrWhiteSpace(settings.SftpHost) ||
                string.IsNullOrWhiteSpace(settings.SftpUsername) ||
                string.IsNullOrWhiteSpace(settings.SftpIngestPath) ||
                string.IsNullOrWhiteSpace(settings.SftpHostKeyFingerprint))
            {
                throw new InvalidOperationException("The CWA SFTP transport is not fully configured.");
            }

            var credential = settings.SftpAuthenticationMode switch
            {
                CwaSftpAuthenticationMode.Password when settings.HasSftpPassword => protector.Unprotect(
                    PublishingSecretPurposes.CwaSftpPassword,
                    settings.ProtectedSftpPassword!,
                    settings.SftpPasswordFormatVersion),
                CwaSftpAuthenticationMode.PrivateKey when settings.HasSftpPrivateKey => protector.Unprotect(
                    PublishingSecretPurposes.CwaSftpPrivateKey,
                    settings.ProtectedSftpPrivateKey!,
                    settings.SftpPrivateKeyFormatVersion),
                CwaSftpAuthenticationMode.Password => throw new InvalidOperationException("The CWA SFTP password is not configured."),
                CwaSftpAuthenticationMode.PrivateKey => throw new InvalidOperationException("The CWA SFTP private key is not configured."),
                _ => throw new InvalidOperationException("The configured SFTP authentication mode is not supported.")
            } ?? throw new InvalidOperationException("The stored CWA SFTP credential could not be decrypted.");

            var passphrase = settings.SftpAuthenticationMode == CwaSftpAuthenticationMode.PrivateKey && settings.HasSftpPassphrase
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
                settings.SftpAuthenticationMode,
                credential,
                passphrase,
                settings.SftpHostKeyFingerprint,
                clock,
                logger);
        }

        if (string.IsNullOrWhiteSpace(settings.LocalIngestPath))
        {
            throw new InvalidOperationException("The CWA local ingest path is not configured.");
        }

        return new LocalCwaIngestTransport(settings.LocalIngestPath);
    }
}
