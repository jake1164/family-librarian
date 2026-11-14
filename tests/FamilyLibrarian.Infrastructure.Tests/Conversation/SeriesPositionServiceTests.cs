using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Domain.Catalog;

namespace FamilyLibrarian.Infrastructure.Tests.Conversation;

/// <summary>
/// The lab returned HTTP 500 for "what comes after X in Y" and for "what comes next" about a book in a series: the series lookup
/// ran its two catalog searches (the series name and the named book) at the same time, and both read provider settings through the
/// request's one scoped database context, which EF Core does not allow to be used in parallel. Fakes cannot reproduce a DbContext, so
/// this pins the invariant that prevents it: the lookup's searches run one at a time.
/// </summary>
[TestClass]
public sealed class SeriesPositionServiceTests
{
    private sealed class OverlapDetectingSearch : ICatalogSearchService
    {
        private int inFlight;
        private int maxInFlight;

        public int MaxInFlight => Volatile.Read(ref maxInFlight);

        public List<string> Queries { get; } = [];

        public async Task<CatalogSearchOutcome> SearchAsync(BookSearchQuery query, CancellationToken cancellationToken)
        {
            lock (Queries)
            {
                Queries.Add(query.Text);
            }

            var now = Interlocked.Increment(ref inFlight);
            int seen;
            while (now > (seen = Volatile.Read(ref maxInFlight)))
            {
                Interlocked.CompareExchange(ref maxInFlight, now, seen);
            }

            await Task.Delay(60, cancellationToken);
            Interlocked.Decrement(ref inFlight);
            return new CatalogSearchOutcome([], [], 1, false, []);
        }

        public Task<ProviderSearchResult> SearchProviderAsync(
            IBookMetadataProvider provider, BookSearchQuery query, bool tolerateUnexpectedFailures, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public CatalogSearchOutcome Compose(IReadOnlyList<ProviderSearchResult> providerResults, BookSearchQuery query) => throw new NotSupportedException();
    }

    /// <summary>Knows no series, so the lookup only exercises its searches.</summary>
    private sealed class EmptyCatalog : ICatalogRepository
    {
        public Task<Series?> FindSeriesByNormalizedNameAsync(string normalizedName, CancellationToken cancellationToken) => Task.FromResult<Series?>(null);

        public Task<Work?> FindWorkByExternalReferenceAsync(string providerId, string externalId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Work?> FindWorkByIsbn13Async(IReadOnlyCollection<string> isbn13s, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Work?> GetWorkAsync(Guid workId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<ExternalReference>> GetWorkSourcesAsync(Guid workId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Author?> FindAuthorByNormalizedNameAsync(string normalizedName, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Series?> GetSeriesAsync(Guid seriesId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Author?> GetAuthorAsync(Guid authorId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void AddWork(Work work) => throw new NotSupportedException();

        public void AddAuthor(Author author) => throw new NotSupportedException();

        public void AddSeries(Series series) => throw new NotSupportedException();

        public void AddExternalReference(ExternalReference externalReference) => throw new NotSupportedException();

        public Task SaveChangesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [TestMethod]
    public async Task TheSeriesAndTheNamedBookAreSearchedOneAfterTheOtherNeverTogether()
    {
        var search = new OverlapDetectingSearch();
        var service = new SeriesPositionService(search, new EmptyCatalog());

        await service.FindAsync("Lantern Road", position: null, afterTitle: "Lanterns at Dusk", maxBooks: 5, CancellationToken.None);

        Assert.AreEqual(1, search.MaxInFlight, "Two searches in flight share one database context and throw.");
        Assert.HasCount(2, search.Queries);
        CollectionAssert.Contains(search.Queries, "Lantern Road", "The series is still searched.");
        CollectionAssert.Contains(search.Queries, "Lanterns at Dusk", "The named book is still searched.");
    }

    [TestMethod]
    public async Task ALookupWithOnlyASeriesNameSearchesOnce()
    {
        var search = new OverlapDetectingSearch();
        var service = new SeriesPositionService(search, new EmptyCatalog());

        await service.FindAsync("Lantern Road", position: 3, afterTitle: null, maxBooks: 5, CancellationToken.None);

        Assert.HasCount(1, search.Queries);
    }
}
