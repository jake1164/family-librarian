using FamilyLibrarian.Domain.Publishing;

namespace FamilyLibrarian.Application.Publishing;

/// <summary>The full administrator-visible state of the CWA destination. No secret value is ever included — only hints/set-at.</summary>
public sealed record CwaStatus(
    bool IsEnabled,
    CwaTransportMode TransportMode,
    string? LocalIngestPath,
    string? SftpHost,
    int? SftpPort,
    string? SftpUsername,
    string? SftpIngestPath,
    CwaSftpAuthenticationMode SftpAuthenticationMode,
    bool HasSftpPrivateKey,
    string? SftpPrivateKeyHint,
    DateTimeOffset? SftpPrivateKeySetAtUtc,
    bool HasSftpPassphrase,
    string? SftpPassphraseHint,
    DateTimeOffset? SftpPassphraseSetAtUtc,
    bool HasSftpPassword,
    string? SftpPasswordHint,
    DateTimeOffset? SftpPasswordSetAtUtc,
    string? SftpHostKeyFingerprint,
    DateTimeOffset? SftpHostKeyTrustedAtUtc,
    string? OpdsBaseUrl,
    string? PublicUrl,
    string? OpdsUsername,
    bool HasOpdsPassword,
    string? OpdsPasswordHint,
    DateTimeOffset? OpdsPasswordSetAtUtc,
    string? EreaderServiceAccountUsername,
    bool HasEreaderServiceAccountPassword,
    string? EreaderServiceAccountPasswordHint,
    DateTimeOffset? EreaderServiceAccountPasswordSetAtUtc,
    DateTimeOffset? LastTestedAtUtc,
    bool? LastTestSucceeded,
    string? LastTestMessage)
{
    /// <summary>
    /// True once ingest delivery has enough to enable. Mirrors
    /// <c>CwaSettingsService</c>'s <c>GetConfigurationError</c> exactly, so the
    /// settings UI can show the Enabled switch only when flipping it would
    /// actually be accepted.
    /// </summary>
    public bool IsIngestConfigured => TransportMode == CwaTransportMode.Local
        ? !string.IsNullOrWhiteSpace(LocalIngestPath)
        : !string.IsNullOrWhiteSpace(SftpHost) &&
            !string.IsNullOrWhiteSpace(SftpUsername) &&
            !string.IsNullOrWhiteSpace(SftpIngestPath) &&
            !string.IsNullOrWhiteSpace(SftpHostKeyFingerprint) &&
            (SftpAuthenticationMode == CwaSftpAuthenticationMode.Password
                ? HasSftpPassword
                : HasSftpPrivateKey);

    /// <summary>
    /// True once the e-reader delivery service account has enough saved
    /// configuration to attempt a send -- independent of <see cref="IsIngestConfigured"/>
    /// and of whether CWA ingest/OPDS is enabled, since this is a separate capability.
    /// </summary>
    public bool IsEreaderDeliveryConfigured =>
        !string.IsNullOrWhiteSpace(EreaderServiceAccountUsername) && HasEreaderServiceAccountPassword;
}
