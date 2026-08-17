using System.Text;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Infrastructure.Tests.Acquisition;

[TestClass]
public sealed class ManualImportServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task AnUnknownRequestIsRejected()
    {
        var context = new TestContext();

        var result = await context.Service.ImportAsync(
            Guid.NewGuid(), Guid.NewGuid(), Content("epub bytes"), "book.epub", CancellationToken.None);

        Assert.AreEqual(ManualImportOutcome.Invalid, result.Outcome);
    }

    [TestMethod]
    public async Task ADisallowedExtensionIsRejectedBeforeStaging()
    {
        var context = new TestContext();
        var (request, format) = context.SeedRequest(RequestMediaType.Ebook);

        var result = await context.Service.ImportAsync(
            request.Id, format.Id, Content("not really an exe"), "book.exe", CancellationToken.None);

        Assert.AreEqual(ManualImportOutcome.Invalid, result.Outcome);
        Assert.AreEqual(0, context.StagingStore.WriteCount);
    }

    [TestMethod]
    public async Task AContentMismatchIsRejectedAfterStaging()
    {
        var context = new TestContext();
        context.StagingStore.NextDetectedMimeType = "text/plain";
        var (request, format) = context.SeedRequest(RequestMediaType.Ebook);

        var result = await context.Service.ImportAsync(
            request.Id, format.Id, Content("not actually an epub"), "book.epub", CancellationToken.None);

        Assert.AreEqual(ManualImportOutcome.Invalid, result.Outcome);
        // The mismatch is only detectable once the real bytes are sniffed.
        Assert.AreEqual(1, context.StagingStore.WriteCount);
        Assert.AreEqual(1, context.StagingStore.DeleteCount);
        Assert.AreEqual(0, context.Repository.Assets.Count);
    }

    [TestMethod]
    public async Task ACleanImportStagesAJobAndAsset()
    {
        var context = new TestContext();
        var (request, format) = context.SeedRequest(RequestMediaType.Ebook);

        var result = await context.Service.ImportAsync(
            request.Id, format.Id, Content("epub bytes"), "book.epub", CancellationToken.None);

        Assert.AreEqual(ManualImportOutcome.Success, result.Outcome);
        Assert.HasCount(1, context.Repository.Jobs);
        Assert.HasCount(1, context.Repository.Assets);

        var asset = context.Repository.Assets[0];
        Assert.AreEqual(MediaAssetStorageState.Quarantine, asset.StorageState);
        Assert.AreEqual(request.WorkId, asset.WorkId);
        Assert.AreEqual(format.Id, asset.AssociatedRequestFormatId);

        var job = context.Repository.Jobs[0];
        Assert.AreEqual(AcquisitionJobStatus.CandidateAcquired, job.Status);
        Assert.HasCount(1, job.Candidates);
        Assert.AreEqual(AcquisitionCandidateStatus.Acquired, job.Candidates.Single().Status);
    }

    [TestMethod]
    public async Task ADuplicateChecksumForTheSameFormatIsDetected()
    {
        var context = new TestContext();
        var (request, format) = context.SeedRequest(RequestMediaType.Ebook);
        context.Repository.ExistingChecksums.Add((format.Id, context.StagingStore.NextSha256));

        var result = await context.Service.ImportAsync(
            request.Id, format.Id, Content("epub bytes"), "book.epub", CancellationToken.None);

        Assert.AreEqual(ManualImportOutcome.DuplicateDetected, result.Outcome);
        Assert.HasCount(0, context.Repository.Assets);
        Assert.AreEqual(1, context.StagingStore.DeleteCount);
    }

    [TestMethod]
    public async Task EveryStagedImportWritesAnAuditEvent()
    {
        var context = new TestContext();
        var (request, format) = context.SeedRequest(RequestMediaType.Ebook);

        await context.Service.ImportAsync(
            request.Id, format.Id, Content("epub bytes"), "book.epub", CancellationToken.None);

        Assert.HasCount(1, context.Audit.Entries);
        Assert.AreEqual("manual_import.staged", context.Audit.Entries[0].Action);
    }

    private static MemoryStream Content(string text) => new(Encoding.UTF8.GetBytes(text));

    private sealed class TestContext
    {
        public TestContext()
        {
            Repository = new FakeAcquisitionRepository();
            RequestRepository = new FakeRequestRepository();
            StagingStore = new FakeStagingStore();
            Audit = new RecordingAuditWriter();

            Staging = new AcquisitionStagingService(
                Repository,
                StagingStore,
                new AlwaysHealthyBoundaryGuard(),
                new ManualImportPolicy(),
                Audit,
                new FixedClock());

            Service = new ManualImportService(RequestRepository, Staging);
        }

        public FakeAcquisitionRepository Repository { get; }

        public FakeRequestRepository RequestRepository { get; }

        public FakeStagingStore StagingStore { get; }

        public RecordingAuditWriter Audit { get; }

        public AcquisitionStagingService Staging { get; }

        public ManualImportService Service { get; }

        public (BookRequest Request, RequestFormat Format) SeedRequest(RequestMediaType mediaType)
        {
            var request = new BookRequest(Guid.NewGuid(), Guid.NewGuid(), [mediaType], null, Now);
            RequestRepository.Requests[request.Id] = request;
            return (request, request.Formats.Single());
        }
    }

    private sealed class FakeRequestRepository : IRequestRepository
    {
        public Dictionary<Guid, BookRequest> Requests { get; } = [];

        public Task<bool> WorkExistsAsync(Guid workId, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task<IReadOnlyList<BookRequest>> GetActiveRequestsForWorkAsync(
            Guid userId, Guid workId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

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
            Task.FromResult(Requests.GetValueOrDefault(requestId));

        public Task<AdminBookRequestView?> FindAdminViewAsync(
            Guid requestId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void AddRequest(BookRequest request) => throw new NotSupportedException();

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<TResult> InCreateRequestScopeAsync<TResult>(
            Guid userId,
            Guid workId,
            Func<CancellationToken, Task<TResult>> operation,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeAcquisitionRepository : IAcquisitionRepository
    {
        public List<AcquisitionJob> Jobs { get; } = [];

        public List<MediaAsset> Assets { get; } = [];

        public HashSet<(Guid FormatId, string Sha256)> ExistingChecksums { get; } = [];

        public Task<bool> ExistsAssetWithChecksumForFormatAsync(
            Guid requestFormatId, string sha256, CancellationToken cancellationToken) =>
            Task.FromResult(ExistingChecksums.Contains((requestFormatId, sha256)));

        public Task<IReadOnlyList<MediaAssetAdminView>> ListActiveAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void AddJob(AcquisitionJob job) => Jobs.Add(job);

        public void AddAsset(MediaAsset asset) => Assets.Add(asset);

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeStagingStore : IAssetStagingStore
    {
        public int WriteCount { get; private set; }

        public int DeleteCount { get; private set; }

        public string NextSha256 { get; } = new string('a', 64);

        public string NextDetectedMimeType { get; set; } = "application/epub+zip";

        public Task<StagedFile> WriteToQuarantineAsync(
            Stream content, string originalFilename, long maxSizeBytes, CancellationToken cancellationToken)
        {
            WriteCount++;
            return Task.FromResult(new StagedFile(
                $"{Guid.NewGuid():N}{Path.GetExtension(originalFilename)}",
                SizeBytes: 1024,
                NextSha256,
                NextDetectedMimeType));
        }

        public Task<Stream> OpenAsync(
            MediaAssetStorageState zone, string storedFilename, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task MoveAsync(
            MediaAssetStorageState fromZone,
            MediaAssetStorageState toZone,
            string storedFilename,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteAsync(
            MediaAssetStorageState zone,
            string storedFilename,
            CancellationToken cancellationToken)
        {
            Assert.AreEqual(MediaAssetStorageState.Quarantine, zone);
            DeleteCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class AlwaysHealthyBoundaryGuard : IAcquisitionBoundaryGuard
    {
        public Task<bool> CanAcceptNewArtifactAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class RecordingAuditWriter : IAuditWriter
    {
        public List<(string Action, string SubjectType, string? SubjectId, object? Detail)> Entries { get; } = [];

        public Task WriteAsync(
            string action, string subjectType, string? subjectId, object? detail, CancellationToken cancellationToken)
        {
            Entries.Add((action, subjectType, subjectId, detail));
            return Task.CompletedTask;
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }
}
