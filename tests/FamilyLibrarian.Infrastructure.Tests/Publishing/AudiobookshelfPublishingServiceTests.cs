using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Notifications;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Notifications;
using FamilyLibrarian.Domain.Publishing;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Domain.Security;

namespace FamilyLibrarian.Infrastructure.Tests.Publishing;

[TestClass]
public sealed class AudiobookshelfPublishingServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task PublishDoesNothingWhenAudiobookshelfIsNotConfigured()
    {
        var context = new TestContext();
        var asset = context.CreateAsset();

        await context.Service.PublishAsync(asset, CancellationToken.None);

        Assert.IsNull(await context.Repository.FindByAssetIdAsync(asset.Id, CancellationToken.None));
    }

    [TestMethod]
    public async Task ASuccessfulUploadWithAReturnedIdIsMarkedDeliveredImmediately()
    {
        var context = ConfiguredContext();
        var asset = context.CreateAsset();
        context.ApiClient.ExistingItemId = null;
        context.ApiClient.UploadResult = new AudiobookshelfUploadResult(true, "li_new", null);

        await context.Service.PublishAsync(asset, CancellationToken.None);

        var delivery = await context.Repository.FindByAssetIdAsync(asset.Id, CancellationToken.None);
        Assert.IsNotNull(delivery);
        Assert.AreEqual(DeliveryStatus.Delivered, delivery.Status);
        Assert.AreEqual("li_new", delivery.ExternalItemId);
        Assert.AreEqual(1, context.ApiClient.UploadCount);
    }

    [TestMethod]
    public async Task AnExistingMatchingItemShortCircuitsTheUpload()
    {
        var context = ConfiguredContext();
        var asset = context.CreateAsset();
        context.ApiClient.ExistingItemId = "li_already-there";

        await context.Service.PublishAsync(asset, CancellationToken.None);

        var delivery = await context.Repository.FindByAssetIdAsync(asset.Id, CancellationToken.None);
        Assert.IsNotNull(delivery);
        Assert.AreEqual(DeliveryStatus.Delivered, delivery.Status);
        Assert.AreEqual("li_already-there", delivery.ExternalItemId);
        Assert.AreEqual(0, context.ApiClient.UploadCount);
    }

    [TestMethod]
    public async Task AFailedUploadIsRecordedAsFailedRatherThanThrowing()
    {
        var context = ConfiguredContext();
        var asset = context.CreateAsset();
        context.ApiClient.ExistingItemId = null;
        context.ApiClient.UploadResult = new AudiobookshelfUploadResult(false, null, "Upload rejected.");

        await context.Service.PublishAsync(asset, CancellationToken.None);

        var delivery = await context.Repository.FindByAssetIdAsync(asset.Id, CancellationToken.None);
        Assert.IsNotNull(delivery);
        Assert.AreEqual(DeliveryStatus.Failed, delivery.Status);
        Assert.AreEqual("Upload rejected.", delivery.FailureReason);
    }

    [TestMethod]
    public async Task RecheckRetriesTheWholeUploadWhenPreviouslyFailed()
    {
        var context = ConfiguredContext();
        var asset = context.CreateAsset();
        context.ApiClient.ExistingItemId = null;
        context.ApiClient.UploadResult = new AudiobookshelfUploadResult(false, null, "boom");
        await context.Service.PublishAsync(asset, CancellationToken.None);
        var delivery = await context.Repository.FindByAssetIdAsync(asset.Id, CancellationToken.None);
        Assert.AreEqual(DeliveryStatus.Failed, delivery!.Status);

        context.ApiClient.UploadResult = new AudiobookshelfUploadResult(true, "li_retry", null);
        var handled = await context.Service.RecheckAsync(delivery.Id, CancellationToken.None);

        Assert.IsTrue(handled);
        var reloaded = await context.Repository.FindAsync(delivery.Id, CancellationToken.None);
        Assert.AreEqual(DeliveryStatus.Delivered, reloaded!.Status);
        Assert.AreEqual("li_retry", reloaded.ExternalItemId);
    }

    [TestMethod]
    public async Task RecheckOnAnUnknownDeliveryReturnsFalse()
    {
        var context = ConfiguredContext();

        var handled = await context.Service.RecheckAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.IsFalse(handled);
    }

    private static TestContext ConfiguredContext()
    {
        var context = new TestContext();
        context.Settings.SetSettings("https://abs.example", "lib1", "folder1", null, Now);
        context.Settings.SetEnabled(true, null, Now);
        context.SettingsStore.Exists = true;
        return context;
    }

    private sealed class TestContext
    {
        public TestContext()
        {
            SettingsStore = new FakeAudiobookshelfSettingsStore(Settings);
            Repository = new FakeDeliveryRepository();
            Assets = new FakeAssetLookup();
            StagingStore = new FakeStagingStore();
            ApiClient = new FakeApiClient();
            WorkLookup = new FakeWorkLookup();
            RequestFulfillment = new FakeRequestFulfillmentStore();
            Audit = new RecordingAuditWriter();
            NotificationRepository = new RecordingNotificationRepository();

            Service = new AudiobookshelfPublishingService(
                SettingsStore, Repository, Assets, StagingStore, ApiClient, WorkLookup, RequestFulfillment,
                Audit, new FixedClock(),
                new NotificationService(NotificationRepository, new StubCurrentUser(), new FixedClock()));
        }

        public AudiobookshelfSettings Settings { get; } = new(Now);

        public FakeAudiobookshelfSettingsStore SettingsStore { get; }

        public FakeDeliveryRepository Repository { get; }

        public FakeAssetLookup Assets { get; }

        public FakeStagingStore StagingStore { get; }

        public FakeApiClient ApiClient { get; }

        public FakeWorkLookup WorkLookup { get; }

        public FakeRequestFulfillmentStore RequestFulfillment { get; }

        public RecordingAuditWriter Audit { get; }

        public RecordingNotificationRepository NotificationRepository { get; }

        public AudiobookshelfPublishingService Service { get; }

        public MediaAsset CreateAsset()
        {
            var asset = new MediaAsset(
                Guid.NewGuid(),
                editionId: null,
                RequestMediaType.Audiobook,
                ".m4b",
                "My Audiobook.m4b",
                $"{Guid.NewGuid():N}.m4b",
                sizeBytes: 4096,
                sha256: new string('b', 64),
                detectedMimeType: "audio/mp4",
                associatedRequestFormatId: Guid.NewGuid(),
                sourceAcquisitionCandidateId: null,
                Now);
            Assets.Assets[asset.Id] = asset;
            return asset;
        }
    }

    private sealed class FakeAudiobookshelfSettingsStore(AudiobookshelfSettings settings)
        : IAudiobookshelfSettingsStore
    {
        public bool Exists { get; set; }

        public Task<AudiobookshelfSettings?> FindAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Exists ? settings : null);

        public Task<AudiobookshelfSettings> GetOrCreateAsync(CancellationToken cancellationToken) =>
            Task.FromResult(settings);

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeDeliveryRepository : IDeliveryRepository
    {
        private readonly Dictionary<Guid, Delivery> _byId = [];

        public Task<Delivery?> FindAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(_byId.GetValueOrDefault(id));

        public Task<Delivery?> FindByAssetIdAsync(Guid assetId, CancellationToken cancellationToken) =>
            Task.FromResult(_byId.Values.FirstOrDefault(delivery => delivery.AssetId == assetId));

        public Task<Delivery?> FindByBundleIdAsync(Guid bundleId, CancellationToken cancellationToken) =>
            Task.FromResult(_byId.Values.FirstOrDefault(delivery => delivery.BundleId == bundleId));

        public Task<IReadOnlyList<DeliveryView>> ListRecentAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void Add(Delivery delivery) => _byId[delivery.Id] = delivery;

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeAssetLookup : ISecurityEvaluationRepository
    {
        public Dictionary<Guid, MediaAsset> Assets { get; } = [];

        public Task<MediaAsset?> FindAssetAsync(Guid assetId, CancellationToken cancellationToken) =>
            Task.FromResult(Assets.GetValueOrDefault(assetId));

        public Task<IReadOnlyList<MediaAsset>> FindAssetsByBundleIdAsync(Guid bundleId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MediaAsset>>(
                Assets.Values
                    .Where(asset => asset.BundleId == bundleId)
                    .OrderBy(asset => asset.BundleSequence)
                    .ToArray());

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

    private sealed class FakeApiClient : IAudiobookshelfApiClient
    {
        public string? ExistingItemId { get; set; }

        public AudiobookshelfUploadResult UploadResult { get; set; } = new(true, "li_default", null);

        public int UploadCount { get; private set; }

        public int BundleUploadCount { get; private set; }

        public int LastBundleTrackCount { get; private set; }

        public Task<string?> FindExistingItemIdAsync(
            string title, string? author, CancellationToken cancellationToken) =>
            Task.FromResult(ExistingItemId);

        public Task<AudiobookshelfUploadResult> UploadAsync(
            Stream content, string filename, string title, string? author, CancellationToken cancellationToken)
        {
            UploadCount++;
            return Task.FromResult(UploadResult);
        }

        public Task<AudiobookshelfUploadResult> UploadBundleAsync(
            IReadOnlyList<(Stream Content, string Filename)> tracks,
            string title,
            string? author,
            CancellationToken cancellationToken)
        {
            BundleUploadCount++;
            LastBundleTrackCount = tracks.Count;
            return Task.FromResult(UploadResult);
        }
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

    private sealed class FakeRequestFulfillmentStore : IBookRequestFulfillmentStore
    {
        public Dictionary<Guid, BookRequest> Requests { get; } = [];

        public Task<BookRequest?> FindByFormatIdAsync(Guid requestFormatId, CancellationToken cancellationToken) =>
            Task.FromResult(Requests.GetValueOrDefault(requestFormatId));
    }

    private sealed class StubCurrentUser : ICurrentUser
    {
        public Guid? UserId => null;

        public string? DisplayName => null;
    }

    /// <summary>Records what would have been written, without a real store behind it.</summary>
    private sealed class RecordingNotificationRepository : INotificationRepository
    {
        public List<NotificationEvent> Added { get; } = [];

        public Task<NotificationEvent?> FindLatestAsync(
            NotificationAudience audience,
            Guid? recipientUserId,
            string category,
            string? subjectType,
            string? subjectId,
            CancellationToken cancellationToken) =>
            Task.FromResult<NotificationEvent?>(null);

        public Task AddAsync(NotificationEvent notification, CancellationToken cancellationToken)
        {
            Added.Add(notification);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<NotificationReceipt>> ListReceiptsAsync(
            Guid notificationEventId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<NotificationReceipt>>([]);

        public Task RemoveReceiptsAsync(IReadOnlyList<NotificationReceipt> receipts, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<(NotificationEvent Event, NotificationReceipt? Receipt)>> ListForViewerAsync(
            Guid userId, bool isAdmin, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<(NotificationEvent Event, NotificationReceipt? Receipt)>>([]);

        public Task<NotificationReceipt?> FindReceiptAsync(
            Guid notificationEventId, Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult<NotificationReceipt?>(null);

        public Task AddReceiptAsync(NotificationReceipt receipt, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
