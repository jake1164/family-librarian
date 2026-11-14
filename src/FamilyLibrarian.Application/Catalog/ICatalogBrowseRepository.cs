namespace FamilyLibrarian.Application.Catalog;

/// <summary>A saved author's identity header. Names are display text; the id is the identity.</summary>
public sealed record BrowseAuthorHeader(Guid Id, string Name);

/// <summary>A saved series' identity header. Deliberately carries no status: completion is never asserted.</summary>
public sealed record BrowseSeriesHeader(Guid Id, string Name);

public sealed record BrowseCredit(Guid AuthorId, string Name, string? Role, int Ordinal);

public sealed record BrowseMembership(
    Guid SeriesId,
    string SeriesName,
    string? PositionLabel,
    decimal? PositionSort,
    bool IsPrimary);

/// <summary>The provider and time of a saved source observation for a Work. Not field-level provenance.</summary>
public sealed record BrowseSource(string ProviderId, DateTimeOffset ObservedAtUtc);

/// <summary>
/// A saved Work's browse row. <see cref="Formats"/> are the distinct existing Edition formats, not an inferred version
/// identity. <see cref="AssociationsTruncated"/> and <see cref="FactsTruncated"/> disclose that a per-Work read cap
/// omitted credits/memberships/sources or format facts.
/// </summary>
public sealed record BrowseWork(
    Guid WorkId,
    string Title,
    DateOnly? FirstPublicationDate,
    IReadOnlyList<BrowseCredit> Credits,
    IReadOnlyList<BrowseMembership> Memberships,
    IReadOnlyList<BrowseSource> Sources,
    IReadOnlyList<string>? Formats = null,
    bool AssociationsTruncated = false,
    bool FactsTruncated = false)
{
    public IReadOnlyList<string> Formats { get; init; } = Formats ?? [];
}

public sealed record BrowseSeriesMember(
    Guid EntryId,
    string? PositionLabel,
    decimal? PositionSort,
    bool IsPrimary,
    BrowseWork Work);

/// <summary>A saved author or series whose normalized name equals an observed label. A navigation aid, not an identity claim.</summary>
public sealed record BrowseNameChoice(Guid Id, string Name, int KnownBookCount, IReadOnlyList<string> SampleTitles);

/// <summary>
/// Narrow, read-only, no-tracking projections of the saved catalog for browsing. Separate from
/// <see cref="ICatalogRepository"/> on purpose: that interface serves writers and follows and loads whole graphs,
/// while these queries page before materializing and never track entities.
/// </summary>
/// <remarks>
/// Every list excludes retired Works. <c>skip</c>/<c>take</c> are applied in the database; callers ask for one more
/// row than they intend to show to learn whether another page exists.
/// </remarks>
public interface ICatalogBrowseRepository
{
    Task<BrowseAuthorHeader?> FindAuthorAsync(Guid authorId, CancellationToken cancellationToken);

    Task<BrowseSeriesHeader?> FindSeriesAsync(Guid seriesId, CancellationToken cancellationToken);

    /// <summary>Distinct non-retired Works credited to the author, in a stable total order.</summary>
    Task<IReadOnlyList<BrowseWork>> GetAuthorWorksAsync(
        Guid authorId, int skip, int take, CancellationToken cancellationToken);

    /// <summary>Non-retired members of the series ordered by recorded position (unknown last), then title and id.</summary>
    Task<IReadOnlyList<BrowseSeriesMember>> GetSeriesMembersAsync(
        Guid seriesId, int skip, int take, CancellationToken cancellationToken);

    Task<IReadOnlyList<BrowseNameChoice>> FindAuthorsByNormalizedNameAsync(
        string normalizedName, int take, CancellationToken cancellationToken);

    Task<IReadOnlyList<BrowseNameChoice>> FindSeriesByNormalizedNameAsync(
        string normalizedName, int take, CancellationToken cancellationToken);
}
