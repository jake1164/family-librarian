namespace FamilyLibrarian.Application.Publishing;

public sealed record ConnectionTestOutcome(
    bool Succeeded,
    string Message,
    bool RequiresSftpHostKeyTrust = false,
    string? SftpHostKeyFingerprint = null);
