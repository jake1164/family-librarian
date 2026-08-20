namespace FamilyLibrarian.Contracts.Publishing;

public sealed record SetPublishingEnabledRequest(bool Enabled);

/// <summary>A write-only secret value. Never echoed back — only a hint/set-at timestamp appears in the status response.</summary>
public sealed record SetPublishingSecretRequest(string Value);

public sealed record PublishingConnectionTestResponse(
    bool Succeeded,
    string Message,
    bool RequiresSftpHostKeyTrust = false,
    string? SftpHostKeyFingerprint = null);

public sealed record TrustSftpHostKeyRequest(string Fingerprint);

public sealed record CwaSettingsResponse(
    bool IsEnabled,
    string TransportMode,
    string? LocalIngestPath,
    string? SftpHost,
    int? SftpPort,
    string? SftpUsername,
    string? SftpIngestPath,
    string SftpAuthenticationMode,
    bool HasSftpPrivateKey,
    string? SftpPrivateKeyHint,
    DateTimeOffset? SftpPrivateKeySetAtUtc,
    bool HasSftpPassphrase,
    string? SftpPassphraseHint,
    DateTimeOffset? SftpPassphraseSetAtUtc,
    bool HasSftpPassword,
    string? SftpPasswordHint,
    DateTimeOffset? SftpPasswordSetAtUtc,
    string? SftpHostKeyFingerprint,
    DateTimeOffset? SftpHostKeyTrustedAtUtc,
    string? OpdsBaseUrl,
    string? OpdsUsername,
    bool HasOpdsPassword,
    string? OpdsPasswordHint,
    DateTimeOffset? OpdsPasswordSetAtUtc,
    DateTimeOffset? LastTestedAtUtc,
    bool? LastTestSucceeded,
    string? LastTestMessage,
    bool IsIngestConfigured);

public sealed record SetCwaSettingsRequest(
    string TransportMode,
    string? LocalIngestPath,
    string? SftpHost,
    int? SftpPort,
    string? SftpUsername,
    string? SftpIngestPath,
    string SftpAuthenticationMode,
    string? OpdsBaseUrl,
    string? OpdsUsername);

/// <summary>
/// A non-persistent SFTP connection probe. Secret values are used only for the
/// probe and are never written unless the administrator subsequently saves.
/// </summary>
public sealed record TestCwaIngestRequest(
    string TransportMode,
    string? LocalIngestPath,
    string? SftpHost,
    int? SftpPort,
    string? SftpUsername,
    string? SftpIngestPath,
    string SftpAuthenticationMode,
    string? SftpPrivateKey,
    string? SftpPassphrase,
    string? SftpPassword,
    string? TrustedSftpHostKeyFingerprint);

/// <summary>
/// A non-persistent OPDS search probe. The password is used only for the
/// request and is never written unless the administrator subsequently saves.
/// </summary>
public sealed record TestCwaOpdsRequest(
    string? OpdsBaseUrl,
    string? OpdsUsername,
    string? OpdsPassword);

public sealed record AudiobookshelfSettingsResponse(
    bool IsEnabled,
    string? BaseUrl,
    string? LibraryId,
    string? FolderId,
    bool HasApiToken,
    string? ApiTokenHint,
    DateTimeOffset? ApiTokenSetAtUtc,
    DateTimeOffset? LastTestedAtUtc,
    bool? LastTestSucceeded,
    string? LastTestMessage);

public sealed record SetAudiobookshelfSettingsRequest(string? BaseUrl, string? LibraryId, string? FolderId);

/// <summary>
/// A non-persistent Audiobookshelf connection probe. Values reflect the
/// administrator's current form; when <see cref="ApiToken"/> is blank the
/// probe falls back to the stored token instead of requiring it be retyped.
/// </summary>
public sealed record TestAudiobookshelfRequest(
    string? BaseUrl,
    string? LibraryId,
    string? FolderId,
    string? ApiToken);

/// <summary>A lookup request for an Audiobookshelf instance's libraries and folders, so the administrator can pick IDs instead of guessing them.</summary>
public sealed record DiscoverAudiobookshelfLibrariesRequest(string? BaseUrl, string? ApiToken);

public sealed record AudiobookshelfFolderOption(string Id, string Path);

public sealed record AudiobookshelfLibraryOption(
    string Id, string Name, IReadOnlyList<AudiobookshelfFolderOption> Folders);

public sealed record AudiobookshelfLibraryDiscoveryResponse(
    bool Succeeded, string? Error, IReadOnlyList<AudiobookshelfLibraryOption> Libraries);

public sealed record LibraryImportResponse(
    Guid Id,
    Guid RequestId,
    Guid WorkId,
    string WorkTitle,
    string OriginalFilename,
    string Status,
    string? ExternalBookId,
    string? FailureReason,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc);

public sealed record DeliveryResponse(
    Guid Id,
    Guid RequestId,
    Guid WorkId,
    string WorkTitle,
    string OriginalFilename,
    string Status,
    string? ExternalItemId,
    string? FailureReason,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc);

public sealed record PublishingQueueResponse(
    IReadOnlyList<LibraryImportResponse> LibraryImports,
    IReadOnlyList<DeliveryResponse> Deliveries);
