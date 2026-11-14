using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Domain.Catalog;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.Persistence;

/// <summary>
/// No-tracking, paged projections for author and series browsing. A page of Works is selected (and bounded) in the
/// database first; credits, memberships, sources and distinct Edition formats for exactly that page are then fetched in
/// one bounded query each, so the query count is constant regardless of page content, no full graph is loaded and no
/// association can return more rows per Work than its documented cap.
/// </summary>
/// <remarks>
/// A <see cref="AppDbContext"/> is not thread-safe, so the batched queries run one after another on the scoped
/// context rather than concurrently (EF Core "Avoiding DbContext threading issues"). Retired Works are excluded from
/// every list; direct request and history routes to them are unaffected because they do not use this repository.
/// </remarks>
public sealed class CatalogBrowseRepository(AppDbContext database) : ICatalogBrowseRepository
{
    private const int SampleTitleCount = 3;

    // Explicit per-Work read caps. Each association query fetches at most cap + 1 rows per Work so a Work above the cap
    // is detected and disclosed, while the database result itself stays bounded no matter how many rows the Work has.
    private const int MaximumCreditsPerWork = 20;
    private const int MaximumMembershipsPerWork = 20;
    private const int MaximumSourcesPerWork = 10;
    private const int MaximumFormatsPerWork = 10;

    // row_number() over each Work is the bound: the window and the cap are evaluated by PostgreSQL, so an association
    // with thousands of rows still returns at most cap + 1 rows here.
    private const string CreditsSql = """
        SELECT "WorkId", "Ordinal", "AuthorId", "Name", "Role", "Rank"
        FROM (
            SELECT wa.work_id AS "WorkId", wa.ordinal AS "Ordinal", wa.author_id AS "AuthorId",
                   au.canonical_name AS "Name", wa.role AS "Role",
                   row_number() OVER (PARTITION BY wa.work_id ORDER BY wa.ordinal) AS "Rank"
            FROM catalog.work_authors AS wa
            INNER JOIN catalog.authors AS au ON au.id = wa.author_id
            WHERE wa.work_id = ANY({0})
        ) AS ranked
        WHERE "Rank" <= {1}
        ORDER BY "WorkId", "Ordinal"
        """;

    private const string MembershipsSql = """
        SELECT "WorkId", "SeriesId", "SeriesName", "PositionLabel", "PositionSort", "IsPrimary", "Rank"
        FROM (
            SELECT se.work_id AS "WorkId", se.series_id AS "SeriesId", s.name AS "SeriesName",
                   se.position_label AS "PositionLabel", se.position_sort AS "PositionSort",
                   se.is_primary AS "IsPrimary",
                   row_number() OVER (
                       PARTITION BY se.work_id
                       ORDER BY se.is_primary DESC, s.name, se.series_id) AS "Rank"
            FROM catalog.series_entries AS se
            INNER JOIN catalog.series AS s ON s.id = se.series_id
            WHERE se.work_id = ANY({0})
        ) AS ranked
        WHERE "Rank" <= {1}
        ORDER BY "WorkId", "Rank"
        """;

    private const string SourcesSql = """
        SELECT "WorkId", "ProviderId", "ObservedAtUtc", "Rank"
        FROM (
            SELECT er.entity_id AS "WorkId", er.provider_id AS "ProviderId",
                   er.observed_at_utc AS "ObservedAtUtc",
                   row_number() OVER (
                       PARTITION BY er.entity_id
                       ORDER BY er.provider_id, er.external_id, er.id) AS "Rank"
            FROM catalog.external_references AS er
            WHERE er.entity_type = 'Work' AND er.entity_id = ANY({0})
        ) AS ranked
        WHERE "Rank" <= {1}
        ORDER BY "WorkId", "Rank"
        """;

    private const string FormatsSql = """
        SELECT "WorkId", "Format", "Rank"
        FROM (
            SELECT formats.work_id AS "WorkId", formats.format AS "Format",
                   row_number() OVER (PARTITION BY formats.work_id ORDER BY formats.format) AS "Rank"
            FROM (
                SELECT DISTINCT e.work_id, e.format
                FROM catalog.editions AS e
                WHERE e.work_id = ANY({0})
            ) AS formats
        ) AS ranked
        WHERE "Rank" <= {1}
        ORDER BY "WorkId", "Rank"
        """;

    public async Task<BrowseAuthorHeader?> FindAuthorAsync(Guid authorId, CancellationToken cancellationToken) =>
        await database.Authors.AsNoTracking()
            .Where(author => author.Id == authorId)
            .Select(author => new BrowseAuthorHeader(author.Id, author.CanonicalName))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<BrowseSeriesHeader?> FindSeriesAsync(Guid seriesId, CancellationToken cancellationToken) =>
        await database.Series.AsNoTracking()
            .Where(series => series.Id == seriesId)
            .Select(series => new BrowseSeriesHeader(series.Id, series.Name))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<BrowseWork>> GetAuthorWorksAsync(
        Guid authorId, int skip, int take, CancellationToken cancellationToken)
    {
        // (WorkId, AuthorId) is the link table's key, so each Work appears at most once per author.
        var page = await database.Set<WorkAuthor>().AsNoTracking()
            .Where(link => link.AuthorId == authorId && !link.Work.IsRetired)
            .OrderBy(link => link.Work.NormalizedTitle)
            .ThenBy(link => link.Work.CanonicalTitle)
            .ThenBy(link => link.WorkId)
            .Skip(skip)
            .Take(take)
            .Select(link => new WorkRow(
                link.WorkId, link.Work.CanonicalTitle, link.Work.FirstPublicationDate))
            .ToArrayAsync(cancellationToken);
        return await AttachAsync(page, cancellationToken);
    }

    public async Task<IReadOnlyList<BrowseSeriesMember>> GetSeriesMembersAsync(
        Guid seriesId, int skip, int take, CancellationToken cancellationToken)
    {
        var page = await database.SeriesEntries.AsNoTracking()
            .Where(entry => entry.SeriesId == seriesId && !entry.Work.IsRetired)
            .OrderBy(entry => entry.PositionSort == null)
            .ThenBy(entry => entry.PositionSort)
            .ThenBy(entry => entry.PositionLabel)
            .ThenBy(entry => entry.Work.NormalizedTitle)
            .ThenBy(entry => entry.Work.CanonicalTitle)
            .ThenBy(entry => entry.Id)
            .Skip(skip)
            .Take(take)
            .Select(entry => new MemberRow(
                entry.Id, entry.PositionLabel, entry.PositionSort, entry.IsPrimary,
                new WorkRow(entry.WorkId, entry.Work.CanonicalTitle, entry.Work.FirstPublicationDate)))
            .ToArrayAsync(cancellationToken);

        var works = await AttachAsync(page.Select(row => row.Work).ToArray(), cancellationToken);
        var byId = works.ToDictionary(work => work.WorkId);
        return page
            .Select(row => new BrowseSeriesMember(row.EntryId, row.PositionLabel, row.PositionSort, row.IsPrimary, byId[row.Work.WorkId]))
            .ToArray();
    }

    public async Task<IReadOnlyList<BrowseNameChoice>> FindAuthorsByNormalizedNameAsync(
        string normalizedName, int take, CancellationToken cancellationToken)
    {
        // One round trip: count and a few sample titles are correlated subqueries on the bounded author page.
        var rows = await database.Authors.AsNoTracking()
            .Where(author => author.NormalizedName == normalizedName)
            .OrderBy(author => author.CanonicalName)
            .ThenBy(author => author.Id)
            .Take(take)
            .Select(author => new
            {
                author.Id,
                author.CanonicalName,
                Count = author.WorkAuthors.Count(link => !link.Work.IsRetired),
                Titles = author.WorkAuthors
                    .Where(link => !link.Work.IsRetired)
                    .OrderBy(link => link.Work.NormalizedTitle)
                    .ThenBy(link => link.WorkId)
                    .Take(SampleTitleCount)
                    .Select(link => link.Work.CanonicalTitle)
                    .ToList()
            })
            .ToArrayAsync(cancellationToken);
        return rows.Select(row => new BrowseNameChoice(row.Id, row.CanonicalName, row.Count, row.Titles)).ToArray();
    }

    public async Task<IReadOnlyList<BrowseNameChoice>> FindSeriesByNormalizedNameAsync(
        string normalizedName, int take, CancellationToken cancellationToken)
    {
        var rows = await database.Series.AsNoTracking()
            .Where(series => series.NormalizedName == normalizedName)
            .OrderBy(series => series.Name)
            .ThenBy(series => series.Id)
            .Take(take)
            .Select(series => new
            {
                series.Id,
                series.Name,
                Count = series.Entries.Count(entry => !entry.Work.IsRetired),
                Titles = series.Entries
                    .Where(entry => !entry.Work.IsRetired)
                    .OrderBy(entry => entry.Work.NormalizedTitle)
                    .ThenBy(entry => entry.WorkId)
                    .Take(SampleTitleCount)
                    .Select(entry => entry.Work.CanonicalTitle)
                    .ToList()
            })
            .ToArrayAsync(cancellationToken);
        return rows.Select(row => new BrowseNameChoice(row.Id, row.Name, row.Count, row.Titles)).ToArray();
    }

    /// <summary>
    /// Fetches bounded credits, memberships, sources and formats for one page of Works, one query each, keeping the
    /// page's order. Every association read is capped per Work in PostgreSQL, so the materialized row count is bounded
    /// by the caps rather than by the catalog, and a Work above a cap is reported as truncated.
    /// </summary>
    private async Task<IReadOnlyList<BrowseWork>> AttachAsync(
        IReadOnlyList<WorkRow> page, CancellationToken cancellationToken)
    {
        if (page.Count == 0) return [];
        var ids = page.Select(row => row.WorkId).ToArray();

        var creditRows = await database.Database
            .SqlQueryRaw<CreditRow>(CreditsSql, ids, MaximumCreditsPerWork + 1)
            .ToArrayAsync(cancellationToken);
        var credits = creditRows.Where(row => row.Rank <= MaximumCreditsPerWork)
            .ToLookup(row => row.WorkId, row => new BrowseCredit(row.AuthorId, row.Name, row.Role, row.Ordinal));
        var creditsTruncated = creditRows.Where(row => row.Rank > MaximumCreditsPerWork).Select(row => row.WorkId).ToHashSet();

        var membershipRows = await database.Database
            .SqlQueryRaw<MembershipRow>(MembershipsSql, ids, MaximumMembershipsPerWork + 1)
            .ToArrayAsync(cancellationToken);
        var memberships = membershipRows.Where(row => row.Rank <= MaximumMembershipsPerWork)
            .ToLookup(row => row.WorkId, row => new BrowseMembership(
                row.SeriesId, row.SeriesName, row.PositionLabel, row.PositionSort, row.IsPrimary));
        var membershipsTruncated = membershipRows.Where(row => row.Rank > MaximumMembershipsPerWork).Select(row => row.WorkId).ToHashSet();

        var sourceRows = await database.Database
            .SqlQueryRaw<SourceRow>(SourcesSql, ids, MaximumSourcesPerWork + 1)
            .ToArrayAsync(cancellationToken);
        var sources = sourceRows.Where(row => row.Rank <= MaximumSourcesPerWork)
            .ToLookup(row => row.WorkId, row => new BrowseSource(row.ProviderId, row.ObservedAtUtc));
        var sourcesTruncated = sourceRows.Where(row => row.Rank > MaximumSourcesPerWork).Select(row => row.WorkId).ToHashSet();

        var formatRows = await database.Database
            .SqlQueryRaw<FormatRow>(FormatsSql, ids, MaximumFormatsPerWork + 1)
            .ToArrayAsync(cancellationToken);
        var formats = formatRows.Where(row => row.Rank <= MaximumFormatsPerWork)
            .ToLookup(row => row.WorkId, row => row.Format);
        var formatsTruncated = formatRows.Where(row => row.Rank > MaximumFormatsPerWork).Select(row => row.WorkId).ToHashSet();

        return page.Select(row => new BrowseWork(
                row.WorkId,
                row.Title,
                row.FirstPublicationDate,
                credits[row.WorkId].ToArray(),
                memberships[row.WorkId].ToArray(),
                sources[row.WorkId].ToArray(),
                formats[row.WorkId].ToArray(),
                creditsTruncated.Contains(row.WorkId) || membershipsTruncated.Contains(row.WorkId) || sourcesTruncated.Contains(row.WorkId),
                formatsTruncated.Contains(row.WorkId)))
            .ToArray();
    }

    private sealed record WorkRow(Guid WorkId, string Title, DateOnly? FirstPublicationDate);

    private sealed record MemberRow(Guid EntryId, string? PositionLabel, decimal? PositionSort, bool IsPrimary, WorkRow Work);

    // Unmapped projection types for the bounded SQL above; every column has a matching property (EF Core SqlQuery).
    private sealed record CreditRow(Guid WorkId, int Ordinal, Guid AuthorId, string Name, string? Role, long Rank);

    private sealed record MembershipRow(
        Guid WorkId, Guid SeriesId, string SeriesName, string? PositionLabel, decimal? PositionSort, bool IsPrimary, long Rank);

    private sealed record SourceRow(Guid WorkId, string ProviderId, DateTimeOffset ObservedAtUtc, long Rank);

    private sealed record FormatRow(Guid WorkId, string Format, long Rank);
}
