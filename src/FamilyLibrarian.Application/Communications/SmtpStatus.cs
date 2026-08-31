using FamilyLibrarian.Domain.Communications;

namespace FamilyLibrarian.Application.Communications;

/// <summary>Administrator-visible SMTP state. It intentionally contains no password value.</summary>
public sealed record SmtpStatus(
    bool IsEnabled,
    string? Host,
    int? Port,
    SmtpSecurityMode SecurityMode,
    string? Username,
    bool HasPassword,
    DateTimeOffset? PasswordSetAtUtc,
    string? FromAddress,
    string? FromName,
    DateTimeOffset? LastTestedAtUtc,
    bool? LastTestSucceeded,
    string? LastTestMessage);
