using FamilyLibrarian.Domain.Publishing;

namespace FamilyLibrarian.Application.Publishing;

/// <summary>Unsaved delivery values used for a one-off CWA ingest probe.</summary>
public sealed record CwaIngestTestConfiguration(
    CwaTransportMode TransportMode,
    string? LocalIngestPath,
    string? SftpHost,
    int? SftpPort,
    string? SftpUsername,
    string? SftpIngestPath,
    CwaSftpAuthenticationMode SftpAuthenticationMode,
    string? SftpPrivateKey,
    string? SftpPassphrase,
    string? SftpPassword,
    string? TrustedSftpHostKeyFingerprint);
