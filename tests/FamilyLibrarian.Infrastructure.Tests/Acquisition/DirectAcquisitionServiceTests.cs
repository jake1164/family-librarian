using System.Text;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Providers;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Infrastructure.Tests.Acquisition;

[TestClass]
public sealed class DirectAcquisitionServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task AnUnknownRequestIsRejected()
    {
        var context = new TestContext();

        var result = await context.Service.AcquireAsync(
            Guid.NewGuid(), Guid.NewGuid(), "gutendex", "1234", CancellationToken.None);

        Assert.AreEqual(ManualImportOutcome.Invalid, result.Outcome);
    }

    [TestMethod]
    public async Task AnUnknownProviderIsRejected()
    {
        var context = new TestContext();
        var (request, format) = context.SeedRequest(RequestMediaType.Ebook);

        var result = await context.Service.AcquireAsync(
            request.Id, format.Id, "not-a-real-provider", "1234", CancellationToken.None);

        Assert.AreEqual(ManualImportOutcome.Invalid, result.Outcome);
    }

    [TestMethod]
    public async Task AStaleProviderResultIdIsRejected()
    {
        var context = new TestContext();
        var (request, format) = context.SeedRequest(RequestMediaType.Ebook);
        context.Provider.Matches = false;

        var result = await context.Service.AcquireAsync(
            request.Id, format.Id, "gutendex", "1234", CancellationToken.None);

        Assert.AreEqual(ManualImportOutcome.Invalid, result.Outcome);
        Assert.AreEqual(0, context.StagingStore.WriteCount);
    }

    [TestMethod]
    public async Task ASuccessfulFetchStagesAJobWithTheProviderIdAndCandidateMetadata()
    {
        var context = new TestContext();
        var (request, format) = context.SeedRequest(RequestMediaType.Ebook);

        var result = await context.Service.AcquireAsync(
            request.Id, format.Id, "gutendex", "1234", CancellationToken.None);

        Assert.AreEqual(ManualImportOutcome.Success, result.Outcome);
        var job = context.Repository.Jobs.Single();
        Assert.AreEqual("gutendex", job.ProviderId);
        var candidate = job.Candidates.Single();
        Assert.AreEqual("The Hobbit", candidate.Title);
        Assert.AreEqual("J. R. R. Tolkien", candidate.Author);
    }

    [TestMethod]
    public async Task ADuplicateChecksumIsDetectedThroughTheSharedStagingPath()
    {
        var context = new TestContext();
        var (request, format) = context.SeedRequest(RequestMediaType.Ebook);
        context.Repository.ExistingChecksums.Add((format.Id, context.StagingStore.NextSha256));

        var result = await context.Service.AcquireAsync(
            request.Id, format.Id, "gutendex", "1234", CancellationToken.None);

        Assert.AreEqual(ManualImportOutcome.DuplicateDetected, result.Outcome);
        Assert.AreEqual(0, context.Repository.Assets.Count);
        Assert.AreEqual(1, context.StagingStore.DeleteCount);
    }

    private sealed class TestContext
    {
        public TestContext()
        {
            Repository = new FakeAcquisitionRepository();
            RequestRepository = new FakeRequestRepository();
            StagingStore = new FakeStagingStore();
            Audit = new RecordingAuditWriter();
            Provider = new FakeDirectAcquisitionProvider();
            WorkLookup = new FakeWorkLookup();

            var staging = new AcquisitionStagingService(
                Repository,
                StagingStore,
                new AlwaysHealthyBoundaryGuard(),
                new ManualImportPolicy(),
                Audit,
                new FixedClock());

            // No external providers are registered in these tests — only the
            // DI-registered (Gutendex-style) provider path is under test here;
            // see ExternalProviderClientTests/ExternalProviderEndpointTests for
            // the external-provider path.
            Service = new DirectAcquisitionService(
                RequestRepository,
                [Provider],
                new NoExternalProviders(),
                new UnusedExternalProviderClient(),
                new PrivateEgressRouteResolver(new AlwaysDisabledGatewayCache()),
                new NoOpCredentialProtector(),
                WorkLookup,
                staging);
        }

        public FakeAcquisitionRepository Repository { get; }

        public FakeRequestRepository RequestRepository { get; }

        public FakeStagingStore StagingStore { get; }

        public RecordingAuditWriter Audit { get; }

        public FakeDirectAcquisitionProvider Provider { get; }

        public FakeWorkLookup WorkLookup { get; }

        public DirectAcquisitionService Service { get; }

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

        public Task<IReadOnlyList<MediaAssetAdminView>> ListRecentAsync(
            int maximumCount,
            CancellationToken cancellationToken) =>
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

    private sealed class FakeDirectAcquisitionProvider : IDirectAcquisitionProvider
    {
        public bool Matches { get; set; } = true;

        public string Id => "gutendex";

        public Task<IReadOnlyList<FulfillmentOption>> FindDirectAcquisitionsAsync(
            Guid workId, RequestMediaType mediaType, CancellationToken cancellationToken)
        {
            if (!Matches)
            {
                return Task.FromResult<IReadOnlyList<FulfillmentOption>>([]);
            }

            IReadOnlyList<FulfillmentOption> options =
            [
                new FulfillmentOption(
                    ProviderId: Id,
                    ProviderResultId: "1234",
                    WorkId: workId,
                    EditionId: null,
                    MediaType: mediaType,
                    OptionKind: OptionKind.DirectAcquisition,
                    AcquisitionMethod: AcquisitionMethod.DirectDownload,
                    Format: "epub",
                    Language: null,
                    Quality: null,
                    Availability: null,
                    Cost: 0m,
                    Currency: null,
                    LicenseOrUsageStatus: "Public domain",
                    DrmStatus: null,
                    ExternalActionUri: null,
                    ProviderData: "https://example.test/book.epub")
            ];
            return Task.FromResult(options);
        }

        public Task<IReadOnlyList<DirectAcquisitionFile>> FetchAsync(
            FulfillmentOption fulfillmentOption, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DirectAcquisitionFile>>(
            [
                new DirectAcquisitionFile(new MemoryStream(Encoding.UTF8.GetBytes("epub bytes")), "book.epub")
            ]);
    }

    private sealed class FakeWorkLookup : IWorkLookup
    {
        public Task<WorkSummary?> FindAsync(Guid workId, CancellationToken cancellationToken) =>
            Task.FromResult<WorkSummary?>(new WorkSummary(workId, "The Hobbit", "J. R. R. Tolkien", []));
    }

    private sealed class NoExternalProviders : IExternalProviderStore
    {
        public Task<IReadOnlyList<ExternalProvider>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExternalProvider>>([]);

        public Task<IReadOnlyList<ExternalProvider>> ListEnabledAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExternalProvider>>([]);

        public Task<ExternalProvider?> FindAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<ExternalProvider?>(null);

        public Task<ExternalProvider?> FindByProviderIdAsync(string providerId, CancellationToken cancellationToken) =>
            Task.FromResult<ExternalProvider?>(null);

        public void Add(ExternalProvider provider) => throw new NotSupportedException();

        public void Remove(ExternalProvider provider) => throw new NotSupportedException();

        public Task SaveChangesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class UnusedExternalProviderClient : IExternalProviderClient
    {
        public Task<ExternalProviderManifest> GetManifestAsync(
            string baseUrl, string? apiKey, EgressRoute route, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> GetHealthAsync(
            string baseUrl, string? apiKey, EgressRoute route, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ExternalProviderCandidate>> SearchAsync(
            string baseUrl, string? apiKey, ExternalProviderSearchRequest request, EgressRoute route,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ExternalProviderArtifact> AcquireAsync(
            string baseUrl, string? apiKey, string candidateReference, RequestMediaType mediaType, EgressRoute route,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class AlwaysDisabledGatewayCache : IPrivateEgressGatewayRuntimeCache
    {
        public PrivateEgressGatewayRuntimeState Current => PrivateEgressGatewayRuntimeState.Disabled;

        public void Refresh(PrivateEgressGatewayRuntimeState state) => throw new NotSupportedException();
    }

    private sealed class NoOpCredentialProtector : ICredentialProtector
    {
        public int FormatVersion => 1;

        public string Protect(string providerId, string plaintext) => plaintext;

        public string? Unprotect(string providerId, string protectedValue, int formatVersion) => protectedValue;
    }
}
