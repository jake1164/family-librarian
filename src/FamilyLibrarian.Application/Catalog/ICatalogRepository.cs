using FamilyLibrarian.Domain.Catalog;

namespace FamilyLibrarian.Application.Catalog;

/// <remarks>
/// Reader-side access. The single-result lookups below (<c>FindWorkByIsbn13Async</c>, <c>FindAuthorByNormalizedNameAsync</c>,
/// <c>FindSeriesByNormalizedNameAsync</c>) assume at most one match and throw when a name or an ISBN set legitimately matches
/// several rows, so no catalog writer uses them: writers go through <see cref="ICatalogWriteScope"/> and its list-based
/// <see cref="ICatalogMutationRepository"/>, which also coordinates them across processes.
/// </remarks>
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

    /// <summary>
    /// Every Series with this normalized name, oldest first, so a reader can tell a name that matches several saved series from
    /// one that matches none. The default keeps an implementation that only knows the single-result lookup working; it can never
    /// report an ambiguity, so the real repository overrides it.
    /// </summary>
    async Task<IReadOnlyList<Series>> ListSeriesByNormalizedNameAsync(string normalizedName, CancellationToken cancellationToken) =>
        await FindSeriesByNormalizedNameAsync(normalizedName, cancellationToken) is { } series ? [series] : [];

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
