using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Feedback;
using FamilyLibrarian.Domain.Feedback;

namespace FamilyLibrarian.Infrastructure.Tests.Feedback;

/// <summary>
/// Ownership, concurrency, and the create-vs-correct behavior behind My
/// Reading and the completion/rating action on Work detail.
/// </summary>
[TestClass]
public sealed class UserWorkFeedbackServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Reader = Guid.NewGuid();
    private static readonly Guid OtherReader = Guid.NewGuid();
    private static readonly Guid Work = Guid.NewGuid();
    private static readonly DateOnly CompletedOn = new(2026, 8, 1);

    [TestMethod]
    public async Task SetFeedbackAsyncCreatesARowForTheSignedInUser()
    {
        var repository = new InMemoryFeedbackRepository();
        var service = Create(repository, Reader);

        var result = await service.SetFeedbackAsync(
            Work, CompletedOn, 4, expectedVersion: null, CancellationToken.None);

        Assert.AreEqual(SetFeedbackOutcome.Success, result.Outcome);
        var stored = repository.Rows.Single();
        Assert.AreEqual(Reader, stored.UserId);
        Assert.AreEqual(Work, stored.WorkId);
        Assert.AreEqual(4, stored.Rating);
    }

    [TestMethod]
    public async Task SettingFeedbackASecondTimeCorrectsTheExistingRowInstead()
    {
        var repository = new InMemoryFeedbackRepository();
        var service = Create(repository, Reader);

        var first = await service.SetFeedbackAsync(
            Work, CompletedOn, 3, expectedVersion: null, CancellationToken.None);

        var second = await service.SetFeedbackAsync(
            Work, CompletedOn.AddDays(1), 5, first.Feedback!.Version, CancellationToken.None);

        Assert.AreEqual(SetFeedbackOutcome.Success, second.Outcome);
        Assert.AreEqual(1, repository.Rows.Count);
        Assert.AreEqual(5, repository.Rows.Single().Rating);
        Assert.AreEqual(CompletedOn.AddDays(1), repository.Rows.Single().CompletedOn);
    }

    [TestMethod]
    public async Task ARatingOutsideOneToFiveIsRejectedBeforeAnyDatabaseWork()
    {
        var repository = new InMemoryFeedbackRepository();
        var service = Create(repository, Reader);

        var result = await service.SetFeedbackAsync(
            Work, CompletedOn, 6, expectedVersion: null, CancellationToken.None);

        Assert.AreEqual(SetFeedbackOutcome.Invalid, result.Outcome);
        Assert.AreEqual(0, repository.Rows.Count);
    }

    [TestMethod]
    public async Task FeedbackForAnUnknownWorkIsRefused()
    {
        var repository = new InMemoryFeedbackRepository { KnownWorkExists = false };
        var service = Create(repository, Reader);

        var result = await service.SetFeedbackAsync(
            Work, CompletedOn, 3, expectedVersion: null, CancellationToken.None);

        Assert.AreEqual(SetFeedbackOutcome.WorkNotFound, result.Outcome);
        Assert.AreEqual(0, repository.Rows.Count);
    }

    [TestMethod]
    public async Task AnAnonymousCallerRecordsNothing()
    {
        var repository = new InMemoryFeedbackRepository();
        var service = Create(repository, userId: null);

        var result = await service.SetFeedbackAsync(
            Work, CompletedOn, 3, expectedVersion: null, CancellationToken.None);

        Assert.AreEqual(SetFeedbackOutcome.Unauthenticated, result.Outcome);
        Assert.AreEqual(0, repository.Rows.Count);
    }

    [TestMethod]
    public async Task ExpectingAVersionThatDoesNotExistYetIsAConflictNotABlindCreate()
    {
        var repository = new InMemoryFeedbackRepository();
        var service = Create(repository, Reader);

        var result = await service.SetFeedbackAsync(Work, CompletedOn, 3, expectedVersion: 1, CancellationToken.None);

        Assert.AreEqual(SetFeedbackOutcome.Conflict, result.Outcome);
        Assert.AreEqual(0, repository.Rows.Count);
    }

    [TestMethod]
    public async Task ListMineReturnsOnlyTheCallersFeedback()
    {
        var repository = new InMemoryFeedbackRepository();
        repository.Seed(new UserWorkFeedback(Reader, Work, CompletedOn, 4, Now));
        repository.Seed(new UserWorkFeedback(OtherReader, Work, CompletedOn, 2, Now));
        var service = Create(repository, Reader);

        var mine = await service.ListMineAsync(CancellationToken.None);

        Assert.AreEqual(1, mine.Count);
        Assert.AreEqual(4, mine[0].Rating);
    }

    [TestMethod]
    public async Task AUserCannotSeeAnotherUsersFeedback()
    {
        var repository = new InMemoryFeedbackRepository();
        repository.Seed(new UserWorkFeedback(OtherReader, Work, CompletedOn, 5, Now));
        var service = Create(repository, Reader);

        var mine = await service.FindMineAsync(Work, CancellationToken.None);

        Assert.IsNull(mine);
    }

    [TestMethod]
    public async Task AUserCannotCorrectAnotherUsersFeedbackAndIsToldItDoesNotExist()
    {
        var repository = new InMemoryFeedbackRepository();
        var theirs = new UserWorkFeedback(OtherReader, Work, CompletedOn, 5, Now);
        repository.Seed(theirs);
        var service = Create(repository, Reader);

        // No expectedVersion supplied means "create", but a row already exists
        // for this Work under a different user, so nothing should be written.
        var result = await service.SetFeedbackAsync(
            Work, CompletedOn, 1, expectedVersion: null, CancellationToken.None);

        Assert.AreEqual(SetFeedbackOutcome.Success, result.Outcome);
        Assert.AreEqual(2, repository.Rows.Count);
        Assert.AreEqual(5, theirs.Rating);
    }

    [TestMethod]
    public async Task ARequesterCanRemoveTheirOwnFeedback()
    {
        var repository = new InMemoryFeedbackRepository();
        var feedback = new UserWorkFeedback(Reader, Work, CompletedOn, 3, Now);
        repository.Seed(feedback);
        var service = Create(repository, Reader);

        var result = await service.RemoveFeedbackAsync(Work, feedback.Version, CancellationToken.None);

        Assert.AreEqual(RemoveFeedbackOutcome.Success, result.Outcome);
        Assert.AreEqual(0, repository.Rows.Count);
    }

    [TestMethod]
    public async Task AUserCannotRemoveAnotherUsersFeedbackAndIsToldItDoesNotExist()
    {
        var repository = new InMemoryFeedbackRepository();
        var theirs = new UserWorkFeedback(OtherReader, Work, CompletedOn, 3, Now);
        repository.Seed(theirs);
        var service = Create(repository, Reader);

        var result = await service.RemoveFeedbackAsync(Work, theirs.Version, CancellationToken.None);

        // NotFound rather than Forbidden: answering "forbidden" would confirm that
        // someone else's feedback exists.
        Assert.AreEqual(RemoveFeedbackOutcome.NotFound, result.Outcome);
        Assert.AreEqual(1, repository.Rows.Count);
    }

    [TestMethod]
    public async Task RemovingWithAStaleVersionIsAConflictAndLeavesItInPlace()
    {
        var repository = new InMemoryFeedbackRepository();
        var feedback = new UserWorkFeedback(Reader, Work, CompletedOn, 3, Now);
        repository.Seed(feedback);
        var service = Create(repository, Reader);

        var result = await service.RemoveFeedbackAsync(Work, feedback.Version + 1, CancellationToken.None);

        Assert.AreEqual(RemoveFeedbackOutcome.Conflict, result.Outcome);
        Assert.AreEqual(1, repository.Rows.Count);
    }

    private static UserWorkFeedbackService Create(IUserWorkFeedbackRepository repository, Guid? userId) =>
        new(repository, new StubCurrentUser(userId), new FixedClock());

    private sealed class StubCurrentUser(Guid? userId) : ICurrentUser
    {
        public Guid? UserId => userId;

        public string? DisplayName => userId is null ? null : "Reader";
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class InMemoryFeedbackRepository : IUserWorkFeedbackRepository
    {
        public List<UserWorkFeedback> Rows { get; } = [];

        public bool KnownWorkExists { get; init; } = true;

        public void Seed(UserWorkFeedback feedback) => Rows.Add(feedback);

        public Task<bool> WorkExistsAsync(Guid workId, CancellationToken cancellationToken) =>
            Task.FromResult(KnownWorkExists);

        public Task<UserWorkFeedback?> FindOwnedAsync(
            Guid userId,
            Guid workId,
            CancellationToken cancellationToken) =>
            Task.FromResult(Rows.SingleOrDefault(row => row.UserId == userId && row.WorkId == workId));

        public Task<IReadOnlyList<UserWorkFeedbackView>> ListForUserAsync(
            Guid userId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<UserWorkFeedbackView>>(Rows
                .Where(row => row.UserId == userId)
                .Select(ToView)
                .ToArray());

        public Task<UserWorkFeedbackView?> FindViewAsync(
            Guid userId,
            Guid workId,
            CancellationToken cancellationToken) =>
            Task.FromResult(Rows
                .Where(row => row.UserId == userId && row.WorkId == workId)
                .Select(ToView)
                .SingleOrDefault());

        public void Add(UserWorkFeedback feedback) => Rows.Add(feedback);

        public void Remove(UserWorkFeedback feedback) => Rows.Remove(feedback);

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        private static UserWorkFeedbackView ToView(UserWorkFeedback feedback) => new(
            feedback.WorkId,
            "Project Hail Mary",
            ["Andy Weir"],
            null,
            feedback.CompletedOn,
            feedback.Rating,
            feedback.Version);
    }
}
