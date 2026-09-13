using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Feedback;
using FamilyLibrarian.Application.Following;
using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Domain.Catalog;
using FamilyLibrarian.Domain.Feedback;
using FamilyLibrarian.Domain.Following;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Infrastructure.Tests.Following;

/// <summary>
/// Ownership, idempotency, and the per-user read/owned/caught-up computation
/// behind Following.
/// </summary>
[TestClass]
public sealed class FollowServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Reader = Guid.NewGuid();

    [TestMethod]
    public async Task FollowSeriesAsyncCreatesAFollowForAnExistingSeries()
    {
        var series = new Series("Jack Ryan Universe", SeriesStatus.Unknown, Now);
        var context = CreateContext(Reader);
        context.Catalog.Seed(series);

        var result = await context.Service.FollowSeriesAsync(series.Id, CancellationToken.None);

        Assert.AreEqual(FollowOutcome.Success, result.Outcome);
        var stored = context.Follows.Rows.Single();
        Assert.AreEqual(Reader, stored.UserId);
        Assert.AreEqual(FollowSubjectType.Series, stored.SubjectType);
        Assert.AreEqual(series.Id, stored.SubjectId);
    }

    [TestMethod]
    public async Task FollowSeriesAsyncIsIdempotentWhenAlreadyFollowing()
    {
        var series = new Series("Jack Ryan Universe", SeriesStatus.Unknown, Now);
        var context = CreateContext(Reader);
        context.Catalog.Seed(series);

        await context.Service.FollowSeriesAsync(series.Id, CancellationToken.None);
        var second = await context.Service.FollowSeriesAsync(series.Id, CancellationToken.None);

        Assert.AreEqual(FollowOutcome.Success, second.Outcome);
        Assert.AreEqual(1, context.Follows.Rows.Count);
    }

    [TestMethod]
    public async Task FollowSeriesAsyncReturnsNotFoundForAnUnknownSeries()
    {
        var context = CreateContext(Reader);

        var result = await context.Service.FollowSeriesAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.AreEqual(FollowOutcome.NotFound, result.Outcome);
        Assert.AreEqual(0, context.Follows.Rows.Count);
    }

    [TestMethod]
    public async Task AnAnonymousCallerCannotFollowAnything()
    {
        var series = new Series("Jack Ryan Universe", SeriesStatus.Unknown, Now);
        var context = CreateContext(userId: null);
        context.Catalog.Seed(series);

        var result = await context.Service.FollowSeriesAsync(series.Id, CancellationToken.None);

        Assert.AreEqual(FollowOutcome.Unauthenticated, result.Outcome);
        Assert.AreEqual(0, context.Follows.Rows.Count);
    }

    [TestMethod]
    public async Task UnfollowSeriesAsyncRemovesTheRow()
    {
        var series = new Series("Jack Ryan Universe", SeriesStatus.Unknown, Now);
        var context = CreateContext(Reader);
        context.Catalog.Seed(series);
        await context.Service.FollowSeriesAsync(series.Id, CancellationToken.None);

        var removed = await context.Service.UnfollowAsync(
            FollowSubjectType.Series, series.Id, CancellationToken.None);

        Assert.IsTrue(removed);
        Assert.AreEqual(0, context.Follows.Rows.Count);
    }

    [TestMethod]
    public async Task UnfollowingSomethingNeverFollowedReportsFalse()
    {
        var context = CreateContext(Reader);

        var removed = await context.Service.UnfollowAsync(
            FollowSubjectType.Series, Guid.NewGuid(), CancellationToken.None);

        Assert.IsFalse(removed);
    }

    [TestMethod]
    public async Task ListMyFollowedSeriesAsyncComputesReadOwnedAndCaughtUp()
    {
        var series = new Series("Jack Ryan Universe", SeriesStatus.Unknown, Now);
        var book1 = CreateWork("The Hunt for Red October");
        var book2 = CreateWork("Patriot Games");
        AddToSeries(series, book1, "1", 1m, isPrimary: true);
        AddToSeries(series, book2, "2", 2m, isPrimary: true);

        var context = CreateContext(Reader);
        context.Catalog.Seed(series);
        context.Feedback.Seed(new UserWorkFeedback(Reader, book1.Id, new DateOnly(2026, 1, 1), Now));
        context.Requests.Seed(book1.Id, RequestStatus.Available);
        context.Requests.Seed(book2.Id, RequestStatus.Available);
        await context.Service.FollowSeriesAsync(series.Id, CancellationToken.None);

        var mine = await context.Service.ListMyFollowedSeriesAsync(CancellationToken.None);

        Assert.HasCount(1, mine);
        var view = mine[0];
        Assert.AreEqual("Jack Ryan Universe", view.SeriesName);
        Assert.IsFalse(view.IsCaughtUp, "Book 2 has not been read.");
        var entry1 = view.Entries.Single(entry => entry.WorkId == book1.Id);
        Assert.IsTrue(entry1.IsRead);
        Assert.IsTrue(entry1.IsOwned);
        var entry2 = view.Entries.Single(entry => entry.WorkId == book2.Id);
        Assert.IsFalse(entry2.IsRead);
        Assert.IsTrue(entry2.IsOwned);
    }

    [TestMethod]
    public async Task ListMyFollowedSeriesAsyncCaughtUpOnlyCountsPrimaryEntries()
    {
        var series = new Series("Jack Ryan Universe", SeriesStatus.Unknown, Now);
        var primaryBook = CreateWork("The Hunt for Red October");
        var omnibus = CreateWork("The Jack Ryan Omnibus");
        AddToSeries(series, primaryBook, "1", 1m, isPrimary: true);
        AddToSeries(series, omnibus, null, null, isPrimary: false);

        var context = CreateContext(Reader);
        context.Catalog.Seed(series);
        // Only the primary entry is read; the unread, non-primary omnibus
        // must not prevent "caught up".
        context.Feedback.Seed(new UserWorkFeedback(Reader, primaryBook.Id, new DateOnly(2026, 1, 1), Now));
        await context.Service.FollowSeriesAsync(series.Id, CancellationToken.None);

        var mine = await context.Service.ListMyFollowedSeriesAsync(CancellationToken.None);

        Assert.IsTrue(mine.Single().IsCaughtUp);
    }

    [TestMethod]
    public async Task FollowAuthorAsyncCreatesAFollowForAnExistingAuthor()
    {
        var author = new Author("Tom Clancy", null, Now);
        var context = CreateContext(Reader);
        context.Catalog.Seed(author);

        var result = await context.Service.FollowAuthorAsync(author.Id, CancellationToken.None);

        Assert.AreEqual(FollowOutcome.Success, result.Outcome);
        Assert.AreEqual(FollowSubjectType.Author, context.Follows.Rows.Single().SubjectType);
    }

    [TestMethod]
    public async Task ListMyFollowedAuthorsAsyncComputesReadAndOwnedPerWork()
    {
        var author = new Author("Tom Clancy", null, Now);
        var work = CreateWork("The Sum of All Fears");
        AddAuthorToWork(author, work);

        var context = CreateContext(Reader);
        context.Catalog.Seed(author);
        context.Requests.Seed(work.Id, RequestStatus.Available);
        await context.Service.FollowAuthorAsync(author.Id, CancellationToken.None);

        var mine = await context.Service.ListMyFollowedAuthorsAsync(CancellationToken.None);

        Assert.HasCount(1, mine);
        var workView = mine[0].Works.Single();
        Assert.AreEqual("The Sum of All Fears", workView.WorkTitle);
        Assert.IsFalse(workView.IsRead);
        Assert.IsTrue(workView.IsOwned);
    }

    private static Work CreateWork(string title) =>
        new(title, null, null, null, PublicationStatus.Unknown, Now);

    /// <summary>
    /// Wires a SeriesEntry onto both sides of the relationship. Production
    /// code only ever needs <c>Work.AddSeriesEntry</c> — the inverse
    /// (<c>Series.Entries</c>) is populated by EF Core's own query-time
    /// <c>.Include()</c> in <c>CatalogRepository.GetSeriesAsync</c>, not by
    /// in-memory object-graph fixup, so a hand-built test fixture has to set
    /// both sides itself to accurately stand in for a real query.
    /// </summary>
    private static void AddToSeries(
        Series series, Work work, string? positionLabel, decimal? positionSort, bool isPrimary)
    {
        var entry = new SeriesEntry(series, work, positionLabel, positionSort, isPrimary, Now);
        work.AddSeriesEntry(entry);
        series.Entries.Add(entry);
    }

    /// <summary>Same reasoning as <see cref="AddToSeries"/>, for
    /// <c>Author.WorkAuthors</c>.</summary>
    private static void AddAuthorToWork(Author author, Work work)
    {
        work.AddAuthor(author, 0);
        author.WorkAuthors.Add(work.Authors.Single());
    }

    private static TestContext CreateContext(Guid? userId)
    {
        var follows = new InMemoryFollowRepository();
        var catalog = new StubCatalogRepository();
        var feedback = new InMemoryFeedbackRepository();
        var requests = new StubRequestRepository();
        var service = new FollowService(
            follows, catalog, feedback, requests, new StubCurrentUser(userId), new FixedClock());
        return new TestContext(service, follows, catalog, feedback, requests);
    }

    private sealed record TestContext(
        FollowService Service,
        InMemoryFollowRepository Follows,
        StubCatalogRepository Catalog,
        InMemoryFeedbackRepository Feedback,
        StubRequestRepository Requests);

    private sealed class StubCurrentUser(Guid? userId) : ICurrentUser
    {
        public Guid? UserId => userId;
        public string? DisplayName => userId is null ? null : "Reader";
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class InMemoryFollowRepository : IFollowRepository
    {
        public List<Follow> Rows { get; } = [];

        public Task<Follow?> FindAsync(
            Guid userId, FollowSubjectType subjectType, Guid subjectId, CancellationToken cancellationToken) =>
            Task.FromResult(Rows.SingleOrDefault(follow =>
                follow.UserId == userId && follow.SubjectType == subjectType && follow.SubjectId == subjectId));

        public Task<IReadOnlyList<Follow>> ListForUserAsync(Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Follow>>(Rows.Where(follow => follow.UserId == userId).ToArray());

        public Task<IReadOnlyList<Follow>> ListFollowersAsync(
            FollowSubjectType subjectType, Guid subjectId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Follow>>(Rows
                .Where(follow => follow.SubjectType == subjectType && follow.SubjectId == subjectId)
                .ToArray());

        public void Add(Follow follow) => Rows.Add(follow);

        public void Remove(Follow follow) => Rows.Remove(follow);

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StubCatalogRepository : ICatalogRepository
    {
        private readonly Dictionary<Guid, Series> _series = [];
        private readonly Dictionary<Guid, Author> _authors = [];

        public void Seed(Series series) => _series[series.Id] = series;

        public void Seed(Author author) => _authors[author.Id] = author;

        public Task<Series?> GetSeriesAsync(Guid seriesId, CancellationToken cancellationToken) =>
            Task.FromResult(_series.GetValueOrDefault(seriesId));

        public Task<Author?> GetAuthorAsync(Guid authorId, CancellationToken cancellationToken) =>
            Task.FromResult(_authors.GetValueOrDefault(authorId));

        public Task<Work?> FindWorkByExternalReferenceAsync(
            string providerId, string externalId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<Work?> FindWorkByIsbn13Async(
            IReadOnlyCollection<string> isbn13s, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<Work?> GetWorkAsync(Guid workId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<IReadOnlyList<ExternalReference>> GetWorkSourcesAsync(
            Guid workId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<Author?> FindAuthorByNormalizedNameAsync(
            string normalizedName, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<Series?> FindSeriesByNormalizedNameAsync(
            string normalizedName, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public void AddWork(Work work) => throw new NotSupportedException("Not exercised by these tests.");

        public void AddAuthor(Author author) => throw new NotSupportedException("Not exercised by these tests.");

        public void AddSeries(Series series) => throw new NotSupportedException("Not exercised by these tests.");

        public void AddExternalReference(ExternalReference externalReference) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class InMemoryFeedbackRepository : IUserWorkFeedbackRepository
    {
        private readonly List<UserWorkFeedback> _rows = [];

        public void Seed(UserWorkFeedback feedback) => _rows.Add(feedback);

        public Task<bool> WorkExistsAsync(Guid workId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<UserWorkFeedback?> FindOwnedAsync(
            Guid userId, Guid workId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<IReadOnlyList<UserWorkFeedbackView>> ListForUserAsync(
            Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<UserWorkFeedbackView>>(_rows
                .Where(row => row.UserId == userId)
                .Select(row => new UserWorkFeedbackView(row.WorkId, "Title", [], null, row.CompletedOn, row.Version))
                .ToArray());

        public Task<UserWorkFeedbackView?> FindViewAsync(
            Guid userId, Guid workId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public void Add(UserWorkFeedback feedback) => throw new NotSupportedException("Not exercised by these tests.");

        public void Remove(UserWorkFeedback feedback) => throw new NotSupportedException("Not exercised by these tests.");

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StubRequestRepository : IRequestRepository
    {
        private readonly List<(Guid WorkId, RequestStatus Status)> _rows = [];

        public void Seed(Guid workId, RequestStatus status) => _rows.Add((workId, status));

        public Task<IReadOnlyList<BookRequestView>> ListForUserAsync(
            Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<BookRequestView>>(_rows
                .Select(row => new BookRequestView(
                    Guid.NewGuid(), row.WorkId, "Title", [], null, row.Status, [], null, null,
                    Now, Now, 0))
                .ToArray());

        public Task<bool> WorkExistsAsync(Guid workId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<IReadOnlyList<Domain.Requests.BookRequest>> GetActiveRequestsForWorkAsync(
            Guid userId, Guid workId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<Domain.Requests.BookRequest?> FindOwnedRequestAsync(
            Guid requestId, Guid userId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<BookRequestView?> FindViewAsync(
            Guid requestId, Guid userId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<IReadOnlyList<AdminBookRequestView>> ListForAdminAsync(
            RequestStatus? status, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<Domain.Requests.BookRequest?> FindRequestForAdminAsync(
            Guid requestId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<AdminBookRequestView?> FindAdminViewAsync(
            Guid requestId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public void AddRequest(Domain.Requests.BookRequest request) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<TResult> InCreateRequestScopeAsync<TResult>(
            Guid userId,
            Guid workId,
            Func<CancellationToken, Task<TResult>> operation,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");
    }
}
