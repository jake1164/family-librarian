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

        var settings = await store.GetOrCreateAsync(cancellationToken);
        settings.SetSettings(
            transportMode, localIngestPath, sftpHost, sftpPort, sftpUsername, sftpIngestPath,
            opdsBaseUrl, opdsUsername, currentUser.UserId, clock.UtcNow);
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

    public Task<CwaCommandResult> SetOpdsPasswordAsync(
        string password, CancellationToken cancellationToken) =>
        SetSecretAsync(
            password,
            OpdsPasswordPurpose,
            (settings, protectedValue, formatVersion, hint, actor, at) =>
                settings.SetOpdsPassword(protectedValue, formatVersion, hint, actor, at),
            cancellationToken);

    public async Task<CwaCommandResult> ClearOpdsPasswordAsync(CancellationToken cancellationToken)
    {
        var settings = await store.GetOrCreateAsync(cancellationToken);
        settings.ClearOpdsPassword(currentUser.UserId, clock.UtcNow);
        return await SaveAndAuditSecretClearedAsync(settings, cancellationToken);
    }

    public async Task<CwaCommandResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        var settings = await store.GetOrCreateAsync(cancellationToken);
        var outcome = await connectionTester.TestAsync(settings, cancellationToken);

        settings.RecordTestResult(outcome.Succeeded, outcome.Message, currentUser.UserId, clock.UtcNow);
        await store.SaveChangesAsync(cancellationToken);

        await audit.WriteAsync(
            AuditActions.PublishingDestinationTested,
            AuditSubjectTypes.PublishingDestination,
            "cwa",
            new { Destination = "cwa", outcome.Succeeded },
            cancellationToken);

        return CwaCommandResult.Success(ToStatus(settings));
    }

    private async Task<CwaCommandResult> SetSecretAsync(
        string plaintext,
        string purpose,
        Action<CwaSettings, string, int, string?, Guid?, DateTimeOffset> apply,
        CancellationToken cancellationToken)
    {
        var trimmed = plaintext?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return CwaCommandResult.Invalid("A value is required.");
        }

        var settings = await store.GetOrCreateAsync(cancellationToken);
        apply(
            settings,
            protector.Protect(purpose, trimmed),
            protector.FormatVersion,
            BuildHint(trimmed),
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
            false, null, null, false, null, null, null, null, false, null, null, null, null, null)
        : new CwaStatus(
            settings.IsEnabled,
            settings.TransportMode,
            settings.LocalIngestPath,
            settings.SftpHost,
            settings.SftpPort,
            settings.SftpUsername,
            settings.SftpIngestPath,
            settings.HasSftpPrivateKey,
            settings.SftpPrivateKeyHint,
            settings.SftpPrivateKeySetAtUtc,
            settings.HasSftpPassphrase,
            settings.SftpPassphraseHint,
            settings.SftpPassphraseSetAtUtc,
            settings.OpdsBaseUrl,
            settings.OpdsUsername,
            settings.HasOpdsPassword,
            settings.OpdsPasswordHint,
            settings.OpdsPasswordSetAtUtc,
            settings.LastTestedAtUtc,
            settings.LastTestSucceeded,
            settings.LastTestMessage);

    private static string? BuildHint(string value) => value.Length <= 4 ? null : value[^4..];
}

public sealed record CwaCommandResult(PublishingCommandOutcome Outcome, CwaStatus? Status, string? Error)
{
    public static CwaCommandResult Success(CwaStatus status) => new(PublishingCommandOutcome.Success, status, null);

    public static CwaCommandResult Invalid(string error) => new(PublishingCommandOutcome.Invalid, null, error);
}

public enum PublishingCommandOutcome
{
    Success,
    Invalid
}
