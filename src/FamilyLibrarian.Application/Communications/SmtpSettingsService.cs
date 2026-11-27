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

    /// <summary>
    /// Turns email on or off. Turning it on needs a passing test of the saved settings; when none is recorded, this sends
    /// one itself to <paramref name="testRecipient"/>, the signed-in administrator's own address, so no separate step is needed.
    /// </summary>
    public async Task<SmtpCommandResult> SetEnabledAsync(
        bool enabled, string? testRecipient, CancellationToken cancellationToken)
    {
        var settings = await store.GetOrCreateAsync(cancellationToken);
        if (enabled)
        {
            if (GetConnectionPrerequisiteError(settings) is { } error)
            {
                return SmtpCommandResult.Invalid(error);
            }

            if (settings.LastTestSucceeded != true)
            {
                if (string.IsNullOrWhiteSpace(testRecipient) || !MailAddress.TryCreate(testRecipient.Trim(), out var recipient))
                {
                    return SmtpCommandResult.Invalid(
                        "Your account has no email address to send a test message to. Send a test email below, then turn email on.");
                }

                var outcome = await testSender.SendTestAsync(settings, recipient.Address, cancellationToken);
                await RecordTestAsync(settings, outcome, cancellationToken);
                if (!outcome.Succeeded)
                {
                    return SmtpCommandResult.Invalid(
                        $"Email stayed off. The test message to {recipient.Address} failed: {outcome.Message}");
                }
            }
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
        string? password,
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

        // A null or blank password keeps the stored one, so the form's "leave blank to keep it" holds.
        var trimmedNewPassword = password?.Trim();
        var replacePassword = !string.IsNullOrEmpty(trimmedNewPassword);

        var settings = await store.GetOrCreateAsync(cancellationToken);
        settings.SetSettings(host, port, securityMode, username, fromAddress, fromName, currentUser.UserId, clock.UtcNow);
        if (replacePassword)
        {
            settings.SetPassword(
                protector.Protect(CommunicationSecretPurposes.SmtpPassword, trimmedNewPassword!),
                protector.FormatVersion,
                currentUser.UserId,
                clock.UtcNow);
        }

        await store.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(
            AuditActions.CommunicationProviderSettingsChanged,
            AuditSubjectTypes.CommunicationProvider,
            "smtp",
            new { Provider = "smtp" },
            cancellationToken);
        if (replacePassword)
        {
            await audit.WriteAsync(
                AuditActions.CommunicationProviderSecretSet,
                AuditSubjectTypes.CommunicationProvider,
                "smtp",
                new { Provider = "smtp", Field = "password" },
                cancellationToken);
        }

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

    /// <summary>
    /// Tests the delivery values currently in the administrator's form —
    /// including a freshly typed but unsaved password — without changing the
    /// persisted host/port/username/from-address/from-name. Any omitted
    /// field, the draft password included, falls back to the currently
    /// saved setting, so a caller that only wants to prove the *saved*
    /// configuration works can pass just a recipient address. Only the
    /// pass/fail outcome is recorded onto the persisted settings.
    /// </summary>
    public async Task<SmtpTestResult> SendTestAsync(
        string? recipientAddress,
        string? host,
        int? port,
        SmtpSecurityMode? securityMode,
        string? username,
        string? password,
        string? fromAddress,
        string? fromName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(recipientAddress) || !MailAddress.TryCreate(recipientAddress.Trim(), out var recipient))
        {
            return SmtpTestResult.Invalid("A valid test recipient email address is required.");
        }

        if (port is not null and (< 1 or > 65_535))
        {
            return SmtpTestResult.Invalid("The SMTP port must be between 1 and 65535.");
        }

        if (!string.IsNullOrWhiteSpace(fromAddress) && !MailAddress.TryCreate(fromAddress.Trim(), out _))
        {
            return SmtpTestResult.Invalid("The sender email address is not valid.");
        }

        var persisted = await store.FindAsync(cancellationToken);
        var candidate = new SmtpSettings(clock.UtcNow);
        candidate.SetSettings(
            host ?? persisted?.Host,
            port ?? persisted?.Port,
            securityMode ?? persisted?.SecurityMode ?? SmtpSecurityMode.StartTls,
            username ?? persisted?.Username,
            fromAddress ?? persisted?.FromAddress,
            fromName ?? persisted?.FromName,
            currentUser.UserId,
            clock.UtcNow);

        var trimmedPassword = password?.Trim();
        if (!string.IsNullOrEmpty(trimmedPassword))
        {
            candidate.SetPassword(
                protector.Protect(CommunicationSecretPurposes.SmtpPassword, trimmedPassword),
                protector.FormatVersion,
                currentUser.UserId,
                clock.UtcNow);
        }
        else if (persisted?.HasPassword == true)
        {
            candidate.SetPassword(
                persisted.ProtectedPassword!, persisted.PasswordFormatVersion, currentUser.UserId, clock.UtcNow);
        }

        if (GetConnectionPrerequisiteError(candidate) is { } error)
        {
            return SmtpTestResult.Invalid(error);
        }

        var outcome = await testSender.SendTestAsync(candidate, recipient.Address, cancellationToken);

        // A recorded pass is what lets email be turned on without another test, so a test of unsaved form values must not
        // be recorded against the saved settings -- it would mark settings that were never tried as tested.
        if (persisted is null || !MatchesSavedConfiguration(persisted, candidate))
        {
            return SmtpTestResult.Success(
                ToStatus(persisted),
                outcome with { Message = $"{outcome.Message} These values are not saved yet -- click Save settings to keep them." });
        }

        var settings = await store.GetOrCreateAsync(cancellationToken);
        await RecordTestAsync(settings, outcome, cancellationToken);

        return SmtpTestResult.Success(ToStatus(settings), outcome);
    }

    private async Task RecordTestAsync(SmtpSettings settings, ConnectionTestOutcome outcome, CancellationToken cancellationToken)
    {
        settings.RecordTestResult(outcome.Succeeded, outcome.Message, currentUser.UserId, clock.UtcNow);
        await store.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(
            AuditActions.CommunicationProviderTested,
            AuditSubjectTypes.CommunicationProvider,
            "smtp",
            new { Provider = "smtp", outcome.Succeeded },
            cancellationToken);
    }

    private bool MatchesSavedConfiguration(SmtpSettings persisted, SmtpSettings candidate) =>
        string.Equals(persisted.Host, candidate.Host, StringComparison.Ordinal)
        && persisted.Port == candidate.Port
        && persisted.SecurityMode == candidate.SecurityMode
        && string.Equals(persisted.Username, candidate.Username, StringComparison.Ordinal)
        && string.Equals(persisted.FromAddress, candidate.FromAddress, StringComparison.Ordinal)
        && string.Equals(persisted.FromName, candidate.FromName, StringComparison.Ordinal)
        && string.Equals(RevealPassword(persisted), RevealPassword(candidate), StringComparison.Ordinal);

    private string? RevealPassword(SmtpSettings settings) => settings.HasPassword
        ? protector.Unprotect(CommunicationSecretPurposes.SmtpPassword, settings.ProtectedPassword!, settings.PasswordFormatVersion)
        : null;

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
