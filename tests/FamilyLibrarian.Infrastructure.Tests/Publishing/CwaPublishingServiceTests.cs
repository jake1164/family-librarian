using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Publishing;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Domain.Security;

namespace FamilyLibrarian.Infrastructure.Tests.Publishing;

[TestClass]
public sealed class CwaPublishingServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task PublishDoesNothingWhenCwaIsNotConfigured()
    {
        var context = new TestContext();
        var asset = context.CreateAsset();

        await context.Service.PublishAsync(asset, CancellationToken.None);

        Assert.IsNull(await context.Repository.FindByAssetIdAsync(asset.Id, CancellationToken.None));
    }

    [TestMethod]
    public async Task PublishDoesNothingWhenCwaIsDisabled()
    {
        var context = new TestContext();
        context.Settings.SetSettings(
            CwaTransportMode.Local, "/ingest", null, null, null, null, null, null, null, Now);
        context.Settings.SetEnabled(false, null, Now);
        context.SettingsStore.Exists = true;
        var asset = context.CreateAsset();

        await context.Service.PublishAsync(asset, CancellationToken.None);

        Assert.IsNull(await context.Repository.FindByAssetIdAsync(asset.Id, CancellationToken.None));
    }

    [TestMethod]
    public async Task ASuccessfulPublishIsMarkedAvailableWhenTheCatalogFindsItImmediately()
    {
        var context = context_Configured();
        var asset = context.CreateAsset();
        context.CatalogClient.NextBookId = "42";

        await context.Service.PublishAsync(asset, CancellationToken.None);

        var import = await context.Repository.FindByAssetIdAsync(asset.Id, CancellationToken.None);
        Assert.IsNotNull(import);
        Assert.AreEqual(LibraryImportStatus.Available, import.Status);
        Assert.AreEqual("42", import.ExternalBookId);
        Assert.AreEqual(1, context.Transport.WriteCount);
    }

    [TestMethod]
    public async Task APublishNotYetFoundInTheCatalogStaysAwaitingVerification()
    {
        var context = context_Configured();
        var asset = context.CreateAsset();
        context.CatalogClient.NextBookId = null;

        await context.Service.PublishAsync(asset, CancellationToken.None);

        var import = await context.Repository.FindByAssetIdAsync(asset.Id, CancellationToken.None);
        Assert.IsNotNull(import);
        Assert.AreEqual(LibraryImportStatus.AwaitingVerification, import.Status);
        Assert.IsNull(import.ExternalBookId);
    }

    [TestMethod]
    public async Task ATransportFailureIsRecordedAsFailedRatherThanThrowing()
    {
        var context = context_Configured();
        var asset = context.CreateAsset();
        context.Transport.ThrowOnWrite = true;

        await context.Service.PublishAsync(asset, CancellationToken.None);

        var import = await context.Repository.FindByAssetIdAsync(asset.Id, CancellationToken.None);
        Assert.IsNotNull(import);
        Assert.AreEqual(LibraryImportStatus.Failed, import.Status);
        Assert.IsNotNull(import.FailureReason);
        Assert.AreEqual(1, context.Audit.Entries.Count(entry => entry.Action == "asset.publish_failed"));
    }

    [TestMethod]
    public async Task RecheckRetriesTheWholeHandoffWhenPreviouslyFailed()
    {
        var context = context_Configured();
        var asset = context.CreateAsset();
        context.Transport.ThrowOnWrite = true;
        await context.Service.PublishAsync(asset, CancellationToken.None);
        var import = await context.Repository.FindByAssetIdAsync(asset.Id, CancellationToken.None);
        Assert.AreEqual(LibraryImportStatus.Failed, import!.Status);

        context.Transport.ThrowOnWrite = false;
        context.CatalogClient.NextBookId = "99";
        var handled = await context.Service.RecheckAsync(import.Id, CancellationToken.None);

        Assert.IsTrue(handled);
        var reloaded = await context.Repository.FindAsync(import.Id, CancellationToken.None);
        Assert.AreEqual(LibraryImportStatus.Available, reloaded!.Status);
        Assert.AreEqual("99", reloaded.ExternalBookId);
    }

    [TestMethod]
    public async Task RecheckJustReVerifiesWhenAwaitingVerification()
    {
        var context = context_Configured();
        var asset = context.CreateAsset();
        context.CatalogClient.NextBookId = null;
        await context.Service.PublishAsync(asset, CancellationToken.None);
        var import = await context.Repository.FindByAssetIdAsync(asset.Id, CancellationToken.None);
        Assert.AreEqual(LibraryImportStatus.AwaitingVerification, import!.Status);
        Assert.AreEqual(1, context.Transport.WriteCount);

        context.CatalogClient.NextBookId = "7";
        await context.Service.RecheckAsync(import.Id, CancellationToken.None);

        var reloaded = await context.Repository.FindAsync(import.Id, CancellationToken.None);
        Assert.AreEqual(LibraryImportStatus.Available, reloaded!.Status);
        // Re-verification never re-transports the file.
        Assert.AreEqual(1, context.Transport.WriteCount);
    }

    [TestMethod]
    public async Task RecheckOnAnUnknownImportReturnsFalse()
    {
        var context = context_Configured();

        var handled = await context.Service.RecheckAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.IsFalse(handled);
    }

    private static TestContext context_Configured()
    {
        var context = new TestContext();
        context.Settings.SetSettings(
            CwaTransportMode.Local, "/ingest", null, null, null, null, null, null, null, Now);
        context.Settings.SetEnabled(true, null, Now);
        context.SettingsStore.Exists = true;
        return context;
    }

    private sealed class TestContext
    {
        public TestContext()
        {
            SettingsStore = new FakeCwaSettingsStore(Settings);
            Repository = new FakeLibraryImportRepository();
            Assets = new FakeAssetLookup();
            StagingStore = new FakeStagingStore();
            TransportFactory = new FakeTransportFactory(Transport);
            CatalogClient = new FakeCatalogClient();
            WorkLookup = new FakeWorkLookup();
            Audit = new RecordingAuditWriter();

            Service = new CwaPublishingService(
                SettingsStore, Repository, Assets, StagingStore, TransportFactory, CatalogClient, WorkLookup,
                Audit, new FixedClock());
        }

        public CwaSettings Settings { get; } = new(Now);

        public FakeCwaSettingsStore SettingsStore { get; }

        public FakeLibraryImportRepository Repository { get; }

        public FakeAssetLookup Assets { get; }

        public FakeStagingStore StagingStore { get; }

        public FakeTransport Transport { get; } = new();

        public FakeTransportFactory TransportFactory { get; }

        public FakeCatalogClient CatalogClient { get; }

        public FakeWorkLookup WorkLookup { get; }

        public RecordingAuditWriter Audit { get; }

        public CwaPublishingService Service { get; }

        public MediaAsset CreateAsset()
        {
            var asset = new MediaAsset(
                Guid.NewGuid(),
                editionId: null,
                RequestMediaType.Ebook,
                ".epub",
                "My Book.epub",
                $"{Guid.NewGuid():N}.epub",
                sizeBytes: 1024,
                sha256: new string('a', 64),
                detectedMimeType: "application/epub+zip",
                associatedRequestFormatId: Guid.NewGuid(),
                sourceAcquisitionCandidateId: null,
                Now);
            Assets.Assets[asset.Id] = asset;
            return asset;
        }
    }

    private sealed class FakeCwaSettingsStore(CwaSettings settings) : ICwaSettingsStore
    {
        /// <summary>Mirrors "a row has been saved" — false until a test explicitly configures it, matching production's null-until-first-configure behavior.</summary>
        public bool Exists { get; set; }

        public Task<CwaSettings?> FindAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Exists ? settings : null);

        public Task<CwaSettings> GetOrCreateAsync(CancellationToken cancellationToken) => Task.FromResult(settings);

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeLibraryImportRepository : ILibraryImportRepository
    {
        private readonly Dictionary<Guid, LibraryImport> _byId = [];

        public Task<LibraryImport?> FindAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(_byId.GetValueOrDefault(id));

        public Task<LibraryImport?> FindByAssetIdAsync(Guid assetId, CancellationToken cancellationToken) =>
            Task.FromResult(_byId.Values.FirstOrDefault(import => import.AssetId == assetId));

        public Task<IReadOnlyList<LibraryImportView>> ListRecentAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void Add(LibraryImport import) => _byId[import.Id] = import;

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeAssetLookup : ISecurityEvaluationRepository
    {
        public Dictionary<Guid, MediaAsset> Assets { get; } = [];

        public Task<MediaAsset?> FindAssetAsync(Guid assetId, CancellationToken cancellationToken) =>
            Task.FromResult(Assets.GetValueOrDefault(assetId));

        public Task<SecurityEvaluation?> FindLatestEvaluationAsync(Guid assetId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void AddEvaluation(SecurityEvaluation evaluation) => throw new NotSupportedException();

        public Task SaveChangesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeStagingStore : IAssetStagingStore
    {
        public Task<StagedFile> WriteToQuarantineAsync(
            Stream content, string originalFilename, long maxSizeBytes, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Stream> OpenAsync(
            MediaAssetStorageState zone, string storedFilename, CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new MemoryStream([1, 2, 3]));

        public Task MoveAsync(
            MediaAssetStorageState fromZone,
            MediaAssetStorageState toZone,
            string storedFilename,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeTransport : ICwaIngestTransport
    {
        public int WriteCount { get; private set; }

        public bool ThrowOnWrite { get; set; }

        public Task WriteAsync(Stream content, string targetFilename, CancellationToken cancellationToken)
        {
            if (ThrowOnWrite)
            {
                throw new IOException("Simulated transport failure.");
            }

            WriteCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTransportFactory(FakeTransport transport) : ICwaIngestTransportFactory
    {
        public ICwaIngestTransport Create(CwaSettings settings) => transport;
    }

    private sealed class FakeCatalogClient : ICwaCatalogClient
    {
        public string? NextBookId { get; set; }

        public Task<string?> FindBookIdAsync(string title, string? author, CancellationToken cancellationToken) =>
            Task.FromResult(NextBookId);
    }

    private sealed class FakeWorkLookup : IWorkLookup
    {
        public Task<WorkSummary?> FindAsync(Guid workId, CancellationToken cancellationToken) =>
            Task.FromResult<WorkSummary?>(new WorkSummary(workId, "The Hobbit", "J. R. R. Tolkien"));
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
