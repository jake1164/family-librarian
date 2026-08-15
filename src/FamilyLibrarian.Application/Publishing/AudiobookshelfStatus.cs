namespace FamilyLibrarian.Application.Publishing;

/// <summary>The full administrator-visible state of the Audiobookshelf destination. No secret value is ever included.</summary>
public sealed record AudiobookshelfStatus(
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
