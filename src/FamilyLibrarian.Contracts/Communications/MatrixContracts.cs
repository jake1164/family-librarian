namespace FamilyLibrarian.Contracts.Communications;

public sealed record MatrixSettingsResponse(
    bool IsEnabled,
    string? HomeserverUrl,
    string? BotUserId,
    bool HasAccessToken,
    DateTimeOffset? AccessTokenSetAtUtc,
    DateTimeOffset? LastTestedAtUtc,
    bool? LastTestSucceeded,
    string? LastTestMessage);

/// <summary>
/// Saves the Matrix settings. A non-blank <paramref name="AccessToken"/> replaces the stored
/// token in the same save; null or blank leaves the stored token unchanged.
/// </summary>
public sealed record SetMatrixSettingsRequest(string? HomeserverUrl, string? BotUserId, string? AccessToken = null);

public sealed record SetMatrixEnabledRequest(bool Enabled);

public sealed record SetMatrixAccessTokenRequest(string AccessToken);

/// <summary>
/// A non-persistent Matrix connection probe. Draft field values (including a
/// freshly typed but unsaved access token) are used only for the probe and
/// are never written unless the administrator subsequently saves them. Any
/// omitted field falls back to the currently saved setting.
/// </summary>
public sealed record SendMatrixTestRequest(string? HomeserverUrl, string? BotUserId, string? AccessToken);

public sealed record MatrixTestResponse(bool Succeeded, string? Message);
