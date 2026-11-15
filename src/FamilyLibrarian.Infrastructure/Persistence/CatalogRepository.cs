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
