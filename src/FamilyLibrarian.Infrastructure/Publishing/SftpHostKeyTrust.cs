using Renci.SshNet;

namespace FamilyLibrarian.Infrastructure.Publishing;

internal static class SftpHostKeyTrust
{
    public static void RequireTrustedFingerprint(
        SftpClient client,
        string? trustedFingerprint,
        Action<string> observedFingerprint)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(observedFingerprint);

        client.HostKeyReceived += (_, args) =>
        {
            var fingerprint = args.FingerPrintSHA256;
            observedFingerprint(fingerprint);
            args.CanTrust = !string.IsNullOrWhiteSpace(trustedFingerprint) &&
                string.Equals(fingerprint, trustedFingerprint, StringComparison.Ordinal);
        };
    }
}
