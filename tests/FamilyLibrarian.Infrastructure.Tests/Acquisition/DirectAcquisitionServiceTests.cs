using System.Text;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Matching;
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
    public async Task AnExternalProviderTitleAuthorMatchRequiresConfirmationBeforeFetching()
    {
        var context = new TestContext();
        var (request, format) = context.SeedRequest(RequestMediaType.Ebook);
        var provider = new ExternalProvider("custom-source", "Custom Source", "https://example.test", Now);
        provider.SetEnabled(true, null, Now);
        context.ExternalProviderStore.Add(provider);
        context.ExternalProviderClient.Candidates =
        [
            new ExternalProviderCandidate("ref-1", "The Hobbit", "J. R. R. Tolkien", "epub", 500_000, null)
        ];

        var result = await context.Service.AcquireAsync(
            request.Id, format.Id, "custom-source", "ref-1", CancellationToken.None);

        Assert.AreEqual(ManualImportOutcome.LowConfidenceMatchConfirmationRequired, result.Outcome);
        Assert.AreEqual(0, context.StagingStore.WriteCount);

        var confirmed = await context.Service.AcquireAsync(
            request.Id, format.Id, "custom-source", "ref-1", CancellationToken.None, confirmLowConfidenceMatch: true);

        // Protocol v2: the external-provider path now submits a durable job
        // rather than blocking on the fetch — see DirectAcquisitionService.
        Assert.AreEqual(ManualImportOutcome.AcquisitionInProgress, confirmed.Outcome);
        Assert.AreEqual(1, context.ProviderAcquisitionJobs.Jobs.Count);
    }

    [TestMethod]
    public async Task AnExternalProviderIsbnCorroboratedMatchProceedsWithoutConfirmation()
    {
        var context = new TestContext();
        context.WorkLookup.Isbn13s = ["9780618260300"];
        var (request, format) = context.SeedRequest(RequestMediaType.Ebook);
        var provider = new ExternalProvider("custom-source", "Custom Source", "https://example.test", Now);
        provider.SetEnabled(true, null, Now);
        context.ExternalProviderStore.Add(provider);
        context.ExternalProviderClient.Candidates =
        [
            new ExternalProviderCandidate("ref-1", "The Hobbit", "J. R. R. Tolkien", "epub", 500_000, null)
        ];

        var result = await context.Service.AcquireAsync(
            request.Id, format.Id, "custom-source", "ref-1", CancellationToken.None);

        Assert.AreEqual(ManualImportOutcome.AcquisitionInProgress, result.Outcome);
        Assert.AreEqual(1, context.ProviderAcquisitionJobs.Jobs.Count);
        Assert.AreEqual("ref-1", context.ProviderAcquisitionJobs.Jobs[0].CandidateReference);
    }

    [TestMethod]
    public async Task AnExternalProviderPlausibleButWrongTitleIsNeverFetchedWithoutConfirmation()
    {
        var context = new TestContext();
        var (request, format) = context.SeedRequest(RequestMediaType.Ebook);
        var provider = new ExternalProvider("custom-source", "Custom Source", "https://example.test", Now);
        provider.SetEnabled(true, null, Now);
        context.ExternalProviderStore.Add(provider);
        context.ExternalProviderClient.Candidates =
        [
            new ExternalProviderCandidate("ref-1", "Dim Sum of Fears", "Some Other Author", "epub", 500_000, null)
        ];

        var result = await context.Service.AcquireAsync(
            request.Id, format.Id, "custom-source", "ref-1", CancellationToken.None);

        Assert.AreEqual(ManualImportOutcome.LowConfidenceMatchConfirmationRequired, result.Outcome);
        Assert.AreEqual(0, context.StagingStore.WriteCount);
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
            ExternalProviderStore = new FakeExternalProviderStore();
            ExternalProviderClient = new FakeExternalProviderClient();
            ProviderAcquisitionJobs = new FakeProviderAcquisitionJobStore();

            var staging = new AcquisitionStagingService(
                Repository,
                StagingStore,
                new AlwaysHealthyBoundaryGuard(),
                new ManualImportPolicy(),
                Audit,
                new FixedClock());

            var checker = new ExternalCandidateAvailabilityChecker(
                ExternalProviderStore,
                ExternalProviderClient,
                new PrivateEgressRouteResolver(new AlwaysDisabledGatewayCache()),
                new ExternalProviderMatchVerifier(
                    new BookMatchService(new DeterministicBookMatcher(), new NoOpAmbiguityResolver()),
                    new DeterministicBookMatcher()),
                new NoOpCredentialProtector());

            // No external providers are registered by default — only the
            // DI-registered (Gutendex-style) provider path is under test in
            // the cases above; ExternalProviderStore/ExternalProviderClient
            // are populated per-test below for the external-provider gate
            // cases, and ExternalProviderClientTests/ExternalProviderEndpointTests
            // cover the wire client itself.
            Service = new DirectAcquisitionService(
                RequestRepository,
                [Provider],
                ExternalProviderStore,
                ExternalProviderClient,
                ProviderAcquisitionJobs,
                checker,
                new PrivateEgressRouteResolver(new AlwaysDisabledGatewayCache()),
                new NoOpCredentialProtector(),
                WorkLookup,
                staging,
                new FixedClock());
        }

        public FakeProviderAcquisitionJobStore ProviderAcquisitionJobs { get; }

        public FakeAcquisitionRepository Repository { get; }

        public FakeRequestRepository RequestRepository { get; }

        public FakeStagingStore StagingStore { get; }

        public RecordingAuditWriter Audit { get; }

        public FakeDirectAcquisitionProvider Provider { get; }

        public FakeWorkLookup WorkLookup { get; }

        public FakeExternalProviderStore ExternalProviderStore { get; }

        public FakeExternalProviderClient ExternalProviderClient { get; }

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

    private sealed class FakeProviderAcquisitionJobStore : IProviderAcquisitionJobStore
    {
        public List<ProviderAcquisitionJob> Jobs { get; } = [];

        public Task<ProviderAcquisitionJob?> FindAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(Jobs.FirstOrDefault(job => job.Id == id));

        public Task<ProviderAcquisitionJob?> FindByIdempotencyKeyAsync(
            Guid externalProviderId, string idempotencyKey, CancellationToken cancellationToken) =>
            Task.FromResult(Jobs.FirstOrDefault(
                job => job.ExternalProviderId == externalProviderId && job.IdempotencyKey == idempotencyKey));

        public Task<IReadOnlyList<ProviderAcquisitionJob>> ListDueForPollAsync(
            DateTimeOffset asOfUtc, int maximumCount, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProviderAcquisitionJob>>(
                Jobs.Where(job => job.NextPollAtUtc is not null && job.NextPollAtUtc <= asOfUtc).ToArray());

        public void Add(ProviderAcquisitionJob job) => Jobs.Add(job);

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
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

        public Task<IReadOnlyList<FulfillmentOption>> FindDirectAcquisitionsAsync(
            BookIdentity identity, RequestMediaType mediaType, CancellationToken cancellationToken) =>
            FindDirectAcquisitionsAsync(Guid.Empty, mediaType, cancellationToken);

        public Task<IReadOnlyList<DirectAcquisitionFile>> FetchAsync(
            FulfillmentOption fulfillmentOption, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DirectAcquisitionFile>>(
            [
                new DirectAcquisitionFile(new MemoryStream(Encoding.UTF8.GetBytes("epub bytes")), "book.epub")
            ]);
    }

    private sealed class FakeWorkLookup : IWorkLookup
    {
        public IReadOnlyList<string> Isbn13s { get; set; } = [];

        public Task<WorkSummary?> FindAsync(Guid workId, CancellationToken cancellationToken) =>
            Task.FromResult<WorkSummary?>(new WorkSummary(workId, "The Hobbit", "J. R. R. Tolkien", Isbn13s));
    }

    private sealed class FakeExternalProviderStore : IExternalProviderStore
    {
        public List<ExternalProvider> Providers { get; } = [];

        public Task<IReadOnlyList<ExternalProvider>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExternalProvider>>(Providers);

        public Task<IReadOnlyList<ExternalProvider>> ListEnabledAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExternalProvider>>(Providers.Where(provider => provider.IsEnabled).ToArray());

        public Task<ExternalProvider?> FindAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(Providers.FirstOrDefault(provider => provider.Id == id));

        public Task<ExternalProvider?> FindByProviderIdAsync(string providerId, CancellationToken cancellationToken) =>
            Task.FromResult(Providers.FirstOrDefault(provider => provider.ProviderId == providerId));

        public void Add(ExternalProvider provider) => Providers.Add(provider);

        public void Remove(ExternalProvider provider) => Providers.Remove(provider);

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeExternalProviderClient : IExternalProviderClient
    {
        public IReadOnlyList<ExternalProviderCandidate> Candidates { get; set; } = [];

        public Task<ExternalProviderManifest> GetManifestAsync(
            string baseUrl, string? apiKey, EgressRoute route, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ExternalProviderHealth> GetHealthAsync(
            string baseUrl, string? apiKey, EgressRoute route, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ExternalProviderCandidate>> SearchAsync(
            string baseUrl, string? apiKey, ExternalProviderSearchRequest request, EgressRoute route,
            CancellationToken cancellationToken) =>
            Task.FromResult(Candidates);

        public Task<ExternalProviderArtifact> AcquireAsync(
            string baseUrl, string? apiKey, string candidateReference, RequestMediaType mediaType, EgressRoute route,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ExternalProviderArtifact(new MemoryStream(Encoding.UTF8.GetBytes("epub bytes")), "book.epub"));

        public Task<ExternalProviderAcquireSubmission> SubmitAcquireAsync(
            string baseUrl, string? apiKey, ExternalAcquireRequest request, string idempotencyKey, EgressRoute route,
            CancellationToken cancellationToken) =>
            Task.FromResult(ExternalProviderAcquireSubmission.Accepted(
                "fake-job-1", ProviderAcquisitionJobLifecycleState.Queued, phase: null, pollAfterSeconds: null));

        public Task<ExternalProviderJobStatus> GetAcquireStatusAsync(
            string baseUrl, string? apiKey, string jobId, EgressRoute route, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ExternalProviderOutput>> ListOutputsAsync(
            string baseUrl, string? apiKey, string jobId, EgressRoute route, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ExternalProviderArtifact> GetOutputAsync(
            string baseUrl, string? apiKey, string jobId, string outputId, EgressRoute route,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task CancelAcquireAsync(
            string baseUrl, string? apiKey, string jobId, EgressRoute route, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteAcquireAsync(
            string baseUrl, string? apiKey, string jobId, EgressRoute route, CancellationToken cancellationToken) =>
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
