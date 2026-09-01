using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FamilyLibrarian.Application.Accounts;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Communications;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Accounts;
using FamilyLibrarian.Domain.Audit;
using FamilyLibrarian.Domain.Communications;
using FamilyLibrarian.Domain.Policy;
using FamilyLibrarian.Domain.Providers;
using FamilyLibrarian.Domain.Publishing;
using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;

namespace FamilyLibrarian.Infrastructure.SettingsBackups;

/// <summary>
/// Creates and applies the small, portable configuration archive used to seed a
/// fresh instance. It deliberately has no database-dump or full-restore mode.
/// </summary>
public sealed class SettingsBackupService(
    AppDbContext database,
    ICredentialProtector credentialProtector,
    IOidcRuntimeSettingsCache oidcRuntimeCache,
    IOidcOptionsInvalidator oidcOptionsInvalidator,
    IPrivateEgressGatewayRuntimeCache gatewayRuntimeCache,
    IAuditWriter audit,
    IClock clock)
{
    public const string FileExtension = ".family-librarian-settings";

    private const string FormatIdentifier = "family-librarian-settings-backup";
    private const int ArchiveFormatVersion = 1;
    private const int SettingsDocumentVersion = 1;
    private const int MaxArchiveBytes = 1_048_576;
    private const int SaltBytes = 16;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int KeyBytes = 32;
    private const int ArgonMemoryKiB = 65_536;
    private const int ArgonIterations = 3;
    private const int ArgonParallelism = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public async Task<byte[]> ExportAsync(string passphrase, CancellationToken cancellationToken)
    {
        ValidatePassphrase(passphrase);

        var document = new SettingsDocument(
            SettingsDocumentVersion,
            ToCwaDocument(await database.CwaSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken)),
            ToAudiobookshelfDocument(await database.AudiobookshelfSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken)),
            ToSmtpDocument(await database.SmtpSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken)),
            ToGatewayDocument(await database.PrivateEgressGatewaySettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken)),
            (await database.ProviderSettings.AsNoTracking().OrderBy(setting => setting.ProviderId).ToArrayAsync(cancellationToken))
                .Select(ToProviderDocument).ToArray(),
            ToOidcDocument(await database.OidcSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken)),
            ToPolicyDocument(await database.AcquisitionPolicySettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken)));

        var migrations = await database.Database.GetAppliedMigrationsAsync(cancellationToken);
        var payload = new SettingsArchivePayload(
            new SettingsBackupManifest(
                Guid.NewGuid(),
                clock.UtcNow,
                typeof(SettingsBackupService).Assembly.GetName().Version?.ToString() ?? "unknown",
                migrations.LastOrDefault() ?? "unknown",
                // ASP.NET Core Data Protection deliberately abstracts the active
                // key details. Import therefore proves compatibility by actually
                // unprotecting every stored credential before it writes anything.
                null,
                null,
                ["CwaSettings", "AudiobookshelfSettings", "SmtpSettings", "PrivateEgressGatewaySettings", "ProviderSettings", "OidcSettings", "AcquisitionPolicySettings"]),
            document);

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        try
        {
            return Encrypt(plaintext, passphrase);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public async Task<SettingsBackupPreview> PreviewAsync(
        ReadOnlyMemory<byte> archive,
        string passphrase,
        CancellationToken cancellationToken)
    {
        var payload = ReadPayload(archive.Span, passphrase);
        ValidateCredentials(payload.Document);
        var existing = await GetExistingSectionsAsync(cancellationToken);
        return new SettingsBackupPreview(
            payload.Manifest.CreatedUtc,
            payload.Manifest.AppVersion,
            payload.Manifest.SchemaVersion,
            ToCounts(payload.Document),
            existing.Count == 0,
            existing);
    }

    public async Task<SettingsBackupImportResult> ImportAsync(
        ReadOnlyMemory<byte> archive,
        string passphrase,
        CancellationToken cancellationToken)
    {
        var payload = ReadPayload(archive.Span, passphrase);
        ValidateCredentials(payload.Document);

        var existing = await GetExistingSectionsAsync(cancellationToken);
        if (existing.Count != 0)
        {
            throw new SettingsBackupException(
                $"Settings import requires a clean configuration. Existing sections: {string.Join(", ", existing)}.");
        }

        await using var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        existing = await GetExistingSectionsAsync(cancellationToken);
        if (existing.Count != 0)
        {
            throw new SettingsBackupException(
                $"Settings import requires a clean configuration. Existing sections: {string.Join(", ", existing)}.");
        }

        ApplyDocument(payload.Document);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        RefreshRuntimeCaches(payload.Document);

        var counts = ToCounts(payload.Document);
        await audit.WriteAsync(
            AuditActions.SettingsBackupImported,
            AuditSubjectTypes.SettingsBackup,
            null,
            new { payload.Manifest.BackupId, Sections = counts },
            cancellationToken);

        return new SettingsBackupImportResult(payload.Manifest.BackupId, counts);
    }

    private static byte[] Encrypt(byte[] plaintext, string passphrase)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var header = new SettingsArchiveHeader(
            FormatIdentifier,
            ArchiveFormatVersion,
            "settings",
            "argon2id",
            ArgonMemoryKiB,
            ArgonIterations,
            ArgonParallelism,
            Convert.ToBase64String(salt),
            Convert.ToBase64String(nonce));
        var aad = SerializeHeader(header);
        var key = DeriveKey(passphrase, salt, header);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagBytes];

        try
        {
            using var cipher = new AesGcm(key, TagBytes);
            cipher.Encrypt(nonce, plaintext, ciphertext, tag, aad);
            return JsonSerializer.SerializeToUtf8Bytes(
                new SettingsArchive(header, Convert.ToBase64String(ciphertext), Convert.ToBase64String(tag)),
                JsonOptions);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(aad);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
        }
    }

    private static SettingsArchivePayload ReadPayload(ReadOnlySpan<byte> archiveBytes, string passphrase)
    {
        ValidatePassphrase(passphrase);
        if (archiveBytes.Length == 0 || archiveBytes.Length > MaxArchiveBytes)
        {
            throw SettingsBackupException.InvalidArchive();
        }

        try
        {
            var archive = JsonSerializer.Deserialize<SettingsArchive>(archiveBytes, JsonOptions)
                ?? throw SettingsBackupException.InvalidArchive();
            ValidateHeader(archive.Header);

            var salt = Convert.FromBase64String(archive.Header.Salt);
            var nonce = Convert.FromBase64String(archive.Header.Nonce);
            var ciphertext = Convert.FromBase64String(archive.Ciphertext);
            var tag = Convert.FromBase64String(archive.Tag);
            var aad = SerializeHeader(archive.Header);
            var key = DeriveKey(passphrase, salt, archive.Header);
            var plaintext = new byte[ciphertext.Length];

            try
            {
                using var cipher = new AesGcm(key, TagBytes);
                cipher.Decrypt(nonce, ciphertext, tag, plaintext, aad);
                var payload = JsonSerializer.Deserialize<SettingsArchivePayload>(plaintext, JsonOptions)
                    ?? throw SettingsBackupException.InvalidArchive();
                ValidatePayload(payload);
                return payload;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
                CryptographicOperations.ZeroMemory(salt);
                CryptographicOperations.ZeroMemory(nonce);
                CryptographicOperations.ZeroMemory(ciphertext);
                CryptographicOperations.ZeroMemory(tag);
                CryptographicOperations.ZeroMemory(aad);
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        catch (SettingsBackupException)
        {
            throw;
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException or JsonException or ArgumentException)
        {
            // Treat bad authentication tags, corrupt ciphertext, and an incorrect
            // passphrase identically so this endpoint cannot become a passphrase oracle.
            throw SettingsBackupException.InvalidArchive();
        }
    }

    private static void ValidateHeader(SettingsArchiveHeader header)
    {
        if (header is null ||
            header.FormatIdentifier != FormatIdentifier ||
            header.FormatVersion != ArchiveFormatVersion ||
            header.Scope != "settings" ||
            header.Kdf != "argon2id" ||
            header.MemoryKiB != ArgonMemoryKiB ||
            header.Iterations != ArgonIterations ||
            header.Parallelism != ArgonParallelism)
        {
            throw SettingsBackupException.InvalidArchive();
        }

        if (Convert.FromBase64String(header.Salt).Length != SaltBytes ||
            Convert.FromBase64String(header.Nonce).Length != NonceBytes)
        {
            throw SettingsBackupException.InvalidArchive();
        }
    }

    private static void ValidatePayload(SettingsArchivePayload payload)
    {
        if (payload.Manifest is null || payload.Document is null ||
            payload.Document.Version != SettingsDocumentVersion ||
            payload.Manifest.SettingsEntities is null ||
            payload.Document.ProviderSettings is null)
        {
            throw new SettingsBackupException("The settings archive has an unsupported document version.");
        }
    }

    private static byte[] SerializeHeader(SettingsArchiveHeader header) =>
        JsonSerializer.SerializeToUtf8Bytes(header, JsonOptions);

    private static byte[] DeriveKey(string passphrase, byte[] salt, SettingsArchiveHeader header)
    {
        var password = Encoding.UTF8.GetBytes(passphrase);
        var key = new byte[KeyBytes];
        var parameters = new Argon2Parameters.Builder(Argon2Parameters.Argon2id)
            .WithVersion(Argon2Parameters.Version13)
            .WithSalt(salt)
            .WithMemoryAsKB(header.MemoryKiB)
            .WithIterations(header.Iterations)
            .WithParallelism(header.Parallelism)
            .Build();

        try
        {
            var generator = new Argon2BytesGenerator();
            generator.Init(parameters);
            generator.GenerateBytes(password, key);
            return key;
        }
        finally
        {
            parameters.Clear();
            CryptographicOperations.ZeroMemory(password);
        }
    }

    private async Task<List<string>> GetExistingSectionsAsync(CancellationToken cancellationToken)
    {
        var existing = new List<string>();
        if (await database.CwaSettings.AnyAsync(cancellationToken)) existing.Add("CwaSettings");
        if (await database.AudiobookshelfSettings.AnyAsync(cancellationToken)) existing.Add("AudiobookshelfSettings");
        if (await database.SmtpSettings.AnyAsync(cancellationToken)) existing.Add("SmtpSettings");
        if (await database.PrivateEgressGatewaySettings.AnyAsync(cancellationToken)) existing.Add("PrivateEgressGatewaySettings");
        if (await database.ProviderSettings.AnyAsync(cancellationToken)) existing.Add("ProviderSettings");
        if (await database.OidcSettings.AnyAsync(cancellationToken)) existing.Add("OidcSettings");
        if (await database.AcquisitionPolicySettings.AnyAsync(cancellationToken)) existing.Add("AcquisitionPolicySettings");
        return existing;
    }

    private void ValidateCredentials(SettingsDocument document)
    {
        foreach (var provider in document.ProviderSettings)
        {
            ValidateCredential(provider.ProviderId, provider.ProtectedCredential, provider.CredentialFormatVersion);
        }

        if (document.Cwa is { } cwa)
        {
            ValidateCredential(PublishingSecretPurposes.CwaSftpPrivateKey, cwa.SftpPrivateKey?.ProtectedValue, cwa.SftpPrivateKey?.FormatVersion ?? 0);
            ValidateCredential(PublishingSecretPurposes.CwaSftpPassphrase, cwa.SftpPassphrase?.ProtectedValue, cwa.SftpPassphrase?.FormatVersion ?? 0);
            ValidateCredential(PublishingSecretPurposes.CwaSftpPassword, cwa.SftpPassword?.ProtectedValue, cwa.SftpPassword?.FormatVersion ?? 0);
            ValidateCredential(PublishingSecretPurposes.CwaOpdsPassword, cwa.OpdsPassword?.ProtectedValue, cwa.OpdsPassword?.FormatVersion ?? 0);
        }

        if (document.Audiobookshelf is { } audiobookshelf)
        {
            ValidateCredential(PublishingSecretPurposes.AudiobookshelfApiToken, audiobookshelf.ApiToken?.ProtectedValue, audiobookshelf.ApiToken?.FormatVersion ?? 0);
        }

        if (document.Smtp is { } smtp)
        {
            ValidateCredential(CommunicationSecretPurposes.SmtpPassword, smtp.Password?.ProtectedValue, smtp.Password?.FormatVersion ?? 0);
        }

        if (document.Oidc is { } oidc)
        {
            ValidateCredential(AuthenticationSecretPurposes.OidcClientSecret, oidc.ClientSecret?.ProtectedValue, oidc.ClientSecret?.FormatVersion ?? 0);
        }
    }

    private void ValidateCredential(string purpose, string? protectedValue, int formatVersion)
    {
        if (!string.IsNullOrWhiteSpace(protectedValue) &&
            credentialProtector.Unprotect(purpose, protectedValue, formatVersion) is null)
        {
            throw new SettingsBackupException(
                "The settings archive contains credentials that cannot be decrypted by this instance's Data Protection key ring.");
        }
    }

    private void ApplyDocument(SettingsDocument document)
    {
        if (document.Cwa is { } cwa) database.CwaSettings.Add(CreateCwa(cwa));
        if (document.Audiobookshelf is { } audiobookshelf) database.AudiobookshelfSettings.Add(CreateAudiobookshelf(audiobookshelf));
        if (document.Smtp is { } smtp) database.SmtpSettings.Add(CreateSmtp(smtp));
        if (document.Gateway is { } gateway) database.PrivateEgressGatewaySettings.Add(CreateGateway(gateway));
        foreach (var provider in document.ProviderSettings) database.ProviderSettings.Add(CreateProvider(provider));
        if (document.Oidc is { } oidc) database.OidcSettings.Add(CreateOidc(oidc));
        if (document.AcquisitionPolicy is { } policy) database.AcquisitionPolicySettings.Add(CreatePolicy(policy));
    }

    private CwaSettings CreateCwa(CwaSettingsDocument source)
    {
        var settings = new CwaSettings(clock.UtcNow);
        settings.SetSettings(ParseEnum<CwaTransportMode>(source.TransportMode), source.LocalIngestPath, source.SftpHost,
            source.SftpPort, source.SftpUsername, source.SftpIngestPath,
            ParseEnum<CwaSftpAuthenticationMode>(source.SftpAuthenticationMode), source.OpdsBaseUrl, source.OpdsUsername, null, clock.UtcNow);
        ApplySecret(source.SftpPrivateKey, settings.SetSftpPrivateKey, null, clock.UtcNow);
        ApplySecret(source.SftpPassphrase, settings.SetSftpPassphrase, null, clock.UtcNow);
        ApplySecret(source.SftpPassword, settings.SetSftpPassword, null, clock.UtcNow);
        ApplySecret(source.OpdsPassword, settings.SetOpdsPassword, null, clock.UtcNow);
        if (!string.IsNullOrWhiteSpace(source.SftpHostKeyFingerprint)) settings.TrustSftpHostKey(source.SftpHostKeyFingerprint, null, clock.UtcNow);
        settings.SetEnabled(source.IsEnabled, null, clock.UtcNow);
        return settings;
    }

    private AudiobookshelfSettings CreateAudiobookshelf(AudiobookshelfSettingsDocument source)
    {
        var settings = new AudiobookshelfSettings(clock.UtcNow);
        settings.SetSettings(source.BaseUrl, source.LibraryId, source.FolderId, null, clock.UtcNow);
        ApplySecret(source.ApiToken, settings.SetApiToken, null, clock.UtcNow);
        settings.SetEnabled(source.IsEnabled, null, clock.UtcNow);
        return settings;
    }

    private SmtpSettings CreateSmtp(SmtpSettingsDocument source)
    {
        var settings = new SmtpSettings(clock.UtcNow);
        settings.SetSettings(
            source.Host,
            source.Port,
            ParseEnum<SmtpSecurityMode>(source.SecurityMode),
            source.Username,
            source.FromAddress,
            source.FromName,
            null,
            clock.UtcNow);
        ApplySecret(
            source.Password?.ProtectedValue,
            source.Password?.FormatVersion ?? 0,
            source.Password?.Hint,
            (value, formatVersion, _, actor, at) => settings.SetPassword(value, formatVersion, actor, at),
            null,
            clock.UtcNow);
        settings.SetEnabled(source.IsEnabled, null, clock.UtcNow);
        return settings;
    }

    private PrivateEgressGatewaySettings CreateGateway(PrivateEgressGatewaySettingsDocument source)
    {
        var settings = new PrivateEgressGatewaySettings(clock.UtcNow);
        settings.SetGatewayEndpoint(source.GatewayEndpoint, null, clock.UtcNow);
        settings.SetEnabled(source.IsEnabled, null, clock.UtcNow);
        return settings;
    }

    private ProviderSetting CreateProvider(ProviderSettingDocument source)
    {
        var settings = new ProviderSetting(source.ProviderId, clock.UtcNow);
        ApplySecret(source.ProtectedCredential, source.CredentialFormatVersion, source.CredentialHint, settings.SetCredential, null, clock.UtcNow);
        settings.SetEnabled(source.IsEnabled, null, clock.UtcNow);
        return settings;
    }

    private OidcSettings CreateOidc(OidcSettingsDocument source)
    {
        var settings = new OidcSettings(clock.UtcNow);
        settings.SetSettings(source.DisplayName, source.Authority, source.ClientId, source.Scopes, source.MatchClaimName,
            source.AdminClaimName, source.AdminClaimValues, source.AutoCreateAccounts, null, clock.UtcNow);
        ApplySecret(source.ClientSecret, settings.SetClientSecret, null, clock.UtcNow);
        settings.SetEnabled(source.IsEnabled, null, clock.UtcNow);
        return settings;
    }

    private AcquisitionPolicySettings CreatePolicy(AcquisitionPolicySettingsDocument source)
    {
        var settings = new AcquisitionPolicySettings(clock.UtcNow);
        settings.SetDefaultProfile(source.DefaultProfileId, null, clock.UtcNow);
        return settings;
    }

    private void RefreshRuntimeCaches(SettingsDocument document)
    {
        var oidc = document.Oidc;
        if (oidc is null)
        {
            oidcRuntimeCache.Refresh(OidcRuntimeSettings.Disabled);
        }
        else
        {
            oidcRuntimeCache.Refresh(new OidcRuntimeSettings(
                oidc.IsEnabled,
                oidc.DisplayName,
                oidc.Authority,
                oidc.ClientId,
                oidc.ClientSecret is null ? null : credentialProtector.Unprotect(
                    AuthenticationSecretPurposes.OidcClientSecret, oidc.ClientSecret.ProtectedValue, oidc.ClientSecret.FormatVersion),
                oidc.Scopes,
                oidc.MatchClaimName,
                oidc.AdminClaimName,
                oidc.AdminClaimValues,
                oidc.AutoCreateAccounts,
                // Local-login lockout is intentionally not portable. A settings
                // archive must not remove a fresh instance's recovery path.
                false));
        }

        oidcOptionsInvalidator.Invalidate();
        gatewayRuntimeCache.Refresh(document.Gateway is { } gateway
            ? new PrivateEgressGatewayRuntimeState(gateway.IsEnabled, gateway.GatewayEndpoint, false)
            : PrivateEgressGatewayRuntimeState.Disabled);
    }

    private static void ApplySecret(
        SecretDocument? source,
        Action<string, int, string?, Guid?, DateTimeOffset> apply,
        Guid? actor,
        DateTimeOffset at)
    {
        if (source is not null) apply(source.ProtectedValue, source.FormatVersion, source.Hint, actor, at);
    }

    private static void ApplySecret(
        string? protectedValue,
        int formatVersion,
        string? hint,
        Action<string, int, string?, Guid?, DateTimeOffset> apply,
        Guid? actor,
        DateTimeOffset at)
    {
        if (!string.IsNullOrWhiteSpace(protectedValue)) apply(protectedValue, formatVersion, hint, actor, at);
    }

    private static TEnum ParseEnum<TEnum>(string value) where TEnum : struct, Enum
    {
        if (!Enum.TryParse<TEnum>(value, ignoreCase: false, out var result) || !Enum.IsDefined(result))
        {
            throw new SettingsBackupException("The settings archive contains an unsupported configuration value.");
        }

        return result;
    }

    private static void ValidatePassphrase(string passphrase)
    {
        if (string.IsNullOrWhiteSpace(passphrase) || passphrase.Length < 12)
        {
            throw new SettingsBackupException("A settings archive passphrase must contain at least 12 characters.");
        }
    }

    private static SettingsBackupCounts ToCounts(SettingsDocument document) => new(
        document.Cwa is null ? 0 : 1,
        document.Audiobookshelf is null ? 0 : 1,
        document.Smtp is null ? 0 : 1,
        document.Gateway is null ? 0 : 1,
        document.ProviderSettings.Count,
        document.Oidc is null ? 0 : 1,
        document.AcquisitionPolicy is null ? 0 : 1);

    private static CwaSettingsDocument? ToCwaDocument(CwaSettings? settings) => settings is null ? null : new(
        settings.IsEnabled, settings.TransportMode.ToString(), settings.LocalIngestPath, settings.SftpHost, settings.SftpPort,
        settings.SftpUsername, settings.SftpIngestPath, settings.SftpAuthenticationMode.ToString(),
        ToSecret(settings.ProtectedSftpPrivateKey, settings.SftpPrivateKeyFormatVersion, settings.SftpPrivateKeyHint),
        ToSecret(settings.ProtectedSftpPassphrase, settings.SftpPassphraseFormatVersion, settings.SftpPassphraseHint),
        ToSecret(settings.ProtectedSftpPassword, settings.SftpPasswordFormatVersion, settings.SftpPasswordHint),
        settings.SftpHostKeyFingerprint, settings.OpdsBaseUrl, settings.OpdsUsername,
        ToSecret(settings.ProtectedOpdsPassword, settings.OpdsPasswordFormatVersion, settings.OpdsPasswordHint));

    private static AudiobookshelfSettingsDocument? ToAudiobookshelfDocument(AudiobookshelfSettings? settings) => settings is null ? null : new(
        settings.IsEnabled, settings.BaseUrl, settings.LibraryId, settings.FolderId,
        ToSecret(settings.ProtectedApiToken, settings.ApiTokenFormatVersion, settings.ApiTokenHint));

    private static SmtpSettingsDocument? ToSmtpDocument(SmtpSettings? settings) => settings is null ? null : new(
        settings.IsEnabled,
        settings.Host,
        settings.Port,
        settings.SecurityMode.ToString(),
        settings.Username,
        ToSecret(settings.ProtectedPassword, settings.PasswordFormatVersion, null),
        settings.FromAddress,
        settings.FromName);

    private static PrivateEgressGatewaySettingsDocument? ToGatewayDocument(PrivateEgressGatewaySettings? settings) =>
        settings is null ? null : new(settings.IsEnabled, settings.GatewayEndpoint);

    private static ProviderSettingDocument ToProviderDocument(ProviderSetting setting) => new(
        setting.ProviderId, setting.IsEnabled, setting.ProtectedCredential, setting.CredentialFormatVersion, setting.CredentialHint);

    private static OidcSettingsDocument? ToOidcDocument(OidcSettings? settings) => settings is null ? null : new(
        settings.IsEnabled, settings.DisplayName, settings.Authority, settings.ClientId,
        ToSecret(settings.ProtectedClientSecret, settings.ClientSecretFormatVersion, settings.ClientSecretHint),
        settings.Scopes, settings.MatchClaimName, settings.AdminClaimName, settings.AdminClaimValues, settings.AutoCreateAccounts);

    private static AcquisitionPolicySettingsDocument? ToPolicyDocument(AcquisitionPolicySettings? settings) =>
        settings is null ? null : new(settings.DefaultProfileId);

    private static SecretDocument? ToSecret(string? protectedValue, int formatVersion, string? hint) =>
        string.IsNullOrWhiteSpace(protectedValue) ? null : new(protectedValue, formatVersion, hint);
}

public sealed class SettingsBackupException(string message) : Exception(message)
{
    internal static SettingsBackupException InvalidArchive() =>
        new("The settings archive could not be decrypted or is invalid.");
}

public sealed record SettingsBackupPreview(
    DateTimeOffset CreatedUtc,
    string AppVersion,
    string SchemaVersion,
    SettingsBackupCounts Counts,
    bool CanImport,
    IReadOnlyList<string> ExistingSections);

public sealed record SettingsBackupImportResult(Guid BackupId, SettingsBackupCounts Counts);

public sealed record SettingsBackupCounts(
    int CwaSettings,
    int AudiobookshelfSettings,
    int SmtpSettings,
    int PrivateEgressGatewaySettings,
    int ProviderSettings,
    int OidcSettings,
    int AcquisitionPolicySettings);

internal sealed record SettingsArchive(SettingsArchiveHeader Header, string Ciphertext, string Tag);

internal sealed record SettingsArchiveHeader(
    string FormatIdentifier,
    int FormatVersion,
    string Scope,
    string Kdf,
    int MemoryKiB,
    int Iterations,
    int Parallelism,
    string Salt,
    string Nonce);

internal sealed record SettingsArchivePayload(SettingsBackupManifest Manifest, SettingsDocument Document);

internal sealed record SettingsBackupManifest(
    Guid BackupId,
    DateTimeOffset CreatedUtc,
    string AppVersion,
    string SchemaVersion,
    string? EncryptionKeyId,
    string? EncryptionKeyFingerprint,
    IReadOnlyList<string> SettingsEntities);

internal sealed record SettingsDocument(
    int Version,
    CwaSettingsDocument? Cwa,
    AudiobookshelfSettingsDocument? Audiobookshelf,
    SmtpSettingsDocument? Smtp,
    PrivateEgressGatewaySettingsDocument? Gateway,
    IReadOnlyList<ProviderSettingDocument> ProviderSettings,
    OidcSettingsDocument? Oidc,
    AcquisitionPolicySettingsDocument? AcquisitionPolicy);

internal sealed record SecretDocument(string ProtectedValue, int FormatVersion, string? Hint);

internal sealed record CwaSettingsDocument(
    bool IsEnabled,
    string TransportMode,
    string? LocalIngestPath,
    string? SftpHost,
    int? SftpPort,
    string? SftpUsername,
    string? SftpIngestPath,
    string SftpAuthenticationMode,
    SecretDocument? SftpPrivateKey,
    SecretDocument? SftpPassphrase,
    SecretDocument? SftpPassword,
    string? SftpHostKeyFingerprint,
    string? OpdsBaseUrl,
    string? OpdsUsername,
    SecretDocument? OpdsPassword);

internal sealed record AudiobookshelfSettingsDocument(
    bool IsEnabled,
    string? BaseUrl,
    string? LibraryId,
    string? FolderId,
    SecretDocument? ApiToken);

internal sealed record SmtpSettingsDocument(
    bool IsEnabled,
    string? Host,
    int? Port,
    string SecurityMode,
    string? Username,
    SecretDocument? Password,
    string? FromAddress,
    string? FromName);

internal sealed record PrivateEgressGatewaySettingsDocument(bool IsEnabled, string? GatewayEndpoint);

internal sealed record ProviderSettingDocument(
    string ProviderId,
    bool IsEnabled,
    string? ProtectedCredential,
    int CredentialFormatVersion,
    string? CredentialHint);

internal sealed record OidcSettingsDocument(
    bool IsEnabled,
    string DisplayName,
    string? Authority,
    string? ClientId,
    SecretDocument? ClientSecret,
    string Scopes,
    string MatchClaimName,
    string? AdminClaimName,
    string? AdminClaimValues,
    bool AutoCreateAccounts);

internal sealed record AcquisitionPolicySettingsDocument(string DefaultProfileId);
