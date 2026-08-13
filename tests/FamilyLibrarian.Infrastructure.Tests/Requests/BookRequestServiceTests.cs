using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Infrastructure.Tests.Requests;

/// <summary>
/// Duplicate detection, ownership, and the transitions a requester may make.
/// </summary>
[TestClass]
public sealed class BookRequestServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 13, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Reader = Guid.NewGuid();
    private static readonly Guid OtherReader = Guid.NewGuid();
    private static readonly Guid Work = Guid.NewGuid();

    [TestMethod]
    public async Task CreateAsyncStoresARequestForTheSignedInUser()
    {
        var repository = new InMemoryRequestRepository();
        var service = Create(repository, Reader);

        var result = await service.CreateAsync(
            Work,
            [RequestMediaType.Ebook, RequestMediaType.Audiobook],
            "Please, for the trip.",
            confirmDuplicate: false,
            CancellationToken.None);

        Assert.AreEqual(CreateBookRequestOutcome.Created, result.Outcome);
        var stored = repository.Requests.Single();
        Assert.AreEqual(Reader, stored.UserId);
        Assert.AreEqual(Work, stored.WorkId);
        Assert.AreEqual(RequestStatus.PendingAcquisition, stored.Status);
        Assert.AreEqual(2, stored.Formats.Count);
        Assert.AreEqual("Please, for the trip.", stored.RequesterNote);
    }

    [TestMethod]
    public async Task CreateAsyncTakesTheUserWorkLockBeforeCheckingForDuplicates()
    {
        var repository = new InMemoryRequestRepository();
        var service = Create(repository, Reader);

        await service.CreateAsync(
            Work,
            [RequestMediaType.Ebook],
            null,
            confirmDuplicate: false,
            CancellationToken.None);

        // Without the lock, two simultaneous submissions would both read an empty
        // set and both insert.
        Assert.AreEqual(1, repository.ScopeCount);
    }

    [TestMethod]
    public async Task AnOverlappingFormatWarnsInsteadOfCreatingASecondRequest()
    {
        var repository = new InMemoryRequestRepository();
        repository.Seed(new BookRequest(Reader, Work, [RequestMediaType.Ebook], null, Now));
        var service = Create(repository, Reader);

        var result = await service.CreateAsync(
            Work,
            [RequestMediaType.Ebook, RequestMediaType.Audiobook],
            null,
            confirmDuplicate: false,
            CancellationToken.None);

        Assert.AreEqual(CreateBookRequestOutcome.DuplicateWarning, result.Outcome);
        CollectionAssert.AreEqual(
            new[] { RequestMediaType.Ebook },
            result.OverlappingFormats.ToArray());
        Assert.AreEqual(1, repository.Requests.Count);
    }

    [TestMethod]
    public async Task ANonOverlappingFormatIsNotADuplicate()
    {
        var repository = new InMemoryRequestRepository();
        repository.Seed(new BookRequest(Reader, Work, [RequestMediaType.Ebook], null, Now));
        var service = Create(repository, Reader);

        var result = await service.CreateAsync(
            Work,
            [RequestMediaType.Audiobook],
            null,
            confirmDuplicate: false,
            CancellationToken.None);

        Assert.AreEqual(CreateBookRequestOutcome.Created, result.Outcome);
        Assert.AreEqual(2, repository.Requests.Count);
    }

    [TestMethod]
    public async Task AConfirmedRepeatRequestIsAllowedThrough()
    {
        var repository = new InMemoryRequestRepository();
        repository.Seed(new BookRequest(Reader, Work, [RequestMediaType.Ebook], null, Now));
        var service = Create(repository, Reader);

        var result = await service.CreateAsync(
            Work,
            [RequestMediaType.Ebook],
            null,
            confirmDuplicate: true,
            CancellationToken.None);

        Assert.AreEqual(CreateBookRequestOutcome.Created, result.Outcome);
        Assert.AreEqual(2, repository.Requests.Count);
    }

    [TestMethod]
    public async Task AClosedRequestDoesNotBlockANewOne()
    {
        var repository = new InMemoryRequestRepository();
        var closed = new BookRequest(Reader, Work, [RequestMediaType.Ebook], null, Now);
        closed.TransitionTo(RequestStatus.Cancelled, Reader, null, Now.AddHours(1));
        repository.Seed(closed);
        var service = Create(repository, Reader);

        var result = await service.CreateAsync(
            Work,
            [RequestMediaType.Ebook],
            null,
            confirmDuplicate: false,
            CancellationToken.None);

        Assert.AreEqual(CreateBookRequestOutcome.Created, result.Outcome);
    }

    [TestMethod]
    public async Task AnotherUsersOutstandingRequestIsNotADuplicate()
    {
        var repository = new InMemoryRequestRepository();
        repository.Seed(new BookRequest(OtherReader, Work, [RequestMediaType.Ebook], null, Now));
        var service = Create(repository, Reader);

        var result = await service.CreateAsync(
            Work,
            [RequestMediaType.Ebook],
            null,
            confirmDuplicate: false,
            CancellationToken.None);

        Assert.AreEqual(CreateBookRequestOutcome.Created, result.Outcome);
    }

    [TestMethod]
    public async Task ARequestForAnUnknownWorkIsRefused()
    {
        var repository = new InMemoryRequestRepository { KnownWorkExists = false };
        var service = Create(repository, Reader);

        var result = await service.CreateAsync(
            Work,
            [RequestMediaType.Ebook],
            null,
            confirmDuplicate: false,
            CancellationToken.None);

        Assert.AreEqual(CreateBookRequestOutcome.WorkNotFound, result.Outcome);
        Assert.AreEqual(0, repository.Requests.Count);
    }

    [TestMethod]
    public async Task AnAnonymousCallerCreatesNothing()
    {
        var repository = new InMemoryRequestRepository();
        var service = Create(repository, userId: null);

        var result = await service.CreateAsync(
            Work,
            [RequestMediaType.Ebook],
            null,
            confirmDuplicate: false,
            CancellationToken.None);

        Assert.AreEqual(CreateBookRequestOutcome.Unauthenticated, result.Outcome);
        Assert.AreEqual(0, repository.Requests.Count);
    }

    [TestMethod]
    public async Task ARequestWithNoFormatIsRefusedBeforeAnyDatabaseWork()
    {
        var repository = new InMemoryRequestRepository();
        var service = Create(repository, Reader);

        var result = await service.CreateAsync(
            Work,
            [],
            null,
            confirmDuplicate: false,
            CancellationToken.None);

        Assert.AreEqual(CreateBookRequestOutcome.Invalid, result.Outcome);
        Assert.AreEqual(0, repository.ScopeCount);
    }

    [TestMethod]
    public async Task ListMineReturnsOnlyTheCallersRequests()
    {
        var repository = new InMemoryRequestRepository();
        repository.Seed(new BookRequest(Reader, Work, [RequestMediaType.Ebook], null, Now));
        repository.Seed(new BookRequest(OtherReader, Work, [RequestMediaType.Ebook], null, Now));
        var service = Create(repository, Reader);

        var mine = await service.ListMineAsync(CancellationToken.None);

        Assert.AreEqual(1, mine.Count);
        Assert.IsTrue(repository.Requests.Single(request => request.Id == mine[0].Id).UserId == Reader);
    }

    [TestMethod]
    public async Task ARequesterCanCancelTheirOwnRequest()
    {
        var repository = new InMemoryRequestRepository();
        var request = new BookRequest(Reader, Work, [RequestMediaType.Ebook], null, Now);
        repository.Seed(request);
        var service = Create(repository, Reader);

        var result = await service.TransitionAsync(
            request.Id,
            RequestStatus.Cancelled,
            "Changed my mind.",
            CancellationToken.None);

        Assert.AreEqual(BookRequestCommandOutcome.Success, result.Outcome);
        Assert.AreEqual(RequestStatus.Cancelled, request.Status);
        Assert.AreEqual("Changed my mind.", request.StatusHistory.Last().Reason);
    }

    [TestMethod]
    public async Task AUserCannotChangeAnotherUsersRequestAndIsToldItDoesNotExist()
    {
        var repository = new InMemoryRequestRepository();
        var request = new BookRequest(OtherReader, Work, [RequestMediaType.Ebook], null, Now);
        repository.Seed(request);
        var service = Create(repository, Reader);

        var result = await service.TransitionAsync(
            request.Id,
            RequestStatus.Cancelled,
            null,
            CancellationToken.None);

        // NotFound rather than Forbidden: answering "forbidden" would confirm that
        // someone else's request exists.
        Assert.AreEqual(BookRequestCommandOutcome.NotFound, result.Outcome);
        Assert.AreEqual(RequestStatus.PendingAcquisition, request.Status);
    }

    [TestMethod]
    [DataRow(RequestStatus.NeedsReview)]
    [DataRow(RequestStatus.NotAvailable)]
    public async Task ARequesterCannotDriveAnAdministrativeStatus(RequestStatus status)
    {
        var repository = new InMemoryRequestRepository();
        var request = new BookRequest(Reader, Work, [RequestMediaType.Ebook], null, Now);
        repository.Seed(request);
        var service = Create(repository, Reader);

        var result = await service.TransitionAsync(
            request.Id,
            status,
            null,
            CancellationToken.None);

        Assert.AreEqual(BookRequestCommandOutcome.Invalid, result.Outcome);
        Assert.AreEqual(RequestStatus.PendingAcquisition, request.Status);
    }

    [TestMethod]
    public async Task ATransitionThatTheMatrixForbidsIsRefused()
    {
        var repository = new InMemoryRequestRepository();
        var request = new BookRequest(Reader, Work, [RequestMediaType.Ebook], null, Now);
        request.TransitionTo(RequestStatus.Cancelled, Reader, null, Now.AddHours(1));
        repository.Seed(request);
        var service = Create(repository, Reader);

        // Cancelled -> Cancelled is not a move.
        var result = await service.TransitionAsync(
            request.Id,
            RequestStatus.Cancelled,
            null,
            CancellationToken.None);

        Assert.AreEqual(BookRequestCommandOutcome.Invalid, result.Outcome);
        Assert.IsNotNull(result.Error);
    }

    [TestMethod]
    public void OnlyCancelAndReopenAreOfferedToARequester()
    {
        CollectionAssert.AreEqual(
            new[] { RequestStatus.Cancelled },
            BookRequestService.RequesterTransitionsFrom(RequestStatus.PendingAcquisition).ToArray());

        CollectionAssert.AreEqual(
            new[] { RequestStatus.PendingAcquisition },
            BookRequestService.RequesterTransitionsFrom(RequestStatus.Cancelled).ToArray());
    }

    private static BookRequestService Create(IRequestRepository repository, Guid? userId) =>
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

    private sealed class InMemoryRequestRepository : IRequestRepository
    {
        public List<BookRequest> Requests { get; } = [];

        public int ScopeCount { get; private set; }

        public bool KnownWorkExists { get; init; } = true;

        public void Seed(BookRequest request) => Requests.Add(request);

        public Task<bool> WorkExistsAsync(Guid workId, CancellationToken cancellationToken) =>
            Task.FromResult(KnownWorkExists);

        public Task<IReadOnlyList<BookRequest>> GetActiveRequestsForWorkAsync(
            Guid userId,
            Guid workId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<BookRequest>>(Requests
                .Where(request => request.UserId == userId &&
                    request.WorkId == workId &&
                    request.IsActive)
                .ToArray());

        public Task<BookRequest?> FindOwnedRequestAsync(
            Guid requestId,
            Guid userId,
            CancellationToken cancellationToken) =>
            Task.FromResult(Requests.SingleOrDefault(request =>
                request.Id == requestId && request.UserId == userId));

        public Task<IReadOnlyList<BookRequestView>> ListForUserAsync(
            Guid userId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<BookRequestView>>(Requests
                .Where(request => request.UserId == userId)
                .Select(ToView)
                .ToArray());

        public Task<BookRequestView?> FindViewAsync(
            Guid requestId,
            Guid userId,
            CancellationToken cancellationToken) =>
            Task.FromResult(Requests
                .Where(request => request.Id == requestId && request.UserId == userId)
                .Select(ToView)
                .SingleOrDefault());

        public void AddRequest(BookRequest request) => Requests.Add(request);

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<TResult> InCreateRequestScopeAsync<TResult>(
            Guid userId,
            Guid workId,
            Func<CancellationToken, Task<TResult>> operation,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(operation);
            ScopeCount++;
            return operation(cancellationToken);
        }

        private static BookRequestView ToView(BookRequest request) => new(
            request.Id,
            request.WorkId,
            "A Wrinkle in Time",
            ["Madeleine L'Engle"],
            null,
            request.Status,
            request.Formats
                .Select(format => new RequestFormatView(format.MediaType, format.Status))
                .ToArray(),
            request.RequesterNote,
            request.AdminNote,
            request.RequestedAtUtc,
            request.StatusChangedAtUtc);
    }
}
