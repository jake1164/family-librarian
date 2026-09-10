namespace FamilyLibrarian.Contracts.Requests;

/// <summary>
/// Creates or joins a shared request for a canonical Work in one or both media types.
/// </summary>
/// <param name="Formats">"Ebook", "Audiobook", or both.</param>
/// <param name="ConfirmDuplicate">
/// Legacy confirmation flag. An exception also requires VersionKind and VersionDetails.
/// </param>
/// <param name="ConfirmOwned">
/// Legacy ownership confirmation flag. It cannot bypass required version details.
/// </param>
/// <param name="DeliveryTargetId">
/// The caller's own, enabled <c>DeliveryTarget</c> to send the ebook to once
/// ready (e.g. "send to my Kindle"), or <see langword="null"/> for no delivery.
/// Requires "Ebook" among <see cref="Formats"/>.
/// </param>
public sealed record CreateBookRequestRequest(
    Guid WorkId,
    IReadOnlyList<string> Formats,
    string? Note,
    bool ConfirmDuplicate,
    bool ConfirmOwned,
    string? VersionKind = null,
    string? VersionDetails = null,
    Guid? DeliveryTargetId = null);

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
    uint Version,
    int RequesterCount = 1,
    bool RequiresManualFulfillment = false,
    string? VersionKind = null,
    string? VersionDetails = null,
    Guid? DeliveryTargetId = null,
    KindleDeliveryResponse? KindleDelivery = null);

/// <summary>The viewer's own most recent Kindle delivery attempt for this request.</summary>
/// <param name="ConfirmationStatus">
/// "Unconfirmed", "Confirmed", or "ReportedMissing" -- only meaningful when
/// <paramref name="Status"/> is "Submitted" (KINDLE-7).
/// </param>
public sealed record KindleDeliveryResponse(
    Guid AttemptId,
    string Status,
    string? FailureReason,
    int AttemptNumber,
    string ConfirmationStatus = "Unconfirmed");

public sealed record BookRequestFormatResponse(
    Guid FormatId,
    string MediaType,
    string Status,
    string? ProgressCode = null,
    string? ProgressDescription = null,
    string? ExternalActionUri = null);

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
    DateTimeOffset OccurredAtUtc,
    string IssueKind);

public sealed record AdminBookRequestResponse(
    BookRequestResponse Request,
    string RequesterDisplayName,
    string RequesterEmail,
    IReadOnlyList<BookRequestStatusHistoryResponse> StatusHistory,
    IReadOnlyList<RequestParticipantResponse>? Participants = null);

public sealed record RequestParticipantResponse(string DisplayName, string Email, string? Note, bool Withdrawn);

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

public sealed record BookRequestOwnedFormatResponse(
    string MediaType,
    string ProviderId,
    string? ExternalActionUri);

/// <summary>
/// The 409 answer to a create for a format already present in a linked
/// library (e.g. CWA/Audiobookshelf).
/// </summary>
public sealed record BookRequestOwnedResponse(
    string Message,
    IReadOnlyList<BookRequestOwnedFormatResponse> OwnedFormats);

/// <summary>
/// Unified 409 envelope for request creation: exactly one of
/// <see cref="Duplicate"/> or <see cref="Owned"/> is populated, selected by
/// <see cref="Kind"/> ("Duplicate" or "Owned").
/// </summary>
public sealed record CreateBookRequestConflictResponse(
    string Kind,
    BookRequestDuplicateResponse? Duplicate,
    BookRequestOwnedResponse? Owned);
