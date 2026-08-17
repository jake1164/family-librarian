using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Audit;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Domain.Security;

namespace FamilyLibrarian.Infrastructure.Tests.Security;

[TestClass]
public sealed class AssetDiscardServiceTests
{
    [TestMethod]
    public async Task ARejectedFileCanBeDeletedWhileItsAssetRecordRemains()
    {
        var asset = CreateRejectedAsset();
        var repository = new AssetRepository(asset);
        var staging = new RecordingStagingStore(asset.StoredFilename, MediaAssetStorageState.Rejected);
        var audit = new RecordingAuditWriter();
        var service = new AssetDiscardService(
            repository, staging, audit, new CurrentUser(), new FixedClock());

        var result = await service.DiscardAsync(asset.Id, CancellationToken.None);

        Assert.AreEqual(AssetDiscardOutcome.Success, result.Outcome);
        Assert.AreEqual(MediaAssetStorageState.Destroyed, asset.StorageState);
        Assert.IsFalse(staging.ContainsFile);
        Assert.AreEqual(1, repository.SaveCount);
        Assert.AreEqual(AuditActions.AssetDestroyed, audit.Action);
    }

    [TestMethod]
    public async Task AProcessingFileCannotBeDeletedBeforeAReviewDecision()
    {
        var asset = CreateProcessingAsset();
        var repository = new AssetRepository(asset);
        var staging = new RecordingStagingStore(asset.StoredFilename, MediaAssetStorageState.Processing);
        var service = new AssetDiscardService(
            repository, staging, new RecordingAuditWriter(), new CurrentUser(), new FixedClock());

        var result = await service.DiscardAsync(asset.Id, CancellationToken.None);

        Assert.AreEqual(AssetDiscardOutcome.Invalid, result.Outcome);
        Assert.AreEqual(MediaAssetStorageState.Processing, asset.StorageState);
        Assert.IsTrue(staging.ContainsFile);
    }

    private static MediaAsset CreateRejectedAsset()
    {
        var asset = CreateProcessingAsset();
        asset.TransitionStorageState(MediaAssetStorageState.Rejected, DateTimeOffset.UtcNow);
        return asset;
    }

    private static MediaAsset CreateProcessingAsset()
    {
        var asset = new MediaAsset(
            Guid.NewGuid(), null, RequestMediaType.Ebook, ".epub", "Book.epub", "asset.epub", 12,
            new string('a', 64), "application/epub+zip", Guid.NewGuid(), null, DateTimeOffset.UtcNow);
        asset.TransitionStorageState(MediaAssetStorageState.Processing, DateTimeOffset.UtcNow);
        return asset;
    }

    private sealed class AssetRepository(MediaAsset asset) : ISecurityEvaluationRepository
    {
        public int SaveCount { get; private set; }

        public Task<MediaAsset?> FindAssetAsync(Guid assetId, CancellationToken cancellationToken) =>
            Task.FromResult(asset.Id == assetId ? asset : null);

        public Task<SecurityEvaluation?> FindLatestEvaluationAsync(Guid assetId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void AddEvaluation(SecurityEvaluation evaluation) => throw new NotSupportedException();

        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            SaveCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingStagingStore(string filename, MediaAssetStorageState state) : IAssetStagingStore
    {
        public bool ContainsFile { get; private set; } = true;

        public Task<StagedFile> WriteToQuarantineAsync(
            Stream content, string originalFilename, long maxSizeBytes, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

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
            Assert.AreEqual(state, zone);
            Assert.AreEqual(filename, storedFilename);
            ContainsFile = false;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingAuditWriter : IAuditWriter
    {
        public string? Action { get; private set; }

        public Task WriteAsync(
            string action, string subjectType, string? subjectId, object? detail, CancellationToken cancellationToken)
        {
            Action = action;
            return Task.CompletedTask;
        }
    }

    private sealed class CurrentUser : ICurrentUser
    {
        public Guid? UserId { get; } = Guid.NewGuid();

        public string? DisplayName => "Test admin";
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 8, 17, 20, 0, 0, TimeSpan.Zero);
    }
}
