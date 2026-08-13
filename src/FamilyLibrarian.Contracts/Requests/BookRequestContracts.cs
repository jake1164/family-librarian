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

public sealed record ChangeBookRequestStatusRequest(string Status, string? Reason);

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
    IReadOnlyList<string> AvailableTransitions);

public sealed record BookRequestFormatResponse(string MediaType, string Status);

public sealed record BookRequestListResponse(
    IReadOnlyList<BookRequestResponse> Active,
    IReadOnlyList<BookRequestResponse> History);

/// <summary>
/// The 409 answer to a create that would overlap an outstanding request.
/// </summary>
public sealed record BookRequestDuplicateResponse(
    string Message,
    IReadOnlyList<string> OverlappingFormats,
    BookRequestResponse? ExistingRequest);
