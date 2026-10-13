using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Audit;
using FamilyLibrarian.Domain.Evidence;
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

    [TestMethod]
    public async Task RecordingEvidenceNeverChangesTheSecurityDecisionAndRunsAfterItIsSaved()
    {
        foreach (var scanner in new IMalwareScanner[] { new AlwaysCleanScanner(), new DetectedThreatScanner(), new LimitExceededScanner() })
        {
            var plain = new TestContext();
            var plainAsset = plain.SeedAsset();
            var plainResult = await plain.CreateService(scanner).EvaluateAsync(plainAsset.Id, CancellationToken.None);

            var observed = new TestContext();
            var observedAsset = observed.SeedAsset();
            var recorder = new CapturingRecorder(observed.Repository);
            var observedResult = await observed.CreateService(scanner, recorder).EvaluateAsync(observedAsset.Id, CancellationToken.None);

            Assert.AreEqual(plainResult.Status, observedResult.Status, scanner.GetType().Name);
            Assert.AreEqual(plainAsset.StorageState, observedAsset.StorageState, scanner.GetType().Name);
            Assert.AreEqual(plain.Repository.SaveCount, observed.Repository.SaveCount, "Recording must add no save of its own to the decision path.");
            CollectionAssert.AreEqual(
                plain.Audit.Entries.Select(entry => entry.Action).ToArray(),
                observed.Audit.Entries.Select(entry => entry.Action).ToArray());
            Assert.AreEqual(1, recorder.Calls);
            Assert.AreEqual(observedResult.Status, recorder.StatusWhenCalled, "Evidence is recorded after the decision exists.");
            // Pending evaluation, the scan result and the final outcome were all saved before recording
            // (a destroyed malware file adds one more save afterwards).
            Assert.IsGreaterThanOrEqualTo(3, recorder.SavesWhenCalled, "Evidence is recorded after the decision is saved.");
        }
    }

    private sealed class FixedInspector(params IdentityFact[] facts) : IAssetTechnicalInspector
    {
        public string Id => "fixed";

        public bool Supports(MediaAsset asset) => true;

        public Task<IReadOnlyList<IdentityFact>> InspectAsync(MediaAsset asset, Stream content, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IdentityFact>>(facts);
    }

    private sealed class ThrowingInspector : IAssetTechnicalInspector
    {
        public string Id => "throwing";

        public bool Supports(MediaAsset asset) => true;

        public Task<IReadOnlyList<IdentityFact>> InspectAsync(MediaAsset asset, Stream content, CancellationToken cancellationToken) =>
            throw new InvalidDataException("hostile header");
    }

    private sealed class FixedProfileInspector(bool throws = false) : IAssetProfileInspector
    {
        public string Id => "fixed-profile";

        public bool Supports(MediaAsset asset) => true;

        public Task<IReadOnlyList<IdentityFact>> InspectAsync(MediaAsset asset, Stream content, CancellationToken cancellationToken) =>
            throws
                ? throw new InvalidDataException("hostile header")
                : Task.FromResult<IReadOnlyList<IdentityFact>>(
                    [new IdentityFact("ev-profile-duration", EvidenceDimension.Profile, EvidenceReasonCodes.ArtifactProfile.AudioTotalDuration,
                        EvidencePolarity.Unknown, "seconds=60;tracks=1")]);
    }

    [TestMethod]
    public async Task AnAudiobooksProfileIsReportedWithItsMeasurementsAndChangesNothing()
    {
        var plain = new TestContext();
        var plainAsset = plain.SeedAudioTrack();
        var plainResult = await plain.CreateService(new AlwaysCleanScanner()).EvaluateAsync(plainAsset.Id, CancellationToken.None);

        var profiled = new TestContext();
        var asset = profiled.SeedAudioTrack();
        var recorder = new CapturingRecorder(profiled.Repository);
        var result = await profiled.CreateService(new AlwaysCleanScanner(), recorder, [new FixedInspector(Duration())], [new FixedProfileInspector()])
            .EvaluateAsync(asset.Id, CancellationToken.None);

        Assert.AreEqual(plainResult.Status, result.Status);
        Assert.AreEqual(plain.Repository.SaveCount, profiled.Repository.SaveCount);
        var findings = recorder.Technical.Single().Findings;
        Assert.IsTrue(findings.Any(finding => finding.Code == EvidenceReasonCodes.ArtifactAudio.DurationMeasured));
        Assert.IsTrue(findings.Any(finding => finding.Dimension == EvidenceDimension.Profile));
    }

    [TestMethod]
    public async Task AProfileInspectorThatThrowsStillLeavesTheMeasurementsRecorded()
    {
        var context = new TestContext();
        var asset = context.SeedAudioTrack();
        var recorder = new CapturingRecorder(context.Repository);

        var result = await context.CreateService(new AlwaysCleanScanner(), recorder, [new FixedInspector(Duration())], [new FixedProfileInspector(throws: true)])
            .EvaluateAsync(asset.Id, CancellationToken.None);

        Assert.AreEqual(SecurityEvaluationStatus.Passed, result.Status);
        var findings = recorder.Technical.Single().Findings;
        Assert.AreEqual(EvidenceReasonCodes.ArtifactAudio.DurationMeasured, findings.Single().Code);
    }

    private static IdentityFact Duration() => new(
        "ev-audio-duration", EvidenceDimension.AudioTechnical, EvidenceReasonCodes.ArtifactAudio.DurationMeasured,
        EvidencePolarity.Unknown, "seconds=60;basis=media_header");

    [TestMethod]
    public async Task AnAudiobookIsMeasuredAfterTheDecisionIsSavedAndTheMeasurementChangesNothing()
    {
        var plain = new TestContext();
        var plainAsset = plain.SeedAudioTrack();
        var plainResult = await plain.CreateService(new AlwaysCleanScanner()).EvaluateAsync(plainAsset.Id, CancellationToken.None);

        var measured = new TestContext();
        var asset = measured.SeedAudioTrack();
        var recorder = new CapturingRecorder(measured.Repository);
        var result = await measured.CreateService(new AlwaysCleanScanner(), recorder, [new FixedInspector(Duration())])
            .EvaluateAsync(asset.Id, CancellationToken.None);

        Assert.AreEqual(plainResult.Status, result.Status);
        Assert.AreEqual(plainAsset.StorageState, asset.StorageState);
        Assert.AreEqual(plain.Repository.SaveCount, measured.Repository.SaveCount, "Measuring adds no save to the decision path.");
        var technical = recorder.Technical.Single();
        Assert.AreEqual(SecurityEvaluationStatus.Passed, technical.StatusAtTime, "Recorded after the decision exists.");
        Assert.AreEqual("inspector:fixed", technical.Source);
        Assert.AreEqual(EvidenceReasonCodes.ArtifactAudio.DurationMeasured, technical.Findings.Single().Code);
    }

    [TestMethod]
    public async Task AnAudiobookWithNoInspectorForItsFormatRecordsNotAssessedNeverAGuess()
    {
        var context = new TestContext();
        var asset = context.SeedAudioTrack();
        var recorder = new CapturingRecorder(context.Repository);

        await context.CreateService(new AlwaysCleanScanner(), recorder).EvaluateAsync(asset.Id, CancellationToken.None);

        var finding = recorder.Technical.Single().Findings.Single();
        Assert.AreEqual(EvidenceReasonCodes.ArtifactAudio.NotAssessed, finding.Code);
        Assert.AreEqual(EvidencePolarity.Unknown, finding.Polarity);
    }

    [TestMethod]
    public async Task AnInspectorThatThrowsOnAHostileFileLeavesTheDecisionAloneAndRecordsUnavailable()
    {
        var context = new TestContext();
        var asset = context.SeedAudioTrack();
        var recorder = new CapturingRecorder(context.Repository);

        var result = await context.CreateService(new AlwaysCleanScanner(), recorder, [new ThrowingInspector()])
            .EvaluateAsync(asset.Id, CancellationToken.None);

        Assert.AreEqual(SecurityEvaluationStatus.Passed, result.Status);
        Assert.AreEqual(EvidenceReasonCodes.ArtifactAudio.DurationUnavailable, recorder.Technical.Single().Findings.Single().Code);
    }

    [TestMethod]
    public async Task EbooksAndFailedEvaluationsAreNeverMeasured()
    {
        var ebook = new TestContext();
        var ebookAsset = ebook.SeedAsset();
        var ebookRecorder = new CapturingRecorder(ebook.Repository);
        await ebook.CreateService(new AlwaysCleanScanner(), ebookRecorder, [new FixedInspector(Duration())])
            .EvaluateAsync(ebookAsset.Id, CancellationToken.None);
        Assert.IsEmpty(ebookRecorder.Technical, "Only audiobooks are measured.");

        var infected = new TestContext();
        var infectedAsset = infected.SeedAudioTrack();
        var infectedRecorder = new CapturingRecorder(infected.Repository);
        await infected.CreateService(new DetectedThreatScanner(), infectedRecorder, [new FixedInspector(Duration())])
            .EvaluateAsync(infectedAsset.Id, CancellationToken.None);
        Assert.IsEmpty(infectedRecorder.Technical, "A failed evaluation's bytes are not inspected.");
    }

    [TestMethod]
    public async Task ATrackOfANumberedSetRecordsItsPositionAndAnyGapOrRepeat()
    {
        var context = new TestContext();
        var bundle = Guid.NewGuid();
        context.SeedAudioTrack(bundle, 1, 4);
        var second = context.SeedAudioTrack(bundle, 2, 4);
        context.SeedAudioTrack(bundle, 2, 4);
        var recorder = new CapturingRecorder(context.Repository);

        await context.CreateService(new AlwaysCleanScanner(), recorder, [new FixedInspector(Duration())])
            .EvaluateAsync(second.Id, CancellationToken.None);

        var findings = recorder.Technical.Single().Findings;
        Assert.AreEqual("2/4", findings.Single(finding => finding.Code == EvidenceReasonCodes.ArtifactAudio.TrackPosition).Value);
        Assert.AreEqual("missing=3,4", findings.Single(finding => finding.Code == EvidenceReasonCodes.ArtifactAudio.TrackSetGap).Value);
        Assert.AreEqual("repeated=2", findings.Single(finding => finding.Code == EvidenceReasonCodes.ArtifactAudio.TrackSetDuplicate).Value);
        Assert.IsTrue(findings.All(finding => finding.Dimension == EvidenceDimension.AudioTechnical));
    }

    [TestMethod]
    public async Task AFailingSetLookupStillRecordsTheMeasurements()
    {
        var context = new TestContext();
        var asset = context.SeedAudioTrack(Guid.NewGuid(), 1, 2);
        context.Repository.BundleLookupFails = true;
        var recorder = new CapturingRecorder(context.Repository);

        await context.CreateService(new AlwaysCleanScanner(), recorder, [new FixedInspector(Duration())])
            .EvaluateAsync(asset.Id, CancellationToken.None);

        Assert.AreEqual(EvidenceReasonCodes.ArtifactAudio.DurationMeasured, recorder.Technical.Single().Findings.Single().Code);
    }

    private sealed class CapturingRecorder(FakeSecurityEvaluationRepository repository) : IArtifactEvidenceRecorder
    {
        public int Calls { get; private set; }

        public SecurityEvaluationStatus? StatusWhenCalled { get; private set; }

        public int SavesWhenCalled { get; private set; }

        public Task RecordSecurityEvaluationAsync(
            MediaAsset asset, SecurityEvaluation evaluation, IReadOnlyDictionary<string, string?> toolVersions,
            CancellationToken cancellationToken)
        {
            Calls++;
            StatusWhenCalled = evaluation.Status;
            SavesWhenCalled = repository.SaveCount;
            return Task.CompletedTask;
        }

        public Task RecordIdentityVerificationAsync(
            MediaAsset asset, AssetIdentityVerificationResult result, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RecordIdentityOverrideAsync(
            MediaAsset asset, Guid? administratorUserId, string? overriddenHoldCode, string reason, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public List<(IReadOnlyList<IdentityFact> Findings, string Source, SecurityEvaluationStatus? StatusAtTime)> Technical { get; } = [];

        public Task RecordTechnicalInspectionAsync(
            MediaAsset asset, IReadOnlyList<IdentityFact> findings, string source, CancellationToken cancellationToken)
        {
            Technical.Add((findings, source, StatusWhenCalled));
            return Task.CompletedTask;
        }
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

        public SecurityEvaluationService CreateService(
            IMalwareScanner scanner, IArtifactEvidenceRecorder? evidence = null, IAssetTechnicalInspector[]? inspectors = null,
            IAssetProfileInspector[]? profileInspectors = null) =>
            new(Repository, StagingStore, [scanner], [], Audit, Clock, evidence, inspectors, profileInspectors);

        public MediaAsset SeedAudioTrack(Guid? bundleId = null, int? sequence = null, int? total = null, string format = ".m4b")
        {
            var asset = new MediaAsset(
                Guid.NewGuid(), editionId: null, RequestMediaType.Audiobook, format, $"book{format}", $"{Guid.NewGuid():N}{format}",
                sizeBytes: 1024, sha256: new string('b', 64), detectedMimeType: "audio/mp4", Guid.NewGuid(),
                sourceAcquisitionCandidateId: null, Now, bundleId, sequence, total);
            Repository.Assets[asset.Id] = asset;
            StagingStore.Seed(asset.StoredFilename, MediaAssetStorageState.Quarantine);
            return asset;
        }
    }

    private sealed class FakeSecurityEvaluationRepository : ISecurityEvaluationRepository
    {
        public Dictionary<Guid, MediaAsset> Assets { get; } = [];

        public List<SecurityEvaluation> AddedEvaluations { get; } = [];

        public int SaveCount { get; private set; }

        public Task<MediaAsset?> FindAssetAsync(Guid assetId, CancellationToken cancellationToken) =>
            Task.FromResult(Assets.GetValueOrDefault(assetId));

        public bool BundleLookupFails { get; set; }

        public Task<IReadOnlyList<MediaAsset>> FindAssetsByBundleIdAsync(Guid bundleId, CancellationToken cancellationToken) =>
            BundleLookupFails
                ? throw new IOException("lookup failed")
                : Task.FromResult<IReadOnlyList<MediaAsset>>(Assets.Values.Where(asset => asset.BundleId == bundleId).ToList());

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
