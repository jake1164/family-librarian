namespace FamilyLibrarian.Contracts.SettingsBackups;

/// <summary>Write-only passphrase used to encrypt a settings archive.</summary>
public sealed record CreateSettingsBackupRequest(string Passphrase);

public sealed record SettingsBackupCountsResponse(
    int CwaSettings,
    int AudiobookshelfSettings,
    int SmtpSettings,
    int PrivateEgressGatewaySettings,
    int ProviderSettings,
    int OidcSettings,
    int AcquisitionPolicySettings);

public sealed record SettingsBackupPreviewResponse(
    DateTimeOffset CreatedUtc,
    string AppVersion,
    string SchemaVersion,
    SettingsBackupCountsResponse Counts,
    bool CanImport,
    IReadOnlyList<string> ExistingSections);

public sealed record SettingsBackupImportResponse(Guid BackupId, SettingsBackupCountsResponse Counts);
