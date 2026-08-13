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
    DateTimeOffset StatusChangedAtUtc)
{
    public bool IsActive => RequestStatusTransitions.IsActive(Status);
}

public sealed record RequestFormatView(RequestMediaType MediaType, RequestFormatStatus Status);
