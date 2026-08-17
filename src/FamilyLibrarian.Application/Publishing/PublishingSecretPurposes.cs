namespace FamilyLibrarian.Application.Publishing;

/// <summary>
/// Fixed <see cref="Integrations.ICredentialProtector"/> purpose strings for
/// every publishing-destination secret, shared between the settings services
/// (Application) that write them and the transport/client factories
/// (Infrastructure) that decrypt them at call time.
/// </summary>
public static class PublishingSecretPurposes
{
    public const string CwaSftpPrivateKey = "cwa-sftp-private-key";
    public const string CwaSftpPassphrase = "cwa-sftp-passphrase";
    public const string CwaSftpPassword = "cwa-sftp-password";
    public const string CwaOpdsPassword = "cwa-opds-password";
    public const string AudiobookshelfApiToken = "audiobookshelf-api-token";
}
