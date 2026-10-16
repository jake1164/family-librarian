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
    [DataRow(false)]
    [DataRow(true)]
    public async Task AbridgedConsentSuppressesOnlyTheAbridgedMarker(bool anotherHold)
    {
        var asset = CreateProcessingAsset(".m4b", true);
        var expected = ArtifactProfileComparerTests.Expected();
        var property = typeof(ExpectedArtifactProfile).GetProperty("AcceptedVariantKind");
        Assert.IsNotNull(property);
        property.SetValue(expected, Enum.Parse(Nullable.GetUnderlyingType(property.PropertyType)!, "AbridgedOnly"));
        var facts = new List<IdentityFact>
        {
            new("abridged", FamilyLibrarian.Domain.Evidence.EvidenceDimension.Profile, "profile.compare.abridged_marker", FamilyLibrarian.Domain.Evidence.EvidencePolarity.Condition)
        };
        if (anotherHold) facts.Add(new("series", FamilyLibrarian.Domain.Evidence.EvidenceDimension.Profile,
            "profile.compare.series_position_differs", FamilyLibrarian.Domain.Evidence.EvidencePolarity.Negative));
        var service = new AssetIdentityVerificationService(new AssetRepository(asset), new RecordingStagingStore(),
            [new FixedVerifier(AssetIdentityVerificationResult.Match("test", facts))], new RecordingAuditWriter(), new FixedClock(),
            expectedProfiles: new ProfileSource { Profile = expected });
        var result = await service.VerifyAsync(asset.Id, CancellationToken.None);
        Assert.AreEqual(!anotherHold, result.IsMatch);
        if (anotherHold) Assert.AreEqual("identity.hold.series_position_differs", result.HoldCode);
    }

    [TestMethod]
    [DataRow(320, false)]
    [DataRow(null, true)]
    public async Task ARealThreeChapterEpubIsComparedWithCatalogLengthBeforeTheHoldPolicy(int? pages, bool match)
    {
        var asset = CreateProcessingAsset(".epub", true);
        var work = new FamilyLibrarian.Application.Publishing.WorkSummary(asset.WorkId, "Iron Flame", "Rebecca Yarros", [], CatalogPageCount: pages);
        var lookup = new EpubCorroboratedAuthorTests.Lookup(work);
        var staging = new EpubStaging(EpubCorroboratedAuthorTests.Book());
        var service = new AssetIdentityVerificationService(new AssetRepository(asset), staging,
            [new FamilyLibrarian.Infrastructure.Security.EpubAssetIdentityVerifier(lookup, new FamilyLibrarian.Application.Matching.DeterministicBookMatcher(),
                new FamilyLibrarian.Infrastructure.Tests.Characterization.CharacterizationFixtures.NoRequestStore())],
            new RecordingAuditWriter(), new FixedClock(), expectedProfiles: new ExpectedArtifactProfileSource(lookup,
                new FamilyLibrarian.Infrastructure.Tests.Characterization.CharacterizationFixtures.NoRequestStore()));
        var result = await service.VerifyAsync(asset.Id, CancellationToken.None);
        Assert.AreEqual(match, result.IsMatch);
        Assert.AreEqual(match ? null : "identity.hold.length_far_below_expected", asset.IdentityHoldCode);
        Assert.AreEqual("words=6000;items=3;basis=xhtml_text", result.Facts.Single(f => f.Code == "profile.text.word_count").Value);
    }

    [TestMethod]
    public async Task AStoredBundleDurationIsComparedAndSurvivesRetryWithoutReplayingAnOldComparison()
    {
        var asset = CreateProcessingAsset(".m4b", true);
        var store = new ProfileStore();
        var recorder = new ArtifactEvidenceRecorder(store, new FixedClock());
        await recorder.RecordTechnicalInspectionAsync(asset, [AudioDurations.TotalDuration(14000, 20)], "profile:audio-bundle", CancellationToken.None);
        var source = new ProfileSource { Profile = ArtifactProfileComparerTests.Expected(seconds: 72000) };
        var service = new AssetIdentityVerificationService(new AssetRepository(asset), new RecordingStagingStore(),
            [new MatchingVerifier()], new RecordingAuditWriter(), new FixedClock(), recorder, source, store);
        var held = await service.VerifyAsync(asset.Id, CancellationToken.None);
        Assert.IsFalse(held.IsMatch);
        Assert.AreEqual("identity.hold.duration_far_below_expected", asset.IdentityHoldCode);
        Assert.AreEqual("seconds=14000;tracks=20", held.Facts.Single(f => f.Code == "profile.audio.total_duration").Value);
        source.Profile = source.Profile with { CatalogAudioSeconds = null };
        Assert.IsTrue((await service.RetryUnmatchedAsync(asset.Id, CancellationToken.None)).IsMatch);
        source.Profile = source.Profile with { CatalogAudioSeconds = 72000 };
        Assert.IsFalse((await service.VerifyAsync(asset.Id, CancellationToken.None)).IsMatch,
            "The raw total remains available after an identity pass records a newer profile.");
    }

    [TestMethod]
    public async Task AudioMeasurementsAreRecordedOncePerTrackPerEvaluationAcrossRetryAndReverification()
    {
        var assets = new[] { CreateProcessingAsset(".mp3", true), CreateProcessingAsset(".mp3", true) };
        var store = new ProfileStore();
        var recorder = new ArtifactEvidenceRecorder(store, new FixedClock());
        foreach (var asset in assets)
        {
            await recorder.RecordTechnicalInspectionAsync(asset, [AudioDurations.TotalDuration(14000, 2)], "profile:audio-bundle", CancellationToken.None);
            var source = new ProfileSource { Profile = ArtifactProfileComparerTests.Expected(seconds: 72000) };
            var service = new AssetIdentityVerificationService(new AssetRepository(asset), new RecordingStagingStore(),
                [new MatchingVerifier()], new RecordingAuditWriter(), new FixedClock(), recorder, source, store);
            Assert.IsFalse((await service.VerifyAsync(asset.Id, CancellationToken.None)).IsMatch);
            source.Profile = source.Profile with { CatalogAudioSeconds = null };
            Assert.IsTrue((await service.RetryUnmatchedAsync(asset.Id, CancellationToken.None)).IsMatch);
            source.Profile = source.Profile with { CatalogAudioSeconds = 72000 };
            Assert.IsFalse((await service.VerifyAsync(asset.Id, CancellationToken.None)).IsMatch);
            var measurements = store.Rows.Where(row => row.MediaAssetId == asset.Id && row.Code == "profile.audio.total_duration").ToArray();
            Assert.HasCount(1, measurements, "Identity and retry must reuse the measured profile, never record it again.");
            Assert.HasCount(1, measurements.Select(row => row.RecordId).Distinct().ToArray());
            await recorder.RecordTechnicalInspectionAsync(asset, [AudioDurations.TotalDuration(36000, 2)], "profile:audio-bundle", CancellationToken.None);
            Assert.IsTrue((await service.RetryUnmatchedAsync(asset.Id, CancellationToken.None)).IsMatch,
                "A fresh evaluation replaces the comparison basis with its new measurement.");
            Assert.IsTrue((await service.VerifyAsync(asset.Id, CancellationToken.None)).IsMatch);
            measurements = store.Rows.Where(row => row.MediaAssetId == asset.Id && row.Code == "profile.audio.total_duration").ToArray();
            Assert.HasCount(2, measurements, "Two evaluations produce exactly two measurement records per track.");
            Assert.HasCount(2, measurements.Select(row => row.RecordId).Distinct().ToArray());
        }
    }

    private sealed class ProfileSource : IExpectedArtifactProfileSource
    {
        public required ExpectedArtifactProfile Profile { get; set; }
        public Task<ExpectedArtifactProfile?> FindAsync(MediaAsset asset, CancellationToken cancellationToken) => Task.FromResult<ExpectedArtifactProfile?>(Profile);
    }

    private sealed class ProfileStore : IArtifactEvidenceStore
    {
        private readonly List<FamilyLibrarian.Domain.Evidence.ArtifactEvidence> _rows = [];
        public IReadOnlyList<FamilyLibrarian.Domain.Evidence.ArtifactEvidence> Rows => _rows;
        public Task<IReadOnlyList<FamilyLibrarian.Domain.Evidence.ArtifactEvidence>> ListLatestAudioMeasurementsAsync(MediaAsset asset, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FamilyLibrarian.Domain.Evidence.ArtifactEvidence>>(_rows.Where(row => row.MediaAssetId == asset.Id && row.Sha256 == asset.Sha256 &&
                row.Code is "profile.audio.total_duration" or "profile.audio.chapter_titles").GroupBy(row => row.Code).Select(group => group.Last()).ToArray());
        public void Add(IReadOnlyList<FamilyLibrarian.Domain.Evidence.ArtifactEvidence> evidence) => _rows.AddRange(evidence);
        public Task<bool> TrySaveAsync(CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<FamilyLibrarian.Domain.Evidence.ArtifactEvidence>>> ListLatestAsync(
            IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
        {
            var result = _rows.Where(row => ids.Contains(row.MediaAssetId)).GroupBy(row => row.MediaAssetId)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<FamilyLibrarian.Domain.Evidence.ArtifactEvidence>)group
                    .GroupBy(row => row.RecordKind).SelectMany(kind => kind.Where(row => row.RecordId == kind.Last().RecordId)).ToArray());
            return Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<FamilyLibrarian.Domain.Evidence.ArtifactEvidence>>>(result);
        }
    }

    private sealed class EpubStaging(byte[] bytes) : IAssetStagingStore
    {
        public Task<StagedFile> WriteToQuarantineAsync(Stream content, string originalFilename, long maxSizeBytes, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Stream> OpenAsync(MediaAssetStorageState zone, string storedFilename, CancellationToken cancellationToken) => Task.FromResult<Stream>(new MemoryStream(bytes));
        public Task MoveAsync(MediaAssetStorageState fromZone, MediaAssetStorageState toZone, string storedFilename, CancellationToken cancellationToken) => Task.CompletedTask;
    }

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

    [TestMethod]
    public async Task RecordingEvidenceNeverChangesAnIdentityVerdict()
    {
        foreach (var (format, preConfirmed, verifiers) in new (string, bool, IAssetIdentityVerifier[])[]
                 {
                     (".mobi", false, []),
                     (".mobi", true, []),
                     (".m4b", true, [new MatchingVerifier()])
                 })
        {
            var plainAsset = CreateProcessingAsset(format, preConfirmed);
            var plain = await new AssetIdentityVerificationService(
                new AssetRepository(plainAsset), new RecordingStagingStore(), verifiers, new RecordingAuditWriter(), new FixedClock())
                .VerifyAsync(plainAsset.Id, CancellationToken.None);

            var observedAsset = CreateProcessingAsset(format, preConfirmed);
            var recorder = new CapturingIdentityRecorder();
            var observed = await new AssetIdentityVerificationService(
                new AssetRepository(observedAsset), new RecordingStagingStore(), verifiers, new RecordingAuditWriter(), new FixedClock(), recorder)
                .VerifyAsync(observedAsset.Id, CancellationToken.None);

            Assert.AreEqual(plain.IsMatch, observed.IsMatch, $"{format}/{preConfirmed}");
            Assert.AreEqual(plain.VerifierId, observed.VerifierId);
            Assert.AreEqual(plainAsset.StorageState, observedAsset.StorageState);
            Assert.AreEqual(1, recorder.Calls);
            Assert.AreEqual(observed.IsMatch, recorder.LastResult!.IsMatch);
            // Held assets already carry their state when the evidence is recorded.
            Assert.AreEqual(observedAsset.StorageState, recorder.StorageStateWhenCalled);
        }
    }

    /// <summary>M1-5b (P1): no content verifier plus an external provider's claim alone is held, through the existing path.</summary>
    [TestMethod]
    [DataRow(AssetOrigin.ExternalProvider, "identity.hold.attested_only_external", false)]
    [DataRow(AssetOrigin.Unknown, "identity.hold.attested_only_unknown_origin", false)]
    [DataRow(AssetOrigin.BuiltInProvider, null, true)]
    public async Task AnAttestedOnlyFormatIsHeldOrTrustedAccordingToItsPersistedOrigin(AssetOrigin origin, string? expectedCode, bool passes)
    {
        var asset = CreateProcessingAsset(".mobi", identityPreConfirmed: true, origin);
        var service = new AssetIdentityVerificationService(
            new AssetRepository(asset), new RecordingStagingStore(), [], new RecordingAuditWriter(), new FixedClock());

        var result = await service.VerifyAsync(asset.Id, CancellationToken.None);

        Assert.AreEqual(passes, result.IsMatch);
        Assert.AreEqual(passes ? MediaAssetStorageState.Processing : MediaAssetStorageState.Unmatched, asset.StorageState);
        Assert.AreEqual(expectedCode, asset.IdentityHoldCode);
        Assert.AreEqual(passes, asset.IdentityMismatchReason is null);
    }

    [TestMethod]
    public async Task ARetryOfAHeldAttestedOnlyAssetStaysHeldUntilAnAdministratorOverridesIt()
    {
        var asset = CreateProcessingAsset(".mp3", identityPreConfirmed: true, AssetOrigin.ExternalProvider);
        var repository = new AssetRepository(asset);
        var staging = new RecordingStagingStore();
        var service = new AssetIdentityVerificationService(repository, staging, [], new RecordingAuditWriter(), new FixedClock());
        await service.VerifyAsync(asset.Id, CancellationToken.None);

        var retry = await service.RetryUnmatchedAsync(asset.Id, CancellationToken.None);

        Assert.IsFalse(retry.IsMatch, "Re-running the same deterministic check cannot clear a hold that exists because no check can run.");
        Assert.AreEqual(MediaAssetStorageState.Unmatched, asset.StorageState);
        Assert.AreEqual("identity.hold.attested_only_external", asset.IdentityHoldCode);
    }

    [TestMethod]
    public async Task ACompletenessCueHoldIsPersistedWithItsCode()
    {
        var asset = CreateProcessingAsset(".epub", identityPreConfirmed: true, AssetOrigin.ManualUpload);
        var cue = new IdentityFact("ev", FamilyLibrarian.Domain.Evidence.EvidenceDimension.Completeness,
            "artifact.completeness.sample_marker_in_metadata", FamilyLibrarian.Domain.Evidence.EvidencePolarity.Condition, "subject:sample");
        var service = new AssetIdentityVerificationService(
            new AssetRepository(asset), new RecordingStagingStore(),
            [new FixedVerifier(AssetIdentityVerificationResult.Match("verifier", [cue]))], new RecordingAuditWriter(), new FixedClock());

        var result = await service.VerifyAsync(asset.Id, CancellationToken.None);

        Assert.IsFalse(result.IsMatch);
        Assert.AreEqual(MediaAssetStorageState.Unmatched, asset.StorageState);
        Assert.AreEqual("identity.hold.completeness_sample_marker", asset.IdentityHoldCode);
        StringAssert.Contains(asset.IdentityMismatchReason, "subject:sample");
    }

    [TestMethod]
    public async Task AnAttestedFormatWithNoVerifierStillPassesAsBeforeButIsRecordedAsUncorroborated()
    {
        var asset = CreateProcessingAsset(".mp3", identityPreConfirmed: true);
        var recorder = new CapturingIdentityRecorder();
        var result = await new AssetIdentityVerificationService(
            new AssetRepository(asset), new RecordingStagingStore(), [], new RecordingAuditWriter(), new FixedClock(), recorder)
            .VerifyAsync(asset.Id, CancellationToken.None);

        Assert.IsTrue(result.IsMatch, "M1-2 is evidence-only: existing behavior is preserved.");
        Assert.AreEqual(ArtifactEvidenceRecorder.NoVerifierId, recorder.LastResult!.VerifierId);
        Assert.IsEmpty(recorder.LastResult.Facts, "Nothing in the file was compared, so there are no content facts.");
    }

    private sealed class CapturingIdentityRecorder : IArtifactEvidenceRecorder
    {
        public int Calls { get; private set; }

        public AssetIdentityVerificationResult? LastResult { get; private set; }

        public MediaAssetStorageState StorageStateWhenCalled { get; private set; }

        public Task RecordSecurityEvaluationAsync(
            MediaAsset asset, SecurityEvaluation evaluation, IReadOnlyDictionary<string, string?> toolVersions,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RecordIdentityOverrideAsync(
            MediaAsset asset, Guid? administratorUserId, string? overriddenHoldCode, string reason, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RecordTechnicalInspectionAsync(
            MediaAsset asset, IReadOnlyList<IdentityFact> findings, string source, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RecordIdentityVerificationAsync(
            MediaAsset asset, AssetIdentityVerificationResult result, CancellationToken cancellationToken)
        {
            Calls++;
            LastResult = result;
            StorageStateWhenCalled = asset.StorageState;
            return Task.CompletedTask;
        }
    }

    private static AssetIdentityVerificationResult Held(params IdentityFact[] facts) =>
        AssetIdentityVerificationResult.Unmatched("verifier", "held", facts);

    private static IdentityFact Fact(FamilyLibrarian.Domain.Evidence.EvidenceDimension dimension, FamilyLibrarian.Domain.Evidence.EvidencePolarity polarity) =>
        new("ev", dimension, "code", polarity);

    /// <summary>M1-5a: the holds that already exist get a stable code; no hold is added and no verdict changes.</summary>
    [TestMethod]
    public async Task EveryExistingHoldIsLabeledWithAStableCodeAndAMatchHasNone()
    {
        var contradicted = Held(Fact(FamilyLibrarian.Domain.Evidence.EvidenceDimension.IdentityCorroboration, FamilyLibrarian.Domain.Evidence.EvidencePolarity.Negative));
        var languageOnly = Held(
            Fact(FamilyLibrarian.Domain.Evidence.EvidenceDimension.IdentityCorroboration, FamilyLibrarian.Domain.Evidence.EvidencePolarity.Positive),
            Fact(FamilyLibrarian.Domain.Evidence.EvidenceDimension.Language, FamilyLibrarian.Domain.Evidence.EvidencePolarity.Negative));
        var both = Held(
            Fact(FamilyLibrarian.Domain.Evidence.EvidenceDimension.IdentityCorroboration, FamilyLibrarian.Domain.Evidence.EvidencePolarity.Negative),
            Fact(FamilyLibrarian.Domain.Evidence.EvidenceDimension.Language, FamilyLibrarian.Domain.Evidence.EvidencePolarity.Negative));
        var inconclusive = Held();

        foreach (var (result, expected) in new[]
                 {
                     (contradicted, "identity.hold.content_contradicted"),
                     (languageOnly, "identity.hold.language_conflict"),
                     (both, "identity.hold.content_contradicted"),
                     (inconclusive, "identity.hold.content_inconclusive")
                 })
        {
            var asset = CreateProcessingAsset(".epub", identityPreConfirmed: true);
            var service = new AssetIdentityVerificationService(
                new AssetRepository(asset), new RecordingStagingStore(), [new FixedVerifier(result)], new RecordingAuditWriter(), new FixedClock());

            var verdict = await service.VerifyAsync(asset.Id, CancellationToken.None);

            Assert.IsFalse(verdict.IsMatch);
            Assert.AreEqual(MediaAssetStorageState.Unmatched, asset.StorageState);
            Assert.AreEqual(expected, asset.IdentityHoldCode);
            Assert.AreEqual("held", asset.IdentityMismatchReason, "The human-readable reason is unchanged.");
        }

        var matching = CreateProcessingAsset(".epub", identityPreConfirmed: true);
        await new AssetIdentityVerificationService(
            new AssetRepository(matching), new RecordingStagingStore(), [new MatchingVerifier()], new RecordingAuditWriter(), new FixedClock())
            .VerifyAsync(matching.Id, CancellationToken.None);
        Assert.IsNull(matching.IdentityHoldCode);
    }

    [TestMethod]
    public async Task TheExistingNoVerifierHoldForAnUnattestedAssetIsLabeledNotAttested()
    {
        var asset = CreateProcessingAsset(".mobi", identityPreConfirmed: false);
        var service = new AssetIdentityVerificationService(
            new AssetRepository(asset), new RecordingStagingStore(), [], new RecordingAuditWriter(), new FixedClock());

        await service.VerifyAsync(asset.Id, CancellationToken.None);

        Assert.AreEqual(MediaAssetStorageState.Unmatched, asset.StorageState);
        Assert.AreEqual("identity.hold.not_attested", asset.IdentityHoldCode);
    }

    [TestMethod]
    public async Task ASuccessfulRetryClearsTheHoldCodeAndAFailedRetryRelabelsIt()
    {
        var asset = CreateUnmatchedAsset();
        asset.SetIdentityHoldCode("identity.hold.content_inconclusive");
        var staging = new RecordingStagingStore();
        var repository = new AssetRepository(asset);
        var contradicted = Held(Fact(FamilyLibrarian.Domain.Evidence.EvidenceDimension.IdentityCorroboration, FamilyLibrarian.Domain.Evidence.EvidencePolarity.Negative));

        await new AssetIdentityVerificationService(repository, staging, [new FixedVerifier(contradicted)], new RecordingAuditWriter(), new FixedClock())
            .RetryUnmatchedAsync(asset.Id, CancellationToken.None);
        Assert.AreEqual("identity.hold.content_contradicted", asset.IdentityHoldCode, "A still-unmatched retry refreshes the label.");

        await new AssetIdentityVerificationService(repository, staging, [new MatchingVerifier()], new RecordingAuditWriter(), new FixedClock())
            .RetryUnmatchedAsync(asset.Id, CancellationToken.None);
        Assert.AreEqual(MediaAssetStorageState.Processing, asset.StorageState);
        Assert.IsNull(asset.IdentityHoldCode);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PeerSizeOutlierIsRecordedWithoutHoldingAMatchIncludingRetry(bool retry)
    {
        var now = DateTimeOffset.UtcNow;
        var asset = new MediaAsset(Guid.NewGuid(), null, RequestMediaType.Ebook, ".epub", "Book.epub", "asset.epub",
            600_000, new string('a', 64), "application/epub+zip", Guid.NewGuid(), null, now,
            identityPreConfirmed: true, origin: AssetOrigin.ExternalProvider);
        asset.TransitionStorageState(MediaAssetStorageState.Processing, now);
        if (retry) asset.TransitionStorageState(MediaAssetStorageState.Unmatched, now);
        var recorder = new CapturingIdentityRecorder();
        var result = await RunAsync();
        Assert.IsTrue(result.IsMatch);
        Assert.IsNull(result.HoldCode);
        Assert.IsNull(asset.IdentityHoldCode);
        Assert.AreEqual(MediaAssetStorageState.Processing, asset.StorageState);
        var finding = recorder.LastResult!.Facts.Single(f => f.Code == "profile.compare.size_peer_outlier");
        Assert.AreEqual(FamilyLibrarian.Domain.Evidence.EvidencePolarity.Weak, finding.Polarity);
        Assert.AreEqual("bytes=600000;peer_median_bytes=4000000;peers=3", finding.Value);

        async Task<AssetIdentityVerificationResult> RunAsync()
        {
            var service = new AssetIdentityVerificationService(new AssetRepository(asset), new RecordingStagingStore(),
                [new MatchingVerifier()], new RecordingAuditWriter(), new FixedClock(), recorder, new PeerExpectedSource());
            return retry ? await service.RetryUnmatchedAsync(asset.Id, CancellationToken.None)
                : await service.VerifyAsync(asset.Id, CancellationToken.None);
        }
    }

    private sealed class PeerExpectedSource : IExpectedArtifactProfileSource
    {
        public Task<ExpectedArtifactProfile?> FindAsync(MediaAsset asset, CancellationToken cancellationToken) =>
            Task.FromResult<ExpectedArtifactProfile?>(new("Book", [], null, [], [], "en", null, null, false,
                4_000_000, 3, asset.SizeBytes));
    }

    private sealed class FixedVerifier(AssetIdentityVerificationResult result) : IAssetIdentityVerifier
    {
        public string Id => "verifier";

        public bool Supports(MediaAsset asset) => true;

        public Task<AssetIdentityVerificationResult> VerifyAsync(MediaAsset asset, Stream content, CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }

    private static MediaAsset CreateProcessingAsset(
        string format, bool identityPreConfirmed, AssetOrigin origin = AssetOrigin.BuiltInProvider)
    {
        var asset = new MediaAsset(
            Guid.NewGuid(), null, RequestMediaType.Ebook, format, $"Book{format}", $"asset{format}", 12,
            new string('a', 64), "application/octet-stream", Guid.NewGuid(), null, DateTimeOffset.UtcNow,
            identityPreConfirmed: identityPreConfirmed, origin: origin,
            originUserId: origin == AssetOrigin.ManualUpload ? Guid.NewGuid() : null);
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
