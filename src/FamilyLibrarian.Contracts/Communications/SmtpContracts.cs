namespace FamilyLibrarian.Contracts.Communications;

public sealed record SmtpSettingsResponse(
    bool IsEnabled,
    string? Host,
    int? Port,
    string SecurityMode,
    string? Username,
    bool HasPassword,
    DateTimeOffset? PasswordSetAtUtc,
    string? FromAddress,
    string? FromName,
    DateTimeOffset? LastTestedAtUtc,
    bool? LastTestSucceeded,
    string? LastTestMessage);

public sealed record SetSmtpSettingsRequest(
    string? Host,
    int? Port,
    string SecurityMode,
    string? Username,
    string? FromAddress,
    string? FromName);

public sealed record SetSmtpEnabledRequest(bool Enabled);

public sealed record SetSmtpPasswordRequest(string Password);

public sealed record SendSmtpTestRequest(string? RecipientAddress);

public sealed record SmtpTestResponse(bool Succeeded, string? Message);
