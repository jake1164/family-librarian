using FamilyLibrarian.Application.Catalog.Answers;
using FamilyLibrarian.Domain.Catalog;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.Persistence;

/// <summary>
/// No-tracking, paged evidence projections for answers. A page of Works is selected (and bounded) in the database first; each
/// Work's credits for exactly that page are then read in one bounded query, capped per Work by PostgreSQL. It reads the stored
/// publication date, its precision and basis, the version class and the series position facts exactly as saved, and never
/// reconstructs a precision or basis the browse rows do not carry.
/// </summary>
/// <remarks>
/// A <see cref="AppDbContext"/> is not thread-safe, so the two queries run one after the other on the scoped context (EF Core
/// "Avoiding DbContext threading issues"). Retired Works are excluded. The projections write nothing.
/// </remarks>
public sealed class SavedCatalogEvidenceRepository(AppDbContext database) : ISavedCatalogEvidenceRepository
{
    private const int MaximumCreditsPerWork = 20;

    // row_number() over each Work is the bound: the window and the cap are evaluated by PostgreSQL, so a Work with thousands of
    // credits still returns at most cap + 1 rows here.
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

    public async Task<IReadOnlyList<SavedSeriesEntryEvidence>> GetSeriesEvidenceAsync(
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
                entry.Id, entry.PositionLabel, entry.PositionSort, entry.IsPrimary, entry.PositionState,
                new WorkRow(
                    entry.WorkId, entry.Work.CanonicalTitle, entry.Work.FirstPublicationDate, entry.Work.FirstPublicationPrecision,
                    entry.Work.FirstPublicationBasis, entry.Work.PublicationStatus, entry.Work.VersionClass)))
            .ToArrayAsync(cancellationToken);

        var works = await AttachAsync(page.Select(row => row.Work).ToArray(), cancellationToken);
        var byId = works.ToDictionary(work => work.WorkId);
        return page
            .Select(row => new SavedSeriesEntryEvidence(
                row.EntryId, row.PositionSort, row.PositionLabel, row.IsPrimary, row.PositionState, byId[row.Work.WorkId]))
            .ToArray();
    }

    public async Task<IReadOnlyList<SavedWorkEvidence>> GetAuthorEvidenceAsync(
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
                link.WorkId, link.Work.CanonicalTitle, link.Work.FirstPublicationDate, link.Work.FirstPublicationPrecision,
                link.Work.FirstPublicationBasis, link.Work.PublicationStatus, link.Work.VersionClass))
            .ToArrayAsync(cancellationToken);
        return await AttachAsync(page, cancellationToken);
    }

    private async Task<IReadOnlyList<SavedWorkEvidence>> AttachAsync(IReadOnlyList<WorkRow> page, CancellationToken cancellationToken)
    {
        if (page.Count == 0) return [];
        var ids = page.Select(row => row.WorkId).Distinct().ToArray();
        var creditRows = await database.Database
            .SqlQueryRaw<CreditRow>(CreditsSql, ids, MaximumCreditsPerWork + 1)
            .ToArrayAsync(cancellationToken);
        var credits = creditRows
            .Where(row => row.Rank <= MaximumCreditsPerWork)
            .ToLookup(row => row.WorkId, row => new SavedCreditEvidence(row.AuthorId, row.Name, row.Role, row.Ordinal));
        var truncated = creditRows.Where(row => row.Rank > MaximumCreditsPerWork).Select(row => row.WorkId).ToHashSet();
        return page.Select(row => new SavedWorkEvidence(
                row.WorkId, row.Title, row.Date, row.Precision, row.Basis, row.Status, row.VersionClass,
                credits[row.WorkId].ToArray(), truncated.Contains(row.WorkId)))
            .ToArray();
    }

    private sealed record WorkRow(
        Guid WorkId, string Title, DateOnly? Date, DatePrecision? Precision, DateBasis? Basis, PublicationStatus Status, string? VersionClass);

    private sealed record MemberRow(
        Guid EntryId, string? PositionLabel, decimal? PositionSort, bool IsPrimary, SeriesPositionState? PositionState, WorkRow Work);

    // Unmapped projection type for the bounded SQL above; every column has a matching property (EF Core SqlQuery).
    private sealed record CreditRow(Guid WorkId, int Ordinal, Guid AuthorId, string Name, string? Role, long Rank);
}
