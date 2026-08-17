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
    string? OpdsUsername,
    bool HasOpdsPassword,
    string? OpdsPasswordHint,
    DateTimeOffset? OpdsPasswordSetAtUtc,
    DateTimeOffset? LastTestedAtUtc,
    bool? LastTestSucceeded,
    string? LastTestMessage);
