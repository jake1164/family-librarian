using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Domain.Catalog;

namespace FamilyLibrarian.Infrastructure.Tests.Catalog;

[TestClass]
public sealed class CatalogWorkResolverTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void GroupExactIsbnMatchesKeepsOnlyTheMostCompleteCandidate()
    {
        var sparse = CreateCandidate() with
        {
            ProviderId = "source-a",
            Description = null,
            Series = []
        };
        var complete = CreateCandidate() with { ProviderId = "source-b" };

        var grouped = BookCandidateGrouper.GroupExactIsbnMatches([sparse, complete]);

        Assert.HasCount(1, grouped);
        Assert.AreEqual("source-b", grouped[0].ProviderId);
    }

    [TestMethod]
    public void GroupExactIsbnMatchesPrefersExactTitleMatchesAndExcludesBroadProviderMatches()
    {
        var results = BookCandidateGrouper.GroupExactIsbnMatches(
            [
                CreateCandidate("Dim sum of all fears", "dim-sum") with { Editions = [] },
                CreateCandidate("Kol ha-peḥadim kulam", "translated") with { Editions = [] },
                CreateCandidate("Sea of Islands", "sea") with { Editions = [] },
                CreateCandidate("The Sum of All Fears", "sum-of-all-fears") with { Editions = [] }
            ],
            "sum of all fears");

        Assert.HasCount(1, results);
        Assert.AreEqual("The Sum of All Fears", results[0].Title);
    }

    [TestMethod]
    public async Task ResolveAsyncCreatesCanonicalWorkAndProvenance()
    {
        var repository = new InMemoryCatalogRepository();
        var provider = new StubProvider(CreateCandidate());
        var resolver = new CatalogWorkResolver([provider], repository, new FixedClock());

        var result = await resolver.ResolveAsync("stub", "work-1", CancellationToken.None);

        Assert.IsTrue(result.WasCreated);
        Assert.AreEqual("Project Hail Mary", result.Work.CanonicalTitle);
        Assert.HasCount(1, result.Work.Authors);
        Assert.AreEqual("Andy Weir", result.Work.Authors.Single().Author.CanonicalName);
        Assert.HasCount(1, result.Work.Editions);
        Assert.AreEqual(EditionFormat.Ebook, result.Work.Editions.Single().Format);
        Assert.AreEqual("9780593135204", result.Work.Editions.Single().Isbn13);
        Assert.HasCount(1, result.Work.SeriesEntries);
        Assert.AreEqual(2.5m, result.Work.SeriesEntries.Single().PositionSort);
        Assert.HasCount(1, repository.ExternalReferences);
        Assert.AreEqual(1, repository.SaveCount);
    }

    [TestMethod]
    public async Task ResolveAsyncReusesExistingProviderReferenceWithoutCallingProvider()
    {
        var repository = new InMemoryCatalogRepository();
        var existing = new Work(
            "Project Hail Mary",
            null,
            null,
            null,
            PublicationStatus.Unknown,
            Now);
        repository.AddWork(existing);
        repository.AddExternalReference(new ExternalReference(
            "stub",
            ExternalReferenceEntityType.Work,
            existing.Id,
            "work-1",
            Now));
        var provider = new StubProvider(CreateCandidate()) { ThrowIfCalled = true };
        var resolver = new CatalogWorkResolver([provider], repository, new FixedClock());

        var result = await resolver.ResolveAsync("stub", "work-1", CancellationToken.None);

        Assert.IsFalse(result.WasCreated);
        Assert.AreSame(existing, result.Work);
        Assert.AreEqual(0, provider.GetDetailsCallCount);
        Assert.AreEqual(0, repository.SaveCount);
    }

    [TestMethod]
    public async Task ResolveAsyncReusesWorkWithMatchingIsbnAndAddsProviderReference()
    {
        var repository = new InMemoryCatalogRepository();
        var existing = new Work(
            "Project Hail Mary",
            null,
            null,
            null,
            PublicationStatus.Unknown,
            Now);
        existing.AddEdition(new Edition(
            existing.Id,
            "Project Hail Mary",
            EditionFormat.Ebook,
            "9780593135204",
            null,
            Now));
        repository.AddWork(existing);
        var provider = new StubProvider(CreateCandidate());
        var resolver = new CatalogWorkResolver([provider], repository, new FixedClock());

        var result = await resolver.ResolveAsync("stub", "work-1", CancellationToken.None);

        Assert.IsFalse(result.WasCreated);
        Assert.AreSame(existing, result.Work);
        Assert.HasCount(1, repository.ExternalReferences);
        Assert.AreEqual(existing.Id, repository.ExternalReferences.Single().EntityId);
    }

    private static BookCandidate CreateCandidate(
        string title = "Project Hail Mary",
        string externalId = "work-1") => new(
        "stub",
        "Stub catalog",
        externalId,
        title,
        ["Andy Weir"],
        "A science-fiction novel.",
        null,
        new DateOnly(2021, 5, 4),
        [new BookEditionCandidate(title, "9780593135204", "Ebook", new DateOnly(2021, 5, 4))],
        [new BookSeriesCandidate("Project Hail Mary Universe", "2.5", true)]);

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class StubProvider(BookCandidate candidate) : IBookMetadataProvider
    {
        public bool ThrowIfCalled { get; init; }

        public int GetDetailsCallCount { get; private set; }

        public string Id => "stub";

        public string DisplayName => "Stub catalog";

        public Task<IReadOnlyList<BookCandidate>> SearchAsync(
            BookSearchQuery query,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<BookCandidate>>([candidate]);

        public Task<BookCandidate?> GetDetailsAsync(string externalId, CancellationToken cancellationToken)
        {
            GetDetailsCallCount++;
            if (ThrowIfCalled)
            {
                throw new InvalidOperationException("The provider should not be called.");
            }

            return Task.FromResult<BookCandidate?>(
                string.Equals(externalId, candidate.ExternalId, StringComparison.Ordinal) ? candidate : null);
        }
    }

    private sealed class InMemoryCatalogRepository : ICatalogRepository
    {
        private readonly List<Work> _works = [];
        private readonly List<Author> _authors = [];
        private readonly List<Series> _series = [];

        public List<ExternalReference> ExternalReferences { get; } = [];

        public int SaveCount { get; private set; }

        public Task<Work?> FindWorkByExternalReferenceAsync(
            string providerId,
            string externalId,
            CancellationToken cancellationToken) =>
            Task.FromResult(_works.SingleOrDefault(work => ExternalReferences.Any(reference =>
                reference.EntityType == ExternalReferenceEntityType.Work &&
                reference.EntityId == work.Id &&
                reference.ProviderId == providerId &&
                reference.ExternalId == externalId)));

        public Task<Work?> FindWorkByIsbn13Async(
            IReadOnlyCollection<string> isbn13s,
            CancellationToken cancellationToken) =>
            Task.FromResult(_works.SingleOrDefault(work => work.Editions.Any(edition =>
                edition.Isbn13 is not null && isbn13s.Contains(edition.Isbn13))));

        public Task<Work?> GetWorkAsync(Guid workId, CancellationToken cancellationToken) =>
            Task.FromResult(_works.SingleOrDefault(work => work.Id == workId));

        public Task<IReadOnlyList<ExternalReference>> GetWorkSourcesAsync(
            Guid workId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExternalReference>>(ExternalReferences
                .Where(reference => reference.EntityType == ExternalReferenceEntityType.Work &&
                    reference.EntityId == workId)
                .ToArray());

        public Task<Author?> FindAuthorByNormalizedNameAsync(
            string normalizedName,
            CancellationToken cancellationToken) =>
            Task.FromResult(_authors.SingleOrDefault(author => author.NormalizedName == normalizedName));

        public Task<Series?> FindSeriesByNormalizedNameAsync(
            string normalizedName,
            CancellationToken cancellationToken) =>
            Task.FromResult(_series.SingleOrDefault(series => series.NormalizedName == normalizedName));

        public void AddWork(Work work) => _works.Add(work);

        public void AddAuthor(Author author) => _authors.Add(author);

        public void AddSeries(Series series) => _series.Add(series);

        public void AddExternalReference(ExternalReference externalReference) =>
            ExternalReferences.Add(externalReference);

        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            SaveCount++;
            return Task.CompletedTask;
        }
    }
}
