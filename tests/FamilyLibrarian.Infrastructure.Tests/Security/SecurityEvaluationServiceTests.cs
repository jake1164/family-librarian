using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Audit;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Domain.Security;

namespace FamilyLibrarian.Infrastructure.Tests.Security;

/// <summary>
/// F3: a scanner failure used to strand a <see cref="MediaAsset"/> in
/// <see cref="MediaAssetStorageState.Processing"/> forever — the database
/// transition was never persisted before the failure, so a retry's own
/// <c>MoveAsync</c> call found the file already gone from quarantine and threw
/// <see cref="FileNotFoundException"/> on every subsequent attempt. These tests
/// exercise the fixed sequencing directly against <see cref="SecurityEvaluationService"/>,
/// independent of
/// <see cref="FamilyLibrarian.Infrastructure.Tests.Acquisition.FileSystemAssetStagingStoreTests"/>'s
/// coverage of the underlying idempotent move.
/// </summary>
[TestClass]
public sealed class SecurityEvaluationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task ACleanScanPersistsTheProcessingTransitionBeforeScanningAndEndsPassed()
    {
        var context = new TestContext();
        var asset = context.SeedAsset();

        var result = await context.CreateService(new AlwaysCleanScanner())
            .EvaluateAsync(asset.Id, CancellationToken.None);

        Assert.AreEqual(SecurityEvaluationOutcome.Success, result.Outcome);
        Assert.AreEqual(SecurityEvaluationStatus.Passed, result.Status);
        Assert.AreEqual(MediaAssetStorageState.Processing, asset.StorageState);
        Assert.AreEqual(MediaAssetStorageState.Processing, context.StagingStore.ZoneOf(asset.StoredFilename));

        // Persist the pending evaluation before scanning, each scan result,
        // then the final policy outcome.
        Assert.AreEqual(3, context.Repository.SaveCount);
    }

    [TestMethod]
    public async Task AScannerFailureRecoversTheAssetToQuarantineAndRethrows()
    {
        var context = new TestContext();
        var asset = context.SeedAsset();

        await Assert.ThrowsExactlyAsync<IOException>(
            () => context.CreateService(new ThrowingScanner())
                .EvaluateAsync(asset.Id, CancellationToken.None));

        Assert.AreEqual(MediaAssetStorageState.Quarantine, asset.StorageState);
        Assert.AreEqual(MediaAssetStorageState.Quarantine, context.StagingStore.ZoneOf(asset.StoredFilename));
        StringAssert.Contains(asset.ScanFailureReason, "Simulated connection reset mid-stream.");

        // Persisted Processing, then persisted the recovery back to Quarantine.
        Assert.AreEqual(2, context.Repository.SaveCount);

        // Retain evidence that an attempt started, without claiming it passed.
        var interrupted = context.Repository.AddedEvaluations.Single();
        Assert.AreEqual(SecurityEvaluationStatus.Pending, interrupted.Status);
        Assert.IsNull(interrupted.CompletedAtUtc);

        var auditEntry = context.Audit.Entries.Single();
        Assert.AreEqual(AuditActions.AssetEvaluationFailed, auditEntry.Action);
        Assert.AreEqual(asset.Id.ToString(), auditEntry.SubjectId);
    }

    [TestMethod]
    public async Task ACancelledRequestStillRecoversTheAssetToQuarantine()
    {
        var context = new TestContext();
        var asset = context.SeedAsset();
        using var cancellation = new CancellationTokenSource();

        // The request is aborted mid-scan; recovery must not be cancelled with it.
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => context.CreateService(new CancellingScanner(cancellation))
                .EvaluateAsync(asset.Id, cancellation.Token));

        Assert.AreEqual(MediaAssetStorageState.Quarantine, asset.StorageState);
        Assert.AreEqual(MediaAssetStorageState.Quarantine, context.StagingStore.ZoneOf(asset.StoredFilename));
        StringAssert.Contains(asset.ScanFailureReason, "cancelled");
    }

    [TestMethod]
    public async Task AnUnexpectedScannerExceptionRecoversTheAssetAndRecordsTheReason()
    {
        var context = new TestContext();
        var asset = context.SeedAsset();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => context.CreateService(new FaultingScanner())
                .EvaluateAsync(asset.Id, CancellationToken.None));

        Assert.AreEqual(MediaAssetStorageState.Quarantine, asset.StorageState);
        StringAssert.Contains(asset.ScanFailureReason, "scanner exploded");
    }

    [TestMethod]
    public async Task AScanLimitErrorHoldsTheFileForReviewInsteadOfDestroyingIt()
    {
        var context = new TestContext();
        var asset = context.SeedAsset();

        var result = await context.CreateService(new LimitExceededScanner())
            .EvaluateAsync(asset.Id, CancellationToken.None);

        Assert.AreEqual(SecurityEvaluationStatus.ReviewRequired, result.Status);
        Assert.AreEqual(MediaAssetStorageState.Processing, asset.StorageState);
        Assert.IsTrue(context.StagingStore.Contains(asset.StoredFilename));
    }

    [TestMethod]
    public async Task AnAbandonedPendingEvaluationInProcessingCanBeRetried()
    {
        var context = new TestContext();
        var asset = context.SeedAbandonedProcessingAsset();
        context.Clock.UtcNow = Now.AddMinutes(16);

        var result = await context.CreateService(new AlwaysCleanScanner())
            .EvaluateAsync(asset.Id, CancellationToken.None);

        Assert.AreEqual(SecurityEvaluationOutcome.Success, result.Outcome);
        Assert.AreEqual(SecurityEvaluationStatus.Passed, result.Status);
        CollectionAssert.Contains(
            context.Audit.Entries.Select(entry => entry.Action).ToArray(), AuditActions.AssetEvaluationFailed);
    }

    [TestMethod]
    public async Task AReviewRequiredAssetCanBeRescannedAndPassesOnceTheScannerCompletes()
    {
        var context = new TestContext();
        var asset = context.SeedReviewRequiredProcessingAsset();

        var result = await context.CreateService(new AlwaysCleanScanner())
            .EvaluateAsync(asset.Id, CancellationToken.None);

        Assert.AreEqual(SecurityEvaluationOutcome.Success, result.Outcome);
        Assert.AreEqual(SecurityEvaluationStatus.Passed, result.Status);
        Assert.AreEqual(2, context.Repository.AddedEvaluations.Count);
        CollectionAssert.Contains(
            context.Audit.Entries.Select(entry => entry.Action).ToArray(), AuditActions.AssetRescanRequested);
    }

    [TestMethod]
    public async Task ARecentPendingEvaluationInProcessingIsLeftAlone()
    {
        var context = new TestContext();
        var asset = context.SeedAbandonedProcessingAsset();
        context.Clock.UtcNow = Now.AddMinutes(2);

        var result = await context.CreateService(new AlwaysCleanScanner())
            .EvaluateAsync(asset.Id, CancellationToken.None);

        Assert.AreEqual(SecurityEvaluationOutcome.Invalid, result.Outcome);
        Assert.AreEqual(MediaAssetStorageState.Processing, asset.StorageState);
        Assert.AreEqual(1, context.Repository.AddedEvaluations.Count);
    }

    [TestMethod]
    public async Task ARetryAfterARecoveredFailureSucceedsInsteadOfFailingForever()
    {
        var context = new TestContext();
        var asset = context.SeedAsset();

        // First attempt: the scanner drops the connection mid-stream, exactly
        // as a real ClamAV disconnect does (see ClamAvMalwareScannerTests).
        await Assert.ThrowsExactlyAsync<IOException>(
            () => context.CreateService(new ThrowingScanner())
                .EvaluateAsync(asset.Id, CancellationToken.None));

        // Second attempt: a fresh service instance, as a new request would
        // get — same underlying repository/staging state, healthy scanner
        // this time. Confirmed by reverting the fix and running this test: the
        // asset was never usable again (a real deployment sees this as
        // MoveAsync's FileNotFoundException once the database and filesystem
        // have genuinely diverged across requests; this in-memory fake shares
        // the entity by reference, so here it surfaces one step earlier, as
        // the guard clause rejecting a Processing asset as not evaluable — the
        // observable defect is the same: permanently stuck, not transiently
        // stuck).
        var result = await context.CreateService(new AlwaysCleanScanner())
            .EvaluateAsync(asset.Id, CancellationToken.None);

        Assert.AreEqual(SecurityEvaluationOutcome.Success, result.Outcome);
        Assert.AreEqual(SecurityEvaluationStatus.Passed, result.Status);
        Assert.AreEqual(MediaAssetStorageState.Processing, asset.StorageState);
    }

    [TestMethod]
    public async Task ADetectedThreatIsDestroyedAfterItsFailedEvaluationIsRecorded()
    {
        var context = new TestContext();
        var asset = context.SeedAsset();

        var result = await context.CreateService(new DetectedThreatScanner())
            .EvaluateAsync(asset.Id, CancellationToken.None);

        Assert.AreEqual(SecurityEvaluationStatus.Failed, result.Status);
        Assert.AreEqual(MediaAssetStorageState.Destroyed, asset.StorageState);
        Assert.IsFalse(context.StagingStore.Contains(asset.StoredFilename));
        Assert.AreEqual(4, context.Repository.SaveCount);
        CollectionAssert.AreEquivalent(
            new[] { AuditActions.AssetEvaluated, AuditActions.AssetMalwareDestroyed },
            context.Audit.Entries.Select(entry => entry.Action).ToArray());
    }

    private sealed class TestContext
    {
        public TestContext()
        {
            Repository = new FakeSecurityEvaluationRepository();
            StagingStore = new FakeAssetStagingStore();
            Audit = new RecordingAuditWriter();
            Clock = new FixedClock();
        }

        public FixedClock Clock { get; }

        public FakeSecurityEvaluationRepository Repository { get; }

        public FakeAssetStagingStore StagingStore { get; }

        public RecordingAuditWriter Audit { get; }

        public MediaAsset SeedAsset()
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
                Guid.NewGuid(),
                sourceAcquisitionCandidateId: null,
                Now);

            Repository.Assets[asset.Id] = asset;
            StagingStore.Seed(asset.StoredFilename, MediaAssetStorageState.Quarantine);
            return asset;
        }

        /// <summary>An asset a scan started on and then never finished (host restart, aborted request).</summary>
        public MediaAsset SeedAbandonedProcessingAsset()
        {
            var asset = SeedAsset();
            StagingStore.Seed(asset.StoredFilename, MediaAssetStorageState.Processing);
            asset.TransitionStorageState(MediaAssetStorageState.Processing, Now);
            Repository.AddEvaluation(new SecurityEvaluation(asset.Id, "v1", Now));
            return asset;
        }

        /// <summary>An asset whose scanner could not finish (e.g. a clamd limit hit), waiting on an administrator.</summary>
        public MediaAsset SeedReviewRequiredProcessingAsset()
        {
            var asset = SeedAsset();
            StagingStore.Seed(asset.StoredFilename, MediaAssetStorageState.Processing);
            asset.TransitionStorageState(MediaAssetStorageState.Processing, Now);
            var evaluation = new SecurityEvaluation(asset.Id, "v1", Now);
            evaluation.RecordScanResult(
                "clamav", true, ScanResultStatus.Error, "Not fully scanned: Heuristics.Limits.Exceeded.MaxScanTime", null, Now);
            evaluation.Evaluate(Now);
            Repository.AddEvaluation(evaluation);
            return asset;
        }

        public SecurityEvaluationService CreateService(IMalwareScanner scanner) =>
            new(Repository, StagingStore, [scanner], [], Audit, Clock);
    }

    private sealed class FakeSecurityEvaluationRepository : ISecurityEvaluationRepository
    {
        public Dictionary<Guid, MediaAsset> Assets { get; } = [];

        public List<SecurityEvaluation> AddedEvaluations { get; } = [];

        public int SaveCount { get; private set; }

        public Task<MediaAsset?> FindAssetAsync(Guid assetId, CancellationToken cancellationToken) =>
            Task.FromResult(Assets.GetValueOrDefault(assetId));

        public Task<IReadOnlyList<MediaAsset>> FindAssetsByBundleIdAsync(Guid bundleId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SecurityEvaluation?> FindLatestEvaluationAsync(Guid assetId, CancellationToken cancellationToken) =>
            Task.FromResult(AddedEvaluations.LastOrDefault(evaluation => evaluation.AssetId == assetId));

        public void AddEvaluation(SecurityEvaluation evaluation) => AddedEvaluations.Add(evaluation);

        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            // Like EF Core, a cancelled token fails the save.
            cancellationToken.ThrowIfCancellationRequested();
            SaveCount++;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Tracks which zone each stored file is actually in, and throws if a move
    /// is attempted from a zone that doesn't hold the file — the same failure
    /// mode <see cref="File.Move(string, string)"/> produces, which is exactly
    /// what stranded the asset before F3 was fixed.
    /// </summary>
    private sealed class FakeAssetStagingStore : IAssetStagingStore
    {
        private readonly Dictionary<string, MediaAssetStorageState> _zones = [];

        public void Seed(string storedFilename, MediaAssetStorageState zone) => _zones[storedFilename] = zone;

        public MediaAssetStorageState ZoneOf(string storedFilename) => _zones[storedFilename];

        public bool Contains(string storedFilename) => _zones.ContainsKey(storedFilename);

        public Task<StagedFile> WriteToQuarantineAsync(
            Stream content, string originalFilename, long maxSizeBytes, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Stream> OpenAsync(
            MediaAssetStorageState zone, string storedFilename, CancellationToken cancellationToken)
        {
            if (_zones.GetValueOrDefault(storedFilename) != zone)
            {
                throw new InvalidOperationException(
                    $"'{storedFilename}' is not in {zone}; it is in {_zones.GetValueOrDefault(storedFilename)}.");
            }

            return Task.FromResult<Stream>(new MemoryStream("epub bytes"u8.ToArray()));
        }

        public Task MoveAsync(
            MediaAssetStorageState fromZone,
            MediaAssetStorageState toZone,
            string storedFilename,
            CancellationToken cancellationToken)
        {
            if (_zones.GetValueOrDefault(storedFilename) != fromZone)
            {
                throw new FileNotFoundException(
                    $"'{storedFilename}' is not in {fromZone}; it is in {_zones.GetValueOrDefault(storedFilename)}.");
            }

            _zones[storedFilename] = toZone;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(
            MediaAssetStorageState zone,
            string storedFilename,
            CancellationToken cancellationToken)
        {
            if (_zones.GetValueOrDefault(storedFilename) != zone)
            {
                throw new FileNotFoundException(
                    $"'{storedFilename}' is not in {zone}; it is in {_zones.GetValueOrDefault(storedFilename)}.");
            }

            _zones.Remove(storedFilename);
            return Task.CompletedTask;
        }
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
        public DateTimeOffset UtcNow { get; set; } = Now;
    }

    private sealed class AlwaysCleanScanner : IMalwareScanner
    {
        public string Id => "clean";

        public bool IsRequired => true;

        public Task<ScannerHealth> CheckHealthAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ScannerHealth(true, "1.0", null));

        public Task<ScanOutcome> ScanAsync(Stream content, CancellationToken cancellationToken) =>
            Task.FromResult(new ScanOutcome(ScanResultStatus.Clean, null));
    }

    /// <summary>Simulates the dropped-connection behavior proven real in <c>ClamAvMalwareScannerTests</c>.</summary>
    private sealed class ThrowingScanner : IMalwareScanner
    {
        public string Id => "throwing";

        public bool IsRequired => true;

        public Task<ScannerHealth> CheckHealthAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ScannerHealth(true, "1.0", null));

        public Task<ScanOutcome> ScanAsync(Stream content, CancellationToken cancellationToken) =>
            throw new IOException("Simulated connection reset mid-stream.");
    }

    private sealed class CancellingScanner(CancellationTokenSource cancellation) : IMalwareScanner
    {
        public string Id => "cancelling";

        public bool IsRequired => true;

        public Task<ScannerHealth> CheckHealthAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ScannerHealth(true, "1.0", null));

        public Task<ScanOutcome> ScanAsync(Stream content, CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ScanOutcome(ScanResultStatus.Clean, null));
        }
    }

    private sealed class FaultingScanner : IMalwareScanner
    {
        public string Id => "faulting";

        public bool IsRequired => true;

        public Task<ScannerHealth> CheckHealthAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ScannerHealth(true, "1.0", null));

        public Task<ScanOutcome> ScanAsync(Stream content, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("scanner exploded");
    }

    private sealed class LimitExceededScanner : IMalwareScanner
    {
        public string Id => "limit";

        public bool IsRequired => true;

        public Task<ScannerHealth> CheckHealthAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ScannerHealth(true, "1.0", null));

        public Task<ScanOutcome> ScanAsync(Stream content, CancellationToken cancellationToken) =>
            Task.FromResult(new ScanOutcome(
                ScanResultStatus.Error, "Not fully scanned: Heuristics.Limits.Exceeded.MaxScanSize"));
    }

    private sealed class DetectedThreatScanner : IMalwareScanner
    {
        public string Id => "detected";

        public bool IsRequired => true;

        public Task<ScannerHealth> CheckHealthAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ScannerHealth(true, "1.0", null));

        public Task<ScanOutcome> ScanAsync(Stream content, CancellationToken cancellationToken) =>
            Task.FromResult(new ScanOutcome(ScanResultStatus.Detected, "Test threat"));
    }
}
