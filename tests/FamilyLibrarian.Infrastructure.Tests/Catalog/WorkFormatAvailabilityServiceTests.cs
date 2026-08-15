using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Infrastructure.Tests.Catalog;

[TestClass]
public sealed class WorkFormatAvailabilityServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task AWorkWithNoRequestsIsNotOwnedAndHasNoAcquisitionState()
    {
        var service = new WorkFormatAvailabilityService(new StubRequestRepository([]));

        var availability = await service.GetAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        Assert.HasCount(2, availability);
        Assert.IsTrue(availability.All(entry => entry.OwnershipState == OwnershipState.NotOwned));
        Assert.IsTrue(availability.All(entry => entry.AcquisitionState is null));
    }

    [TestMethod]
    public async Task ARequestedFormatReportsAnAcquisitionStateButStaysNotOwned()
    {
        var userId = Guid.NewGuid();
        var workId = Guid.NewGuid();
        var request = new BookRequest(userId, workId, [RequestMediaType.Ebook], null, Now);

        var service = new WorkFormatAvailabilityService(new StubRequestRepository([request]));

        var availability = await service.GetAsync(userId, workId, CancellationToken.None);

        var ebook = availability.Single(entry => entry.MediaType == RequestMediaType.Ebook);
        Assert.AreEqual(AcquisitionState.Requested, ebook.AcquisitionState);
        Assert.AreEqual(OwnershipState.NotOwned, ebook.OwnershipState);

        var audiobook = availability.Single(entry => entry.MediaType == RequestMediaType.Audiobook);
        Assert.IsNull(audiobook.AcquisitionState);
    }

    private sealed class StubRequestRepository(IReadOnlyList<BookRequest> requests) : IRequestRepository
    {
        public Task<bool> WorkExistsAsync(Guid workId, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task<IReadOnlyList<BookRequest>> GetActiveRequestsForWorkAsync(
            Guid userId,
            Guid workId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<BookRequest>>(
                requests.Where(request => request.UserId == userId && request.WorkId == workId).ToArray());

        public Task<BookRequest?> FindOwnedRequestAsync(
            Guid requestId, Guid userId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<BookRequestView>> ListForUserAsync(
            Guid userId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<BookRequestView?> FindViewAsync(
            Guid requestId, Guid userId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<AdminBookRequestView>> ListForAdminAsync(
            RequestStatus? status, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<BookRequest?> FindRequestForAdminAsync(
            Guid requestId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AdminBookRequestView?> FindAdminViewAsync(
            Guid requestId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void AddRequest(BookRequest request) => throw new NotSupportedException();

        public Task SaveChangesAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TResult> InCreateRequestScopeAsync<TResult>(
            Guid userId,
            Guid workId,
            Func<CancellationToken, Task<TResult>> operation,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
