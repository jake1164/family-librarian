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

/// <summary>
/// A non-persistent SMTP connection probe. Draft field values (including a
/// freshly typed but unsaved password) are used only for the probe and are
/// never written unless the administrator subsequently saves them. Any
/// omitted field -- a blank draft password included -- falls back to the
/// currently saved setting, so a caller that only wants to test the saved
/// configuration can still pass just <see cref="RecipientAddress"/>.
/// </summary>
public sealed record SendSmtpTestRequest(
    string? RecipientAddress,
    string? Host,
    int? Port,
    string? SecurityMode,
    string? Username,
    string? Password,
    string? FromAddress,
    string? FromName);

public sealed record SmtpTestResponse(bool Succeeded, string? Message);
