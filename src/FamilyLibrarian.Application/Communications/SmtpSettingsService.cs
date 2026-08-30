using System.Net.Mail;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Audit;
using FamilyLibrarian.Domain.Communications;

namespace FamilyLibrarian.Application.Communications;

/// <summary>Administrative commands for the optional outbound SMTP provider.</summary>
public sealed class SmtpSettingsService(
    ISmtpSettingsStore store,
    ICredentialProtector protector,
    ISmtpTestSender testSender,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock)
{
    public async Task<SmtpStatus> GetStatusAsync(CancellationToken cancellationToken) =>
        ToStatus(await store.FindAsync(cancellationToken));

    public async Task<SmtpCommandResult> SetEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        var settings = await store.GetOrCreateAsync(cancellationToken);
        if (enabled && GetConfigurationError(settings) is { } error)
        {
            return SmtpCommandResult.Invalid(error);
        }

        settings.SetEnabled(enabled, currentUser.UserId, clock.UtcNow);
        await store.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(
            enabled ? AuditActions.CommunicationProviderEnabled : AuditActions.CommunicationProviderDisabled,
            AuditSubjectTypes.CommunicationProvider,
            "smtp",
            new { Provider = "smtp", Enabled = enabled },
            cancellationToken);

        return SmtpCommandResult.Success(ToStatus(settings));
    }

    public async Task<SmtpCommandResult> SetSettingsAsync(
        string? host,
        int? port,
        SmtpSecurityMode securityMode,
        string? username,
        string? fromAddress,
        string? fromName,
        CancellationToken cancellationToken)
    {
        if (port is not null and (< 1 or > 65_535))
        {
            return SmtpCommandResult.Invalid("The SMTP port must be between 1 and 65535.");
        }

        if (!string.IsNullOrWhiteSpace(fromAddress) && !MailAddress.TryCreate(fromAddress.Trim(), out _))
        {
            return SmtpCommandResult.Invalid("The sender email address is not valid.");
        }

        var settings = await store.GetOrCreateAsync(cancellationToken);
        settings.SetSettings(host, port, securityMode, username, fromAddress, fromName, currentUser.UserId, clock.UtcNow);
        await store.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(
            AuditActions.CommunicationProviderSettingsChanged,
            AuditSubjectTypes.CommunicationProvider,
            "smtp",
            new { Provider = "smtp" },
            cancellationToken);

        return SmtpCommandResult.Success(ToStatus(settings));
    }

    public async Task<SmtpCommandResult> SetPasswordAsync(string password, CancellationToken cancellationToken)
    {
        var trimmed = password?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return SmtpCommandResult.Invalid("An SMTP password is required.");
        }

        var settings = await store.GetOrCreateAsync(cancellationToken);
        settings.SetPassword(
            protector.Protect(CommunicationSecretPurposes.SmtpPassword, trimmed),
            protector.FormatVersion,
            currentUser.UserId,
            clock.UtcNow);
        await store.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(
            AuditActions.CommunicationProviderSecretSet,
            AuditSubjectTypes.CommunicationProvider,
            "smtp",
            new { Provider = "smtp", Field = "password" },
            cancellationToken);

        return SmtpCommandResult.Success(ToStatus(settings));
    }

    public async Task<SmtpCommandResult> ClearPasswordAsync(CancellationToken cancellationToken)
    {
        var settings = await store.GetOrCreateAsync(cancellationToken);
        settings.ClearPassword(currentUser.UserId, clock.UtcNow);
        await store.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(
            AuditActions.CommunicationProviderSecretCleared,
            AuditSubjectTypes.CommunicationProvider,
            "smtp",
            new { Provider = "smtp", Field = "password" },
            cancellationToken);

        return SmtpCommandResult.Success(ToStatus(settings));
    }

    public async Task<SmtpTestResult> SendTestAsync(string? recipientAddress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(recipientAddress) || !MailAddress.TryCreate(recipientAddress.Trim(), out var recipient))
        {
            return SmtpTestResult.Invalid("A valid test recipient email address is required.");
        }

        var settings = await store.GetOrCreateAsync(cancellationToken);
        if (GetConnectionPrerequisiteError(settings) is { } error)
        {
            return SmtpTestResult.Invalid(error);
        }

        var outcome = await testSender.SendTestAsync(settings, recipient.Address, cancellationToken);
        settings.RecordTestResult(outcome.Succeeded, outcome.Message, currentUser.UserId, clock.UtcNow);
        await store.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(
            AuditActions.CommunicationProviderTested,
            AuditSubjectTypes.CommunicationProvider,
            "smtp",
            new { Provider = "smtp", outcome.Succeeded },
            cancellationToken);

        return SmtpTestResult.Success(ToStatus(settings), outcome);
    }

    private static SmtpStatus ToStatus(SmtpSettings? settings) => settings is null
        ? new SmtpStatus(false, null, null, SmtpSecurityMode.StartTls, null, false, null, null, null, null, null, null)
        : new SmtpStatus(
            settings.IsEnabled,
            settings.Host,
            settings.Port,
            settings.SecurityMode,
            settings.Username,
            settings.HasPassword,
            settings.PasswordSetAtUtc,
            settings.FromAddress,
            settings.FromName,
            settings.LastTestedAtUtc,
            settings.LastTestSucceeded,
            settings.LastTestMessage);

    private static string? GetConfigurationError(SmtpSettings settings)
    {
        if (GetConnectionPrerequisiteError(settings) is { } error)
        {
            return error;
        }

        if (settings.LastTestSucceeded != true)
        {
            return "Send a successful test email for the currently saved configuration before enabling SMTP.";
        }

        return null;
    }

    private static string? GetConnectionPrerequisiteError(SmtpSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Host)) return "An SMTP host is required.";
        if (settings.Port is not (> 0 and <= 65_535)) return "An SMTP port between 1 and 65535 is required.";
        if (string.IsNullOrWhiteSpace(settings.FromAddress) || !MailAddress.TryCreate(settings.FromAddress, out _))
        {
            return "A valid sender email address is required.";
        }

        if (string.IsNullOrWhiteSpace(settings.Username) != !settings.HasPassword)
        {
            return "Provide both an SMTP username and password, or leave both blank for a trusted relay.";
        }

        return null;
    }
}

public sealed record SmtpCommandResult(bool Succeeded, SmtpStatus? Status, string? Error)
{
    public static SmtpCommandResult Success(SmtpStatus status) => new(true, status, null);

    public static SmtpCommandResult Invalid(string error) => new(false, null, error);
}

public sealed record SmtpTestResult(bool Succeeded, SmtpStatus? Status, ConnectionTestOutcome? Outcome, string? Error)
{
    public static SmtpTestResult Success(SmtpStatus status, ConnectionTestOutcome outcome) => new(true, status, outcome, null);

    public static SmtpTestResult Invalid(string error) => new(false, null, null, error);
}
