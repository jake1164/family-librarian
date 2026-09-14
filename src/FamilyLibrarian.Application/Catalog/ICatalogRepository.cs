using FamilyLibrarian.Domain.Catalog;

namespace FamilyLibrarian.Application.Catalog;

public interface ICatalogRepository
{
    Task<Work?> FindWorkByExternalReferenceAsync(
        string providerId,
        string externalId,
        CancellationToken cancellationToken);

    Task<Work?> FindWorkByIsbn13Async(
        IReadOnlyCollection<string> isbn13s,
        CancellationToken cancellationToken);

    Task<Work?> GetWorkAsync(Guid workId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ExternalReference>> GetWorkSourcesAsync(
        Guid workId,
        CancellationToken cancellationToken);

    Task<Author?> FindAuthorByNormalizedNameAsync(
        string normalizedName,
        CancellationToken cancellationToken);

    Task<Series?> FindSeriesByNormalizedNameAsync(
        string normalizedName,
        CancellationToken cancellationToken);

    /// <summary>The Series with its <c>Entries</c> (and each entry's Work)
    /// loaded, for following/gap-detection.</summary>
    Task<Series?> GetSeriesAsync(Guid seriesId, CancellationToken cancellationToken);

    /// <summary>The Author with its <c>WorkAuthors</c> (and each Work) loaded,
    /// for following/gap-detection.</summary>
    Task<Author?> GetAuthorAsync(Guid authorId, CancellationToken cancellationToken);

    void AddWork(Work work);

    void AddAuthor(Author author);

    void AddSeries(Series series);

    void AddExternalReference(ExternalReference externalReference);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
