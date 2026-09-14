namespace FamilyLibrarian.Contracts.Following;

public sealed record FollowedSeriesEntryResponse(
    Guid WorkId,
    string WorkTitle,
    string? PositionLabel,
    bool IsPrimary,
    bool IsRead,
    bool IsOwned);

public sealed record FollowedSeriesResponse(
    Guid FollowId,
    Guid SeriesId,
    string SeriesName,
    string Status,
    IReadOnlyList<FollowedSeriesEntryResponse> Entries,
    bool IsCaughtUp);

public sealed record FollowedAuthorWorkResponse(
    Guid WorkId,
    string WorkTitle,
    bool IsRead,
    bool IsOwned);

public sealed record FollowedAuthorResponse(
    Guid FollowId,
    Guid AuthorId,
    string AuthorName,
    IReadOnlyList<FollowedAuthorWorkResponse> Works);

public sealed record FollowingSummaryResponse(
    IReadOnlyList<FollowedSeriesResponse> Series,
    IReadOnlyList<FollowedAuthorResponse> Authors);
