using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Domain.Catalog;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.Persistence;

public sealed class CatalogRepository(AppDbContext database) : ICatalogRepository
{
    public Task<Work?> FindWorkByExternalReferenceAsync(
        string providerId,
        string externalId,
        CancellationToken cancellationToken) =>
        QueryWorks()
            .SingleOrDefaultAsync(
                work => database.ExternalReferences.Any(reference =>
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
                work => work.Editions.Any(edition =>
                    edition.Isbn13 != null && isbn13s.Contains(edition.Isbn13)),
                cancellationToken);

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
