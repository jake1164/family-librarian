using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Domain.Audit;
using FamilyLibrarian.Domain.Publishing;

namespace FamilyLibrarian.Application.Publishing;

/// <summary>The administrative commands behind the CWA settings panel, mirroring <c>ProviderAdminService</c>'s shape.</summary>
public sealed class CwaSettingsService(
    ICwaSettingsStore store,
    ICredentialProtector protector,
    ICwaConnectionTester connectionTester,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock)
{
    private const string SftpPrivateKeyPurpose = PublishingSecretPurposes.CwaSftpPrivateKey;
    private const string SftpPassphrasePurpose = PublishingSecretPurposes.CwaSftpPassphrase;
    private const string SftpPasswordPurpose = PublishingSecretPurposes.CwaSftpPassword;
    private const string OpdsPasswordPurpose = PublishingSecretPurposes.CwaOpdsPassword;

    public async Task<CwaStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var settings = await store.FindAsync(cancellationToken);
        return ToStatus(settings);
    }

    public async Task<CwaCommandResult> SetEnabledAsync(
        bool isEnabled, CancellationToken cancellationToken)
    {
        var settings = await store.GetOrCreateAsync(cancellationToken);
        if (isEnabled && GetConfigurationError(settings) is { } configurationError)
        {
            return CwaCommandResult.Invalid(configurationError);
        }

        settings.SetEnabled(isEnabled, currentUser.UserId, clock.UtcNow);
        await store.SaveChangesAsync(cancellationToken);

        await audit.WriteAsync(
            isEnabled ? AuditActions.PublishingDestinationEnabled : AuditActions.PublishingDestinationDisabled,
            AuditSubjectTypes.PublishingDestination,
            "cwa",
            new { Destination = "cwa", Enabled = isEnabled },
            cancellationToken);

        return CwaCommandResult.Success(ToStatus(settings));
    }

    public async Task<CwaCommandResult> SetSettingsAsync(
        CwaTransportMode transportMode,
        string? localIngestPath,
        string? sftpHost,
        int? sftpPort,
        string? sftpUsername,
        string? sftpIngestPath,
        CwaSftpAuthenticationMode sftpAuthenticationMode,
        string? opdsBaseUrl,
        string? opdsUsername,
        CancellationToken cancellationToken)
    {
        if (transportMode == CwaTransportMode.Local && string.IsNullOrWhiteSpace(localIngestPath))
        {
            return CwaCommandResult.Invalid(
                "A local ingest path is required for the Local transport.");
        }

        if (transportMode == CwaTransportMode.Sftp &&
            (string.IsNullOrWhiteSpace(sftpHost) || string.IsNullOrWhiteSpace(sftpUsername) ||
                string.IsNullOrWhiteSpace(sftpIngestPath)))
        {
            return CwaCommandResult.Invalid(
                "An SFTP host, username, and ingest path are required for the SFTP transport.");
        }

        if (sftpPort is <= 0 or > 65_535)
        {
            return CwaCommandResult.Invalid("The SFTP port must be between 1 and 65535.");
        }

        var settings = await store.GetOrCreateAsync(cancellationToken);
        settings.SetSettings(
            transportMode, localIngestPath, sftpHost, sftpPort, sftpUsername, sftpIngestPath,
            sftpAuthenticationMode, opdsBaseUrl, opdsUsername, currentUser.UserId, clock.UtcNow);
        await store.SaveChangesAsync(cancellationToken);

        await audit.WriteAsync(
            AuditActions.PublishingDestinationSettingsChanged,
            AuditSubjectTypes.PublishingDestination,
            "cwa",
            new { Destination = "cwa" },
            cancellationToken);

        return CwaCommandResult.Success(ToStatus(settings));
    }

    public Task<CwaCommandResult> SetSftpPrivateKeyAsync(
        string privateKeyPem, CancellationToken cancellationToken) =>
        SetSecretAsync(
            privateKeyPem,
            SftpPrivateKeyPurpose,
            (settings, protectedValue, formatVersion, hint, actor, at) =>
                settings.SetSftpPrivateKey(protectedValue, formatVersion, hint, actor, at),
            cancellationToken);

    public async Task<CwaCommandResult> ClearSftpPrivateKeyAsync(CancellationToken cancellationToken)
    {
        var settings = await store.GetOrCreateAsync(cancellationToken);
        settings.ClearSftpPrivateKey(currentUser.UserId, clock.UtcNow);
        return await SaveAndAuditSecretClearedAsync(settings, cancellationToken);
    }

    public Task<CwaCommandResult> SetSftpPassphraseAsync(
        string passphrase, CancellationToken cancellationToken) =>
        SetSecretAsync(
            passphrase,
            SftpPassphrasePurpose,
            (settings, protectedValue, formatVersion, hint, actor, at) =>
                settings.SetSftpPassphrase(protectedValue, formatVersion, hint, actor, at),
            cancellationToken);

    public async Task<CwaCommandResult> ClearSftpPassphraseAsync(CancellationToken cancellationToken)
    {
        var settings = await store.GetOrCreateAsync(cancellationToken);
        settings.ClearSftpPassphrase(currentUser.UserId, clock.UtcNow);
        return await SaveAndAuditSecretClearedAsync(settings, cancellationToken);
    }

    public Task<CwaCommandResult> SetSftpPasswordAsync(
        string password, CancellationToken cancellationToken) =>
        SetSecretAsync(
            password,
            SftpPasswordPurpose,
            (settings, protectedValue, formatVersion, hint, actor, at) =>
                settings.SetSftpPassword(protectedValue, formatVersion, null, actor, at),
            cancellationToken);

    public async Task<CwaCommandResult> ClearSftpPasswordAsync(CancellationToken cancellationToken)
    {
        var settings = await store.GetOrCreateAsync(cancellationToken);
        settings.ClearSftpPassword(currentUser.UserId, clock.UtcNow);
        return await SaveAndAuditSecretClearedAsync(settings, cancellationToken);
    }

    public async Task<CwaCommandResult> TrustSftpHostKeyAsync(
        string fingerprint, CancellationToken cancellationToken)
    {
        var trimmed = fingerprint?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return CwaCommandResult.Invalid("An SSH host-key fingerprint is required.");
        }

        var settings = await store.GetOrCreateAsync(cancellationToken);
        if (settings.TransportMode != CwaTransportMode.Sftp || string.IsNullOrWhiteSpace(settings.SftpHost))
        {
            return CwaCommandResult.Invalid("Configure an SFTP host before trusting its host key.");
        }

        settings.TrustSftpHostKey(trimmed, currentUser.UserId, clock.UtcNow);
        await store.SaveChangesAsync(cancellationToken);

        await audit.WriteAsync(
            AuditActions.PublishingDestinationSettingsChanged,
            AuditSubjectTypes.PublishingDestination,
            "cwa",
            new { Destination = "cwa", Field = "sftp-host-key" },
            cancellationToken);

        return CwaCommandResult.Success(ToStatus(settings));
    }

    public Task<CwaCommandResult> SetOpdsPasswordAsync(
        string password, CancellationToken cancellationToken) =>
        SetSecretAsync(
            password,
            OpdsPasswordPurpose,
            (settings, protectedValue, formatVersion, hint, actor, at) =>
                settings.SetOpdsPassword(protectedValue, formatVersion, null, actor, at),
            cancellationToken);

    public async Task<CwaCommandResult> ClearOpdsPasswordAsync(CancellationToken cancellationToken)
    {
        var settings = await store.GetOrCreateAsync(cancellationToken);
        settings.ClearOpdsPassword(currentUser.UserId, clock.UtcNow);
        return await SaveAndAuditSecretClearedAsync(settings, cancellationToken);
    }

    public async Task<CwaConnectionTestResult> TestConnectionAsync(
        CwaConnectionTestTarget target,
        CancellationToken cancellationToken)
    {
        var settings = await store.GetOrCreateAsync(cancellationToken);
        var outcome = await connectionTester.TestAsync(settings, target, cancellationToken);

        settings.RecordTestResult(outcome.Succeeded, outcome.Message, currentUser.UserId, clock.UtcNow);
        await store.SaveChangesAsync(cancellationToken);

        await audit.WriteAsync(
            AuditActions.PublishingDestinationTested,
            AuditSubjectTypes.PublishingDestination,
            "cwa",
            new { Destination = "cwa", Target = target.ToString(), outcome.Succeeded },
            cancellationToken);

        return new CwaConnectionTestResult(ToStatus(settings), outcome);
    }

    /// <summary>
    /// Tests the delivery values currently in the administrator's form without
    /// changing the persisted destination, credential, host-key trust, audit
    /// trail, or last-test status.
    /// </summary>
    public async Task<ConnectionTestOutcome> TestIngestConnectionAsync(
        CwaIngestTestConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (GetDeliveryConfigurationError(
                configuration.TransportMode,
                configuration.LocalIngestPath,
                configuration.SftpHost,
                configuration.SftpPort,
                configuration.SftpUsername,
                configuration.SftpIngestPath) is { } error)
        {
            return new ConnectionTestOutcome(false, error);
        }

        var persisted = await store.FindAsync(cancellationToken);
        var candidate = new CwaSettings(clock.UtcNow);
        candidate.SetSettings(
            configuration.TransportMode,
            configuration.LocalIngestPath,
            configuration.SftpHost,
            configuration.SftpPort,
            configuration.SftpUsername,
            configuration.SftpIngestPath,
            configuration.SftpAuthenticationMode,
            null,
            null,
            currentUser.UserId,
            clock.UtcNow);

        if (configuration.TransportMode == CwaTransportMode.Sftp)
        {
            ApplyTestCredential(candidate, persisted, configuration);
            if (!string.IsNullOrWhiteSpace(configuration.TrustedSftpHostKeyFingerprint))
            {
                candidate.TrustSftpHostKey(
                    configuration.TrustedSftpHostKeyFingerprint,
                    currentUser.UserId,
                    clock.UtcNow);
            }
        }

        return await connectionTester.TestAsync(candidate, CwaConnectionTestTarget.Ingest, cancellationToken);
    }

    private async Task<CwaCommandResult> SetSecretAsync(
        string plaintext,
        string purpose,
        Action<CwaSettings, string, int, string?, Guid?, DateTimeOffset> apply,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(plaintext))
        {
            return CwaCommandResult.Invalid("A value is required.");
        }

        var settings = await store.GetOrCreateAsync(cancellationToken);
        apply(
            settings,
            protector.Protect(purpose, plaintext),
            protector.FormatVersion,
            BuildHint(plaintext),
            currentUser.UserId,
            clock.UtcNow);
        await store.SaveChangesAsync(cancellationToken);

        await audit.WriteAsync(
            AuditActions.PublishingDestinationSecretSet,
            AuditSubjectTypes.PublishingDestination,
            "cwa",
            new { Destination = "cwa", Field = purpose },
            cancellationToken);

        return CwaCommandResult.Success(ToStatus(settings));
    }

    private async Task<CwaCommandResult> SaveAndAuditSecretClearedAsync(
        CwaSettings settings, CancellationToken cancellationToken)
    {
        await store.SaveChangesAsync(cancellationToken);

        await audit.WriteAsync(
            AuditActions.PublishingDestinationSecretCleared,
            AuditSubjectTypes.PublishingDestination,
            "cwa",
            new { Destination = "cwa" },
            cancellationToken);

        return CwaCommandResult.Success(ToStatus(settings));
    }

    private static CwaStatus ToStatus(CwaSettings? settings) => settings is null
        ? new CwaStatus(false, CwaTransportMode.Local, null, null, null, null, null,
            CwaSftpAuthenticationMode.PrivateKey, false, null, null, false, null, null,
            false, null, null, null, null, null, null, false, null, null, null, null, null)
        : new CwaStatus(
            settings.IsEnabled,
            settings.TransportMode,
            settings.LocalIngestPath,
            settings.SftpHost,
            settings.SftpPort,
            settings.SftpUsername,
            settings.SftpIngestPath,
            settings.SftpAuthenticationMode,
            settings.HasSftpPrivateKey,
            settings.SftpPrivateKeyHint,
            settings.SftpPrivateKeySetAtUtc,
            settings.HasSftpPassphrase,
            settings.SftpPassphraseHint,
            settings.SftpPassphraseSetAtUtc,
            settings.HasSftpPassword,
            null,
            settings.SftpPasswordSetAtUtc,
            settings.SftpHostKeyFingerprint,
            settings.SftpHostKeyTrustedAtUtc,
            settings.OpdsBaseUrl,
            settings.OpdsUsername,
            settings.HasOpdsPassword,
            null,
            settings.OpdsPasswordSetAtUtc,
            settings.LastTestedAtUtc,
            settings.LastTestSucceeded,
            settings.LastTestMessage);

    private static string? GetDeliveryConfigurationError(
        CwaTransportMode transportMode,
        string? localIngestPath,
        string? sftpHost,
        int? sftpPort,
        string? sftpUsername,
        string? sftpIngestPath)
    {
        if (transportMode == CwaTransportMode.Local && string.IsNullOrWhiteSpace(localIngestPath))
        {
            return "A local ingest path is required for the Local transport.";
        }

        if (transportMode == CwaTransportMode.Sftp &&
            (string.IsNullOrWhiteSpace(sftpHost) || string.IsNullOrWhiteSpace(sftpUsername) ||
                string.IsNullOrWhiteSpace(sftpIngestPath)))
        {
            return "An SFTP host, username, and ingest path are required for the SFTP transport.";
        }

        return sftpPort is <= 0 or > 65_535 ? "The SFTP port must be between 1 and 65535." : null;
    }

    private void ApplyTestCredential(
        CwaSettings candidate,
        CwaSettings? persisted,
        CwaIngestTestConfiguration configuration)
    {
        if (configuration.SftpAuthenticationMode == CwaSftpAuthenticationMode.Password)
        {
            if (!string.IsNullOrWhiteSpace(configuration.SftpPassword))
            {
                candidate.SetSftpPassword(
                    protector.Protect(SftpPasswordPurpose, configuration.SftpPassword),
                    protector.FormatVersion,
                    null,
                    currentUser.UserId,
                    clock.UtcNow);
            }
            else if (persisted?.HasSftpPassword == true)
            {
                candidate.SetSftpPassword(
                    persisted.ProtectedSftpPassword!,
                    persisted.SftpPasswordFormatVersion,
                    null,
                    currentUser.UserId,
                    clock.UtcNow);
            }

            return;
        }

        if (!string.IsNullOrWhiteSpace(configuration.SftpPrivateKey))
        {
            candidate.SetSftpPrivateKey(
                protector.Protect(SftpPrivateKeyPurpose, configuration.SftpPrivateKey),
                protector.FormatVersion,
                null,
                currentUser.UserId,
                clock.UtcNow);
        }
        else if (persisted?.HasSftpPrivateKey == true)
        {
            candidate.SetSftpPrivateKey(
                persisted.ProtectedSftpPrivateKey!,
                persisted.SftpPrivateKeyFormatVersion,
                null,
                currentUser.UserId,
                clock.UtcNow);
        }

        if (!string.IsNullOrWhiteSpace(configuration.SftpPassphrase))
        {
            candidate.SetSftpPassphrase(
                protector.Protect(SftpPassphrasePurpose, configuration.SftpPassphrase),
                protector.FormatVersion,
                null,
                currentUser.UserId,
                clock.UtcNow);
        }
        else if (persisted?.HasSftpPassphrase == true)
        {
            candidate.SetSftpPassphrase(
                persisted.ProtectedSftpPassphrase!,
                persisted.SftpPassphraseFormatVersion,
                null,
                currentUser.UserId,
                clock.UtcNow);
        }
    }

    private static string? GetConfigurationError(CwaSettings settings)
    {
        if (settings.TransportMode == CwaTransportMode.Local)
        {
            return string.IsNullOrWhiteSpace(settings.LocalIngestPath)
                ? "A local ingest path is required before enabling CWA."
                : null;
        }

        if (string.IsNullOrWhiteSpace(settings.SftpHost) ||
            string.IsNullOrWhiteSpace(settings.SftpUsername) ||
            string.IsNullOrWhiteSpace(settings.SftpIngestPath))
        {
            return "An SFTP host, username, and ingest path are required before enabling CWA.";
        }

        if (string.IsNullOrWhiteSpace(settings.SftpHostKeyFingerprint))
        {
            return "Test the SFTP connection and explicitly trust its SSH host key before enabling CWA.";
        }

        return settings.SftpAuthenticationMode switch
        {
            CwaSftpAuthenticationMode.Password when !settings.HasSftpPassword =>
                "An SFTP password is required before enabling CWA.",
            CwaSftpAuthenticationMode.PrivateKey when !settings.HasSftpPrivateKey =>
                "An SFTP private key is required before enabling CWA.",
            _ => null
        };
    }

    private static string? BuildHint(string value) => value.Length <= 4 ? null : value[^4..];
}

public sealed record CwaCommandResult(PublishingCommandOutcome Outcome, CwaStatus? Status, string? Error)
{
    public static CwaCommandResult Success(CwaStatus status) => new(PublishingCommandOutcome.Success, status, null);

    public static CwaCommandResult Invalid(string error) => new(PublishingCommandOutcome.Invalid, null, error);
}

public sealed record CwaConnectionTestResult(CwaStatus Status, ConnectionTestOutcome Outcome);

public enum PublishingCommandOutcome
{
    Success,
    Invalid
}
