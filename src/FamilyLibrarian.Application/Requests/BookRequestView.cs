using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Application.Requests;

/// <summary>
/// A request together with the Work facts needed to display it.
/// </summary>
/// <remarks>
/// A read model rather than the entity: My Requests needs a title and authors
/// per row, and loading the full catalog graph for each request to get them
/// would be wasteful.
/// </remarks>
public sealed record BookRequestView(
    Guid Id,
    Guid WorkId,
    string WorkTitle,
    IReadOnlyList<string> Authors,
    string? CoverUrl,
    RequestStatus Status,
    IReadOnlyList<RequestFormatView> Formats,
    string? RequesterNote,
    string? AdminNote,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset StatusChangedAtUtc,
    uint Version,
    int RequesterCount = 1,
    bool RequiresManualFulfillment = false,
    string? VersionKind = null,
    string? VersionDetails = null)
{
    public bool IsActive => RequestStatusTransitions.IsActive(Status);
}

public sealed record RequestFormatView(
    Guid Id,
    RequestMediaType MediaType,
    RequestFormatStatus Status,
    RequestFormatProgressView? Progress = null);

/// <summary>
/// The administrator-only request read model. Requester identity and the status
/// timeline stay out of <see cref="BookRequestView"/> because My Requests is
/// intentionally private to its owner.
/// </summary>
public sealed record AdminBookRequestView(
    BookRequestView Request,
    string RequesterDisplayName,
    string RequesterEmail,
    IReadOnlyList<RequestStatusHistoryView> StatusHistory,
    IReadOnlyList<RequestParticipantView>? Participants = null);

public sealed record RequestParticipantView(string DisplayName, string Email, string? Note, bool Withdrawn);

public sealed record RequestStatusHistoryView(
    RequestStatus? FromStatus,
    RequestStatus ToStatus,
    string? Reason,
    DateTimeOffset OccurredAtUtc);
