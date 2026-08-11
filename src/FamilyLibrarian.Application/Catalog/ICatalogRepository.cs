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

    void AddWork(Work work);

    void AddAuthor(Author author);

    void AddSeries(Series series);

    void AddExternalReference(ExternalReference externalReference);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
