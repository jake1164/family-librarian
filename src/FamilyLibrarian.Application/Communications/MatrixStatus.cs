namespace FamilyLibrarian.Application.Communications;

/// <summary>Administrator-visible Matrix state. It intentionally contains no access-token value.</summary>
public sealed record MatrixStatus(
    bool IsEnabled,
    string? HomeserverUrl,
    string? BotUserId,
    bool HasAccessToken,
    DateTimeOffset? AccessTokenSetAtUtc,
    DateTimeOffset? LastTestedAtUtc,
    bool? LastTestSucceeded,
    string? LastTestMessage);
