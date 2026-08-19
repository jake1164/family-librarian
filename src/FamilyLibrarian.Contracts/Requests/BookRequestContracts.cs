namespace FamilyLibrarian.Contracts.Requests;

/// <summary>
/// Creates a request for a canonical Work in one or both media types.
/// </summary>
/// <param name="Formats">"Ebook", "Audiobook", or both.</param>
/// <param name="ConfirmDuplicate">
/// Set after the caller has seen a duplicate warning and still wants the request.
/// </param>
public sealed record CreateBookRequestRequest(
    Guid WorkId,
    IReadOnlyList<string> Formats,
    string? Note,
    bool ConfirmDuplicate);

public sealed record ChangeBookRequestStatusRequest(
    string Status,
    string? Reason,
    uint? ExpectedVersion = null);

/// <param name="AvailableTransitions">
/// The status changes this user may make now. Presentation only — the host
/// re-checks every transition it is asked to perform.
/// </param>
public sealed record BookRequestResponse(
    Guid Id,
    Guid WorkId,
    string WorkTitle,
    IReadOnlyList<string> Authors,
    string? CoverUrl,
    string Status,
    string StatusDescription,
    bool IsActive,
    IReadOnlyList<BookRequestFormatResponse> Formats,
    string? Note,
    string? AdminNote,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset StatusChangedAtUtc,
    IReadOnlyList<string> AvailableTransitions,
    uint Version);

public sealed record BookRequestFormatResponse(
    Guid FormatId,
    string MediaType,
    string Status,
    string? ProgressCode = null,
    string? ProgressDescription = null);

public sealed record BookRequestListResponse(
    IReadOnlyList<BookRequestResponse> Active,
    IReadOnlyList<BookRequestResponse> History);

public sealed record AdminBookRequestListResponse(
    IReadOnlyList<AdminBookRequestResponse> Requests);

/// <summary>
/// Small, administrator-only attention summary for the persistent application
/// chrome and request-review surfaces. It deliberately contains no requester
/// identity or provider credentials.
/// </summary>
public sealed record AdminRequestAttentionResponse(
    int NeedsReviewCount,
    IReadOnlyList<AdminProviderIssueResponse> ProviderIssues);

public sealed record AdminProviderIssueResponse(
    string ProviderId,
    string DisplayName,
    string Summary,
    DateTimeOffset OccurredAtUtc);

public sealed record AdminBookRequestResponse(
    BookRequestResponse Request,
    string RequesterDisplayName,
    string RequesterEmail,
    IReadOnlyList<BookRequestStatusHistoryResponse> StatusHistory);

public sealed record BookRequestStatusHistoryResponse(
    string? FromStatus,
    string ToStatus,
    string? Reason,
    DateTimeOffset OccurredAtUtc);

public sealed record SetAdminBookRequestNoteRequest(string? Note, uint ExpectedVersion);

/// <param name="ProviderId">
/// A registered automatic direct-acquisition provider id, or <see langword="null"/>
/// to recheck against every registered automatic provider.
/// </param>
public sealed record RecheckNeedsReviewRequest(string? ProviderId);

public sealed record RecheckNeedsReviewResponse(int RequeuedCount);

/// <summary>
/// The 409 answer to a create that would overlap an outstanding request.
/// </summary>
public sealed record BookRequestDuplicateResponse(
    string Message,
    IReadOnlyList<string> OverlappingFormats,
    BookRequestResponse? ExistingRequest);
