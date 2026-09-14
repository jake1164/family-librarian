using FamilyLibrarian.Domain.Catalog;

namespace FamilyLibrarian.Application.Following;

/// <summary>A followed series together with its known entries and, for each,
/// whether the caller has read it and whether it's in their library.</summary>
/// <param name="IsCaughtUp">Every <see cref="FollowedSeriesEntryView.IsPrimary"/>
/// entry has been read. Vacuously true when the series has no primary entries
/// known locally yet.</param>
public sealed record FollowedSeriesView(
    Guid FollowId,
    Guid SeriesId,
    string SeriesName,
    SeriesStatus Status,
    IReadOnlyList<FollowedSeriesEntryView> Entries,
    bool IsCaughtUp);

public sealed record FollowedSeriesEntryView(
    Guid WorkId,
    string WorkTitle,
    string? PositionLabel,
    bool IsPrimary,
    bool IsRead,
    bool IsOwned);

public sealed record FollowedAuthorView(
    Guid FollowId,
    Guid AuthorId,
    string AuthorName,
    IReadOnlyList<FollowedAuthorWorkView> Works);

public sealed record FollowedAuthorWorkView(
    Guid WorkId,
    string WorkTitle,
    bool IsRead,
    bool IsOwned);
