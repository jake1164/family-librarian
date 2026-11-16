using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Domain.Catalog;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.Persistence;

public sealed class CatalogRepository(AppDbContext database) : ICatalogRepository, ICatalogMutationRepository
{
    public Task<Work?> FindWorkByExternalReferenceAsync(
        string providerId,
        string externalId,
        CancellationToken cancellationToken) =>
        QueryWorks()
            .SingleOrDefaultAsync(
                work => !work.IsRetired && database.ExternalReferences.Any(reference =>
                    reference.EntityType == ExternalReferenceEntityType.Work &&
                    reference.EntityId == work.Id &&
                    reference.ProviderId == providerId &&
                    reference.ExternalId == externalId),
                cancellationToken);

    public Task<Work?> FindWorkByIsbn13Async(
        IReadOnlyCollection<string> isbn13s,
        CancellationToken cancellationToken) =>
        QueryWorks()
            .SingleOrDefaultAsync(
                work => !work.IsRetired && work.Editions.Any(edition =>
                    edition.Isbn13 != null && isbn13s.Contains(edition.Isbn13)),
                cancellationToken);

    /// <summary>The most Works, Authors or Series one list lookup returns. More than this many same-name rows is already ambiguous.</summary>
    private const int MaximumListedMatches = 50;

    public async Task<IReadOnlyList<Work>> ListWorksByIsbn13Async(
        IReadOnlyCollection<string> isbn13s,
        CancellationToken cancellationToken) =>
        await QueryWorks()
            .Where(work => !work.IsRetired && work.Editions.Any(edition =>
                edition.Isbn13 != null && isbn13s.Contains(edition.Isbn13)))
            .OrderBy(work => work.CreatedAtUtc)
            .ThenBy(work => work.Id)
            .Take(MaximumListedMatches)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Author>> ListAuthorsByNormalizedNameAsync(
        string normalizedName,
        CancellationToken cancellationToken) =>
        await database.Authors
            .Where(author => author.NormalizedName == normalizedName)
            .OrderBy(author => author.CreatedAtUtc)
            .ThenBy(author => author.Id)
            .Take(MaximumListedMatches)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Series>> ListSeriesByNormalizedNameAsync(
        string normalizedName,
        CancellationToken cancellationToken) =>
        await database.Series
            .Where(series => series.NormalizedName == normalizedName)
            .OrderBy(series => series.CreatedAtUtc)
            .ThenBy(series => series.Id)
            .Take(MaximumListedMatches)
            .ToListAsync(cancellationToken);

    public Task<Work?> GetWorkAsync(Guid workId, CancellationToken cancellationToken) =>
        QueryWorks().SingleOrDefaultAsync(work => work.Id == workId, cancellationToken);

    public async Task<IReadOnlyList<ExternalReference>> GetWorkSourcesAsync(
        Guid workId,
        CancellationToken cancellationToken) =>
        await database.ExternalReferences
            .Where(reference => reference.EntityType == ExternalReferenceEntityType.Work &&
                reference.EntityId == workId)
            .OrderBy(reference => reference.ProviderId)
            .ThenBy(reference => reference.ExternalId)
            .ToArrayAsync(cancellationToken);

    public Task<Author?> FindAuthorByNormalizedNameAsync(
        string normalizedName,
        CancellationToken cancellationToken) =>
        database.Authors.SingleOrDefaultAsync(
            author => author.NormalizedName == normalizedName,
            cancellationToken);

    public Task<Series?> FindSeriesByNormalizedNameAsync(
        string normalizedName,
        CancellationToken cancellationToken) =>
        database.Series.SingleOrDefaultAsync(
            series => series.NormalizedName == normalizedName,
            cancellationToken);

    public Task<Series?> GetSeriesAsync(Guid seriesId, CancellationToken cancellationToken) =>
        database.Series
            .Include(series => series.Entries)
                .ThenInclude(entry => entry.Work)
            .SingleOrDefaultAsync(series => series.Id == seriesId, cancellationToken);

    public Task<Author?> GetAuthorAsync(Guid authorId, CancellationToken cancellationToken) =>
        database.Authors
            .Include(author => author.WorkAuthors)
                .ThenInclude(workAuthor => workAuthor.Work)
            .SingleOrDefaultAsync(author => author.Id == authorId, cancellationToken);

    // Mutation-side evidence access. Every read here is tracked by this attempt's own context and is bounded.

    public Task<IReadOnlyList<ExternalReference>> ListWorkReferencesAsync(Guid workId, CancellationToken cancellationToken) =>
        GetWorkSourcesAsync(workId, cancellationToken);

    public async Task<IReadOnlyDictionary<string, Guid>> MapIsbnOwnersAsync(
        IReadOnlyCollection<string> isbn13s, CancellationToken cancellationToken)
    {
        if (isbn13s.Count == 0)
        {
            return new Dictionary<string, Guid>(StringComparer.Ordinal);
        }

        var owners = await database.Editions
            .Where(edition => edition.Isbn13 != null && isbn13s.Contains(edition.Isbn13) &&
                database.Works.Any(work => work.Id == edition.WorkId && !work.IsRetired))
            .Select(edition => new { Isbn = edition.Isbn13!, edition.WorkId })
            .Take(MaximumListedMatches * 4)
            .ToListAsync(cancellationToken);
        return owners.GroupBy(owner => owner.Isbn, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().WorkId, StringComparer.Ordinal);
    }

    /// <summary>
    /// The complete claims of the requested facts. This is a writer's authoritative read, so it is deliberately not capped: a prefix
    /// of the rows would hide the current or selected claim of a source and make the writer supersede, select or compact wrongly.
    /// What bounds it is the facts asked for and per-source retention, not a row limit.
    /// </summary>
    public async Task<IReadOnlyList<MetadataClaim>> ListClaimsAsync(
        Guid workId, IReadOnlyCollection<ClaimGroup> groups, CancellationToken cancellationToken)
    {
        if (groups.Count == 0)
        {
            return [];
        }

        // A field and a scope are matched independently in the database (a composite membership has no portable translation); the
        // exact pairs are then selected here, from rows that are all complete for the pairs asked for.
        var fields = groups.Select(group => group.Field).Distinct().ToArray();
        var scopes = groups.Select(group => group.ScopeKey).Distinct(StringComparer.Ordinal).ToArray();
        var rows = await database.MetadataClaims
            .Where(claim => claim.WorkId == workId && fields.Contains(claim.Field) && scopes.Contains(claim.ScopeKey))
            .OrderBy(claim => claim.Field)
            .ThenBy(claim => claim.ScopeKey)
            .ThenBy(claim => claim.ProviderId)
            .ThenBy(claim => claim.SourceEntityId)
            .ThenBy(claim => claim.FirstSeenAtUtc)
            .ThenBy(claim => claim.Id)
            .ToListAsync(cancellationToken);
        var wanted = groups.ToHashSet();
        return rows.Where(claim => wanted.Contains(new ClaimGroup(claim.Field, claim.ScopeKey))).ToList();
    }

    public async Task<IReadOnlyList<SourceObservation>> ListObservationsAsync(
        Guid workId, string providerId, string externalId, CancellationToken cancellationToken) =>
        await database.SourceObservations
            .Where(observation => observation.WorkId == workId && observation.ProviderId == providerId &&
                observation.ExternalId == externalId)
            .OrderBy(observation => observation.ObservedAtUtc)
            .ThenBy(observation => observation.Id)
            .ToListAsync(cancellationToken);

    public async Task<string> GetSourceRevisionAsync(
        Guid workId, string providerId, string externalId, CancellationToken cancellationToken)
    {
        // Row identity and xmin (the version PostgreSQL stamps with the writing transaction's id) are database-assigned, so the revision
        // moves whenever any transaction inserts, changes or removes one of these rows, whatever any clock says. No tracking: this is a
        // read of the committed state under the catalog lock, never part of the attempt's own unit of work.
        var snapshots = await database.SourceObservations.AsNoTracking()
            .Where(observation => observation.WorkId == workId && observation.ProviderId == providerId && observation.ExternalId == externalId)
            .Select(observation => new { observation.Id, observation.Version })
            .ToListAsync(cancellationToken);
        var claims = await database.MetadataClaims.AsNoTracking()
            .Where(claim => claim.WorkId == workId && claim.ProviderId == providerId && claim.SourceEntityId == externalId)
            .Select(claim => new { claim.Id, claim.Version })
            .ToListAsync(cancellationToken);

        return string.Join(
            '\n',
            snapshots.Select(row => $"S:{row.Id:N}:{row.Version}")
                .Concat(claims.Select(row => $"C:{row.Id:N}:{row.Version}"))
                .Order(StringComparer.Ordinal));
    }

    public async Task<IReadOnlyList<Guid>> ListEntityIdsByExternalReferenceAsync(
        ExternalReferenceEntityType entityType, string providerId, string externalId, CancellationToken cancellationToken) =>
        await database.ExternalReferences
            .Where(reference => reference.EntityType == entityType && reference.ProviderId == providerId &&
                reference.ExternalId == externalId)
            .OrderBy(reference => reference.CreatedAtUtc)
            .ThenBy(reference => reference.Id)
            .Select(reference => reference.EntityId)
            .Take(MaximumListedMatches)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlySet<Guid>> ListEntityIdsReferencedByOtherIdAsync(
        ExternalReferenceEntityType entityType,
        string providerId,
        string externalId,
        IReadOnlyCollection<Guid> entityIds,
        CancellationToken cancellationToken)
    {
        if (entityIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var tied = await database.ExternalReferences
            .Where(reference => reference.EntityType == entityType && reference.ProviderId == providerId &&
                reference.ExternalId != externalId && entityIds.Contains(reference.EntityId))
            .Select(reference => reference.EntityId)
            .Distinct()
            .ToListAsync(cancellationToken);
        return tied.ToHashSet();
    }

    public async Task<IReadOnlyList<Author>> GetAuthorsAsync(IReadOnlyCollection<Guid> authorIds, CancellationToken cancellationToken) =>
        authorIds.Count == 0
            ? []
            : await database.Authors.Where(author => authorIds.Contains(author.Id)).OrderBy(author => author.CreatedAtUtc)
                .ThenBy(author => author.Id).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Series>> GetSeriesAsync(IReadOnlyCollection<Guid> seriesIds, CancellationToken cancellationToken) =>
        seriesIds.Count == 0
            ? []
            : await database.Series.Where(series => seriesIds.Contains(series.Id)).OrderBy(series => series.CreatedAtUtc)
                .ThenBy(series => series.Id).ToListAsync(cancellationToken);

    public void AddEdition(Edition edition) => database.Editions.Add(edition);

    public void AddSeriesEntry(SeriesEntry seriesEntry) => database.SeriesEntries.Add(seriesEntry);

    public void AddClaim(MetadataClaim claim) => database.MetadataClaims.Add(claim);

    public void RemoveClaim(MetadataClaim claim) => database.MetadataClaims.Remove(claim);

    public void AddObservation(SourceObservation observation) => database.SourceObservations.Add(observation);

    public void RemoveObservation(SourceObservation observation) => database.SourceObservations.Remove(observation);

    public void AddWork(Work work) => database.Works.Add(work);

    public void AddAuthor(Author author) => database.Authors.Add(author);

    public void AddSeries(Series series) => database.Series.Add(series);

    public void AddExternalReference(ExternalReference externalReference) =>
        database.ExternalReferences.Add(externalReference);

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        database.SaveChangesAsync(cancellationToken);

    private IQueryable<Work> QueryWorks() => database.Works
        .Include(work => work.Authors)
            .ThenInclude(workAuthor => workAuthor.Author)
        .Include(work => work.Editions)
        .Include(work => work.SeriesEntries)
            .ThenInclude(seriesEntry => seriesEntry.Series);
}
