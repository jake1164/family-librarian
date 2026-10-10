using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Domain.Security;

namespace FamilyLibrarian.Infrastructure.Tests.Security;

[TestClass]
public sealed class AssetIdentityVerificationServiceTests
{
    [TestMethod]
    public async Task ARetryReturnsAMatchingUnmatchedAssetToProcessing()
    {
        var asset = CreateUnmatchedAsset();
        var repository = new AssetRepository(asset);
        var staging = new RecordingStagingStore();
        var service = new AssetIdentityVerificationService(
            repository,
            staging,
            [new MatchingVerifier()],
            new RecordingAuditWriter(),
            new FixedClock());

        var result = await service.RetryUnmatchedAsync(asset.Id, CancellationToken.None);

        Assert.IsTrue(result.IsMatch);
        Assert.AreEqual(MediaAssetStorageState.Processing, asset.StorageState);
        Assert.AreEqual(
            (MediaAssetStorageState.Unmatched, MediaAssetStorageState.Processing),
            staging.Move);
        Assert.AreEqual(1, repository.SaveCount);
    }

    /// <summary>
    /// The live gap this guards against: a MOBI/AZW3 candidate from an
    /// unconfirmed (title/author-only, <c>confirmLowConfidenceMatch</c>)
    /// external-provider match has no format-specific verifier and used to
    /// pass through as "not-applicable = match" regardless. It must now be
    /// held, same as a confirmed mismatch, since nothing actually checked it.
    /// </summary>
    [TestMethod]
    public async Task AnUnconfirmedMatchWithNoFormatVerifierIsHeldNotWaved()
    {
        var asset = CreateProcessingAsset(format: ".mobi", identityPreConfirmed: false);
        var repository = new AssetRepository(asset);
        var staging = new RecordingStagingStore();
        var service = new AssetIdentityVerificationService(
            repository, staging, [], new RecordingAuditWriter(), new FixedClock());

        var result = await service.VerifyAsync(asset.Id, CancellationToken.None);

        Assert.IsFalse(result.IsMatch);
        Assert.AreEqual(MediaAssetStorageState.Unmatched, asset.StorageState);
        Assert.IsNotNull(asset.IdentityMismatchReason);
    }

    /// <summary>
    /// The existing, still-correct behavior for everything else: a
    /// pre-confirmed match (every built-in-provider acquisition, a manual
    /// upload, or an external candidate strong enough to need no override)
    /// has nothing left to prove, so a format with no verifier still passes.
    /// </summary>
    [TestMethod]
    public async Task APreConfirmedMatchWithNoFormatVerifierStillPasses()
    {
        var asset = CreateProcessingAsset(format: ".m4b", identityPreConfirmed: true);
        var repository = new AssetRepository(asset);
        var staging = new RecordingStagingStore();
        var service = new AssetIdentityVerificationService(
            repository, staging, [], new RecordingAuditWriter(), new FixedClock());

        var result = await service.VerifyAsync(asset.Id, CancellationToken.None);

        Assert.IsTrue(result.IsMatch);
        Assert.AreEqual(MediaAssetStorageState.Processing, asset.StorageState);
    }

    private static MediaAsset CreateProcessingAsset(string format, bool identityPreConfirmed)
    {
        var asset = new MediaAsset(
            Guid.NewGuid(), null, RequestMediaType.Ebook, format, $"Book{format}", $"asset{format}", 12,
            new string('a', 64), "application/octet-stream", Guid.NewGuid(), null, DateTimeOffset.UtcNow,
            identityPreConfirmed: identityPreConfirmed);
        asset.TransitionStorageState(MediaAssetStorageState.Processing, DateTimeOffset.UtcNow);
        return asset;
    }

    private static MediaAsset CreateUnmatchedAsset()
    {
        var asset = new MediaAsset(
            Guid.NewGuid(), null, RequestMediaType.Ebook, ".epub", "Book.epub", "asset.epub", 12,
            new string('a', 64), "application/epub+zip", Guid.NewGuid(), null, DateTimeOffset.UtcNow);
        asset.TransitionStorageState(MediaAssetStorageState.Processing, DateTimeOffset.UtcNow);
        asset.TransitionStorageState(MediaAssetStorageState.Unmatched, DateTimeOffset.UtcNow);
        return asset;
    }

    private sealed class AssetRepository(MediaAsset asset) : ISecurityEvaluationRepository
    {
        public int SaveCount { get; private set; }

        public Task<MediaAsset?> FindAssetAsync(Guid assetId, CancellationToken cancellationToken) =>
            Task.FromResult(asset.Id == assetId ? asset : null);

        public Task<IReadOnlyList<MediaAsset>> FindAssetsByBundleIdAsync(Guid bundleId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SecurityEvaluation?> FindLatestEvaluationAsync(Guid assetId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void AddEvaluation(SecurityEvaluation evaluation) => throw new NotSupportedException();

        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            SaveCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingStagingStore : IAssetStagingStore
    {
        public (MediaAssetStorageState From, MediaAssetStorageState To)? Move { get; private set; }

        public Task<StagedFile> WriteToQuarantineAsync(
            Stream content, string originalFilename, long maxSizeBytes, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Stream> OpenAsync(
            MediaAssetStorageState zone, string storedFilename, CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new MemoryStream());

        public Task MoveAsync(
            MediaAssetStorageState fromZone,
            MediaAssetStorageState toZone,
            string storedFilename,
            CancellationToken cancellationToken)
        {
            Move = (fromZone, toZone);
            return Task.CompletedTask;
        }
    }

    private sealed class MatchingVerifier : IAssetIdentityVerifier
    {
        public string Id => "test";

        public bool Supports(MediaAsset asset) => true;

        public Task<AssetIdentityVerificationResult> VerifyAsync(
            MediaAsset asset,
            Stream content,
            CancellationToken cancellationToken) =>
            Task.FromResult(AssetIdentityVerificationResult.Match(Id));
    }

    private sealed class RecordingAuditWriter : IAuditWriter
    {
        public Task WriteAsync(
            string action,
            string subjectType,
            string? subjectId,
            object? detail,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 8, 17, 20, 0, 0, TimeSpan.Zero);
    }
}
