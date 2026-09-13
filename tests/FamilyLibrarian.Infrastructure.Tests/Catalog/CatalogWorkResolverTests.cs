using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Following;
using FamilyLibrarian.Application.Notifications;
using FamilyLibrarian.Domain.Catalog;
using FamilyLibrarian.Domain.Following;
using FamilyLibrarian.Domain.Notifications;

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
    public void GroupExactIsbnMatchesRanksExactTitleMatchesAheadOfBroadProviderMatches()
    {
        var results = BookCandidateGrouper.GroupExactIsbnMatches(
            [
                CreateCandidate("Dim sum of all fears", "dim-sum") with { Editions = [] },
                CreateCandidate("Kol ha-peḥadim kulam", "translated") with { Editions = [] },
                CreateCandidate("Sea of Islands", "sea") with { Editions = [] },
                CreateCandidate("The Sum of All Fears", "sum-of-all-fears") with { Editions = [] }
            ],
            "sum of all fears");

        Assert.HasCount(4, results);
        Assert.AreEqual("The Sum of All Fears", results[0].Title);
    }

    [TestMethod]
    public void GroupExactIsbnMatchesRanksPreferredLanguageAheadOfOtherLanguagesOnTiedMatchKind()
    {
        // Titles are chosen so that alphabetical order alone (the tiebreak below
        // the language rank) would put the Spanish edition first; only the
        // language tiebreak should push it behind the English and unknown-language
        // candidates.
        var spanish = CreateCandidate("Amenaza inminente", "spanish-edition") with
        {
            Editions = [],
            Language = "es"
        };
        var english = CreateCandidate("Clear and Present Danger", "english-edition") with
        {
            Editions = [],
            Language = "en"
        };
        var unknownLanguage = CreateCandidate("Bystander", "unknown-language") with
        {
            Editions = [],
            Language = null
        };

        var results = BookCandidateGrouper.GroupExactIsbnMatches(
            [spanish, english, unknownLanguage],
            "tom clancy");

        Assert.HasCount(3, results);
        Assert.AreEqual("unknown-language", results[0].ExternalId);
        Assert.AreEqual("english-edition", results[1].ExternalId);
        Assert.AreEqual("spanish-edition", results[2].ExternalId);
    }

    [TestMethod]
    public async Task ResolveAsyncNotifiesEveryFollowerOfAMatchedSeriesAndAuthor()
    {
        var repository = new InMemoryCatalogRepository();
        var existingSeries = new Series("Project Hail Mary Universe", SeriesStatus.Unknown, Now);
        repository.AddSeries(existingSeries);
        var existingAuthor = new Author("Andy Weir", null, Now);
        repository.AddAuthor(existingAuthor);

        var follows = new InMemoryFollowRepository();
        var seriesFollowerId = Guid.NewGuid();
        var authorFollowerId = Guid.NewGuid();
        follows.Seed(new Follow(seriesFollowerId, FollowSubjectType.Series, existingSeries.Id, Now));
        follows.Seed(new Follow(authorFollowerId, FollowSubjectType.Author, existingAuthor.Id, Now));

        var notificationRepository = new RecordingNotificationRepository();
        var notifications = new NotificationService(notificationRepository, new StubCurrentUser(), new FixedClock());

        var provider = new StubProvider(CreateCandidate());
        var resolver = new CatalogWorkResolver([provider], repository, follows, notifications, new FixedClock());

        var result = await resolver.ResolveAsync("stub", "work-1", CancellationToken.None);

        Assert.HasCount(2, notificationRepository.Added);
        var seriesNotification = notificationRepository.Added.Single(
            notification => notification.Category == NotificationCategories.SeriesNewEntryDetected);
        Assert.AreEqual(seriesFollowerId, seriesNotification.RecipientUserId);
        Assert.AreEqual(NotificationSubjectTypes.Work, seriesNotification.SubjectType);
        Assert.AreEqual(result.Work.Id.ToString(), seriesNotification.SubjectId);
        StringAssert.Contains(seriesNotification.Title, "Project Hail Mary");

        var authorNotification = notificationRepository.Added.Single(
            notification => notification.Category == NotificationCategories.AuthorNewWorkDetected);
        Assert.AreEqual(authorFollowerId, authorNotification.RecipientUserId);
        StringAssert.Contains(authorNotification.Title, "Project Hail Mary");
    }

    [TestMethod]
    public async Task ResolveAsyncDoesNotNotifyForABrandNewSeriesOrAuthorNobodyCouldHaveFollowedYet()
    {
        var repository = new InMemoryCatalogRepository();
        var follows = new InMemoryFollowRepository();
        var notificationRepository = new RecordingNotificationRepository();
        var notifications = new NotificationService(notificationRepository, new StubCurrentUser(), new FixedClock());
        var provider = new StubProvider(CreateCandidate());
        var resolver = new CatalogWorkResolver([provider], repository, follows, notifications, new FixedClock());

        await resolver.ResolveAsync("stub", "work-1", CancellationToken.None);

        // Neither the series nor the author existed before this resolve, so
        // ListFollowersAsync is never even asked about them.
        Assert.IsEmpty(notificationRepository.Added);
        Assert.IsEmpty(follows.FollowersQueriedFor);
    }

    [TestMethod]
    public async Task ResolveAsyncCreatesCanonicalWorkAndProvenance()
    {
        var repository = new InMemoryCatalogRepository();
        var provider = new StubProvider(CreateCandidate());
        var resolver = new CatalogWorkResolver(
            [provider], repository, new EmptyFollowRepository(), CreateNotificationService(), new FixedClock());

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
    public async Task ResolveAsyncPrefersAnExplicitPositionSortOverParsingTheLabel()
    {
        var repository = new InMemoryCatalogRepository();
        var candidate = CreateCandidate() with
        {
            Series = [new BookSeriesCandidate("Project Hail Mary Universe", "3rd", true, PositionSort: 3m)]
        };
        var provider = new StubProvider(candidate);
        var resolver = new CatalogWorkResolver(
            [provider], repository, new EmptyFollowRepository(), CreateNotificationService(), new FixedClock());

        var result = await resolver.ResolveAsync("stub", "work-1", CancellationToken.None);

        var entry = result.Work.SeriesEntries.Single();
        Assert.AreEqual(3m, entry.PositionSort);
        Assert.AreEqual("3rd", entry.PositionLabel);
    }

    [TestMethod]
    public async Task ResolveAsyncMarksANewlyDiscoveredSeriesCompletedWhenTheCandidateSaysSo()
    {
        var repository = new InMemoryCatalogRepository();
        var candidate = CreateCandidate() with
        {
            Series = [new BookSeriesCandidate("Project Hail Mary Universe", "1", true, IsCompleted: true)]
        };
        var provider = new StubProvider(candidate);
        var resolver = new CatalogWorkResolver(
            [provider], repository, new EmptyFollowRepository(), CreateNotificationService(), new FixedClock());

        var result = await resolver.ResolveAsync("stub", "work-1", CancellationToken.None);

        Assert.AreEqual(SeriesStatus.Completed, result.Work.SeriesEntries.Single().Series.Status);
    }

    [TestMethod]
    public async Task ResolveAsyncMarksAnExistingUnknownSeriesCompletedWhenALaterCandidateSaysSo()
    {
        var repository = new InMemoryCatalogRepository();
        var existingSeries = new Series("Project Hail Mary Universe", SeriesStatus.Unknown, Now);
        repository.AddSeries(existingSeries);
        var candidate = CreateCandidate() with
        {
            Series = [new BookSeriesCandidate("Project Hail Mary Universe", "1", true, IsCompleted: true)]
        };
        var provider = new StubProvider(candidate);
        var resolver = new CatalogWorkResolver(
            [provider], repository, new EmptyFollowRepository(), CreateNotificationService(), new FixedClock());

        var result = await resolver.ResolveAsync("stub", "work-1", CancellationToken.None);

        Assert.AreSame(existingSeries, result.Work.SeriesEntries.Single().Series);
        Assert.AreEqual(SeriesStatus.Completed, existingSeries.Status);
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
        var resolver = new CatalogWorkResolver(
            [provider], repository, new EmptyFollowRepository(), CreateNotificationService(), new FixedClock());

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
        var resolver = new CatalogWorkResolver(
            [provider], repository, new EmptyFollowRepository(), CreateNotificationService(), new FixedClock());

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

    // No test here ever follows a series/author, so ListFollowersAsync always
    // returns empty and NotificationService is never actually called through
    // it -- these exist only to satisfy CatalogWorkResolver's constructor.
    private static NotificationService CreateNotificationService() =>
        new(new ThrowingNotificationRepository(), new StubCurrentUser(), new FixedClock());

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class StubCurrentUser : ICurrentUser
    {
        public Guid? UserId => null;
        public string? DisplayName => null;
    }

    private sealed class EmptyFollowRepository : IFollowRepository
    {
        public Task<Follow?> FindAsync(
            Guid userId, FollowSubjectType subjectType, Guid subjectId, CancellationToken cancellationToken) =>
            Task.FromResult<Follow?>(null);

        public Task<IReadOnlyList<Follow>> ListForUserAsync(Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Follow>>([]);

        public Task<IReadOnlyList<Follow>> ListFollowersAsync(
            FollowSubjectType subjectType, Guid subjectId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Follow>>([]);

        public void Add(Follow follow) => throw new NotSupportedException("Not exercised by these tests.");

        public void Remove(Follow follow) => throw new NotSupportedException("Not exercised by these tests.");

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class InMemoryFollowRepository : IFollowRepository
    {
        private readonly List<Follow> _rows = [];

        public List<(FollowSubjectType SubjectType, Guid SubjectId)> FollowersQueriedFor { get; } = [];

        public void Seed(Follow follow) => _rows.Add(follow);

        public Task<Follow?> FindAsync(
            Guid userId, FollowSubjectType subjectType, Guid subjectId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<IReadOnlyList<Follow>> ListForUserAsync(Guid userId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<IReadOnlyList<Follow>> ListFollowersAsync(
            FollowSubjectType subjectType, Guid subjectId, CancellationToken cancellationToken)
        {
            FollowersQueriedFor.Add((subjectType, subjectId));
            return Task.FromResult<IReadOnlyList<Follow>>(_rows
                .Where(follow => follow.SubjectType == subjectType && follow.SubjectId == subjectId)
                .ToArray());
        }

        public void Add(Follow follow) => throw new NotSupportedException("Not exercised by these tests.");

        public void Remove(Follow follow) => throw new NotSupportedException("Not exercised by these tests.");

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingNotificationRepository : INotificationRepository
    {
        public List<NotificationEvent> Added { get; } = [];

        public Task<NotificationEvent?> FindLatestAsync(
            NotificationAudience audience, Guid? recipientUserId, string category,
            string? subjectType, string? subjectId, CancellationToken cancellationToken) =>
            Task.FromResult<NotificationEvent?>(null);

        public Task AddAsync(NotificationEvent notification, CancellationToken cancellationToken)
        {
            Added.Add(notification);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<NotificationReceipt>> ListReceiptsAsync(
            Guid notificationEventId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task RemoveReceiptsAsync(
            IReadOnlyList<NotificationReceipt> receipts, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<IReadOnlyList<(NotificationEvent Event, NotificationReceipt? Receipt)>> ListForViewerAsync(
            Guid userId, bool isAdmin, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<NotificationReceipt?> FindReceiptAsync(
            Guid notificationEventId, Guid userId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task AddReceiptAsync(NotificationReceipt receipt, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ThrowingNotificationRepository : INotificationRepository
    {
        public Task<NotificationEvent?> FindLatestAsync(
            NotificationAudience audience, Guid? recipientUserId, string category,
            string? subjectType, string? subjectId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task AddAsync(NotificationEvent notification, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<IReadOnlyList<NotificationReceipt>> ListReceiptsAsync(
            Guid notificationEventId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task RemoveReceiptsAsync(
            IReadOnlyList<NotificationReceipt> receipts, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<IReadOnlyList<(NotificationEvent Event, NotificationReceipt? Receipt)>> ListForViewerAsync(
            Guid userId, bool isAdmin, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<NotificationReceipt?> FindReceiptAsync(
            Guid notificationEventId, Guid userId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task AddReceiptAsync(NotificationReceipt receipt, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task SaveChangesAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");
    }

    private sealed class StubProvider(BookCandidate candidate) : IBookMetadataProvider
    {
        public bool ThrowIfCalled { get; init; }

        public int GetDetailsCallCount { get; private set; }

        public string Id => "stub";

        public string DisplayName => "Stub catalog";

        public Task<BookCandidateSearchPage> SearchAsync(
            BookSearchQuery query,
            CancellationToken cancellationToken) =>
            Task.FromResult(new BookCandidateSearchPage([candidate], false));

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

        public Task<Series?> GetSeriesAsync(Guid seriesId, CancellationToken cancellationToken) =>
            Task.FromResult(_series.SingleOrDefault(series => series.Id == seriesId));

        public Task<Author?> GetAuthorAsync(Guid authorId, CancellationToken cancellationToken) =>
            Task.FromResult(_authors.SingleOrDefault(author => author.Id == authorId));

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
