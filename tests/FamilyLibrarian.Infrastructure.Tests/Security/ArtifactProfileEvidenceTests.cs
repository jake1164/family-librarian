using System.Buffers.Binary;
using System.Text;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.SemanticAssessment;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Evidence;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Domain.Security;
using FamilyLibrarian.Infrastructure.Security;
using static FamilyLibrarian.Infrastructure.Tests.Characterization.CharacterizationFixtures;

namespace FamilyLibrarian.Infrastructure.Tests.Security;

/// <summary>
/// AUTOFIND 3c: profile facts are recorded as their own <c>artifact-profile</c> pass beside the identity and technical passes,
/// never as part of an identity record, and the audio profile (total duration, chapter names) is read from bounded headers.
/// </summary>
[TestClass]
public sealed class ArtifactProfileEvidenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private static IdentityFact Profile(string id, string code, string value) =>
        new(id, EvidenceDimension.Profile, code, EvidencePolarity.Unknown, value);

    private static MediaAsset EpubAsset() => new(
        Guid.NewGuid(), null, RequestMediaType.Ebook, ".epub", "book.epub", "stored.epub", 1, new string('b', 64),
        "application/epub+zip", Guid.NewGuid(), null, Now, identityPreConfirmed: true, origin: AssetOrigin.ExternalProvider);

    // ---- recorder ------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task ProfileFactsFromAnIdentityCheckAreARecordOfTheirOwnKind()
    {
        var store = new FakeStore();
        var recorder = new ArtifactEvidenceRecorder(store, new FixedClock());
        var asset = EpubAsset();
        var result = AssetIdentityVerificationResult.Match("epub-package-metadata",
        [
            new IdentityFact("ev-title", EvidenceDimension.IdentityCorroboration, EvidenceReasonCodes.ArtifactIdentityCorroboration.TitleAgrees, EvidencePolarity.Positive),
            Profile("ev-profile-words", EvidenceReasonCodes.ArtifactProfile.TextWordCount, "words=6000;items=3;basis=xhtml_text"),
            Profile("ev-toc-1", EvidenceReasonCodes.ArtifactProfile.TocEntry, "Chapter One")
        ]);

        await recorder.RecordIdentityVerificationAsync(asset, result, CancellationToken.None);

        var profile = store.Saved.Where(item => item.RecordKind == ArtifactEvidenceRecordKinds.ArtifactProfile).ToArray();
        Assert.HasCount(2, profile);
        Assert.IsTrue(profile.All(item => item.Dimension == EvidenceDimension.Profile && item.Polarity == EvidencePolarity.Unknown));
        Assert.HasCount(1, profile.Select(item => item.RecordId).Distinct().ToArray());
        var identity = store.Saved.Where(item => item.RecordKind == ArtifactEvidenceRecordKinds.IdentityVerification).ToArray();
        Assert.IsFalse(identity.Any(item => item.Dimension == EvidenceDimension.Profile), "Identity evidence never carries profile facts.");
        Assert.AreNotEqual(identity[0].RecordId, profile[0].RecordId);
        Assert.IsTrue(identity.Any(item => item.FindingId == "ev-title"));
    }

    [TestMethod]
    public async Task AnIdentityCheckWithNoProfileFactsWritesNoProfileRecord()
    {
        var store = new FakeStore();
        await new ArtifactEvidenceRecorder(store, new FixedClock()).RecordIdentityVerificationAsync(
            EpubAsset(), AssetIdentityVerificationResult.Match("v"), CancellationToken.None);

        Assert.IsFalse(store.Saved.Any(item => item.RecordKind == ArtifactEvidenceRecordKinds.ArtifactProfile));
    }

    [TestMethod]
    public async Task TechnicalFindingsAndProfileFindingsAreRecordedAsSeparatePasses()
    {
        var store = new FakeStore();
        var asset = EpubAsset();
        await new ArtifactEvidenceRecorder(store, new FixedClock()).RecordTechnicalInspectionAsync(
            asset,
            [
                new IdentityFact("ev-audio-duration", EvidenceDimension.AudioTechnical, EvidenceReasonCodes.ArtifactAudio.DurationMeasured, EvidencePolarity.Unknown, "seconds=60;basis=x"),
                AudioDurations.TotalDuration(60, 1)
            ],
            "inspector:test", CancellationToken.None);

        Assert.AreEqual(1, store.Saved.Count(item => item.RecordKind == ArtifactEvidenceRecordKinds.TechnicalInspection));
        var profile = store.Saved.Single(item => item.RecordKind == ArtifactEvidenceRecordKinds.ArtifactProfile);
        Assert.AreEqual("seconds=60;tracks=1", profile.Value);
        Assert.AreEqual(EvidenceReasonCodes.ArtifactProfile.AudioTotalDuration, profile.Code);
    }

    [TestMethod]
    public void ProfileFindingsAreNotShownToTheShadowReviewerAndCannotCrowdOutIdentityFindings()
    {
        var recordId = Guid.NewGuid();
        var evidence = Enumerable.Range(0, 80)
            .Select(index => new ArtifactEvidence(
                Guid.NewGuid(), new string('a', 64), ArtifactEvidenceRecordKinds.ArtifactProfile, recordId, $"ev-toc-{index}",
                EvidenceDimension.Profile, EvidenceReasonCodes.ArtifactProfile.TocEntry, EvidencePolarity.Unknown, "verifier:x", "Title", null, Now))
            .Append(new ArtifactEvidence(
                Guid.NewGuid(), new string('a', 64), ArtifactEvidenceRecordKinds.IdentityVerification, Guid.NewGuid(), "ev-title",
                EvidenceDimension.IdentityCorroboration, EvidenceReasonCodes.ArtifactIdentityCorroboration.TitleAgrees,
                EvidencePolarity.Positive, "verifier:x", null, null, Now))
            .ToArray();

        var findings = ArtifactSemanticEvidence.ToFindings(evidence, includeDetails: true);

        Assert.HasCount(1, findings);
        Assert.AreEqual("idv.ev-title", findings[0].Id);
        Assert.IsFalse(ArtifactSemanticEvidence.IsEligible(evidence.Where(item => item.Dimension == EvidenceDimension.Profile).ToArray()),
            "Profile findings alone never make an asset worth a model's time.");
    }

    // ---- audio profile -------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task AnM4bReportsItsFirstTenChapterNamesAndItsTotalDuration()
    {
        var titles = Enumerable.Range(1, 12).Select(index => $"Chapter {index}").ToArray();
        using var m4b = M4bWith(Track(timescale: 1_000, duration: 3_600_000, codec: "mp4a"), ChapterList(titles));

        var facts = await new M4bProfileInspector().InspectAsync(Asset(RequestMediaType.Audiobook, ".m4b"), m4b, CancellationToken.None);

        Assert.AreEqual("seconds=3600;tracks=1", facts.Single(fact => fact.Code == EvidenceReasonCodes.ArtifactProfile.AudioTotalDuration).Value);
        var chapters = facts.Single(fact => fact.Code == EvidenceReasonCodes.ArtifactProfile.AudioChapterTitles).Value!;
        Assert.AreEqual(string.Join(" / ", titles.Take(10)), chapters);
        Assert.IsTrue(facts.All(fact => fact.Dimension == EvidenceDimension.Profile && fact.Polarity == EvidencePolarity.Unknown));
    }

    [TestMethod]
    public async Task AnM4bWithNoChapterListHasNoChapterFact()
    {
        using var m4b = M4bWith(Track(timescale: 1_000, duration: 60_000));

        var facts = await new M4bProfileInspector().InspectAsync(Asset(RequestMediaType.Audiobook, ".m4b"), m4b, CancellationToken.None);

        Assert.IsFalse(facts.Any(fact => fact.Code == EvidenceReasonCodes.ArtifactProfile.AudioChapterTitles));
    }

    [TestMethod]
    public async Task AChapterListClaimingMoreEntriesThanItHoldsIsReadSafely()
    {
        var chpl = Box("chpl", Concat([1, 0, 0, 0], new byte[4], [200], ChapterEntry("Only One")));
        using var m4b = M4bWith(Track(timescale: 1_000, duration: 60_000), Box("udta", chpl));

        var facts = await new M4bProfileInspector().InspectAsync(Asset(RequestMediaType.Audiobook, ".m4b"), m4b, CancellationToken.None);

        Assert.AreEqual("Only One", facts.Single(fact => fact.Code == EvidenceReasonCodes.ArtifactProfile.AudioChapterTitles).Value);
    }

    [TestMethod]
    public async Task ATrackOfASetDoesNotClaimTheWholeTotal()
    {
        using var m4b = M4bWith(Track(timescale: 1_000, duration: 60_000));
        var track = SetTrack(".m4b", Guid.NewGuid(), 1, 2);

        var facts = await new M4bProfileInspector().InspectAsync(track, m4b, CancellationToken.None);

        Assert.IsFalse(facts.Any(fact => fact.Code == EvidenceReasonCodes.ArtifactProfile.AudioTotalDuration));
    }

    [TestMethod]
    public async Task AnMp3ReportsItsOwnTotalDuration()
    {
        var bytes = Concat(Frame(1_000), Frame(), Frame());

        var facts = await new Mp3ProfileInspector().InspectAsync(Asset(RequestMediaType.Audiobook, ".mp3"), new MemoryStream(bytes), CancellationToken.None);

        Assert.AreEqual("seconds=26;tracks=1", facts.Single().Value);
    }

    [TestMethod]
    public void TheDurationFactIsReadBackAndGarbageIsRefused()
    {
        Assert.IsTrue(AudioDurations.TryReadSeconds(
            [new IdentityFact("d", EvidenceDimension.AudioTechnical, EvidenceReasonCodes.ArtifactAudio.DurationMeasured, EvidencePolarity.Unknown, "seconds=3600;basis=media_header")],
            out var seconds));
        Assert.AreEqual(3600d, seconds);
        Assert.IsFalse(AudioDurations.TryReadSeconds(
            [new IdentityFact("d", EvidenceDimension.AudioTechnical, EvidenceReasonCodes.ArtifactAudio.DurationMeasured, EvidencePolarity.Unknown, "seconds=abc")], out _));
        Assert.IsFalse(AudioDurations.TryReadSeconds([], out _));
    }

    [TestMethod]
    public async Task ASetRecordsTheSummedDurationOnEveryTrack()
    {
        var bundle = Guid.NewGuid();
        var tracks = new[] { SetTrack(".mp3", bundle, 1, 3), SetTrack(".mp3", bundle, 2, 3), SetTrack(".mp3", bundle, 3, 3) };
        var recorder = new CapturingRecorder();
        var profile = new AudioBundleProfile(
            new BytesStore(Concat(Frame(1_000), Frame(), Frame())), [new Mp3TechnicalInspector()], recorder);

        await profile.RecordAsync(tracks, CancellationToken.None);

        Assert.AreEqual(3, recorder.Technical.Count);
        Assert.IsTrue(recorder.Technical.All(call => call.Findings.Single().Value == "seconds=78;tracks=3"));
        CollectionAssert.AreEquivalent(tracks.Select(track => track.Id).ToArray(), recorder.Technical.Select(call => call.AssetId).ToArray());
    }

    [TestMethod]
    public async Task ASetWithAnUnmeasurableTrackRecordsNothingRatherThanAPartialSum()
    {
        var bundle = Guid.NewGuid();
        var tracks = new[] { SetTrack(".mp3", bundle, 1, 2), SetTrack(".mp3", bundle, 2, 2) };
        var recorder = new CapturingRecorder();
        var profile = new AudioBundleProfile(new BytesStore(new byte[5_000]), [new Mp3TechnicalInspector()], recorder);

        await profile.RecordAsync(tracks, CancellationToken.None);

        Assert.IsEmpty(recorder.Technical);
    }

    [TestMethod]
    public async Task AFailingStoreOrInspectorNeverEscapesTheSetProfile()
    {
        var tracks = new[] { SetTrack(".mp3", Guid.NewGuid(), 1, 2), SetTrack(".mp3", Guid.NewGuid(), 2, 2) };

        await new AudioBundleProfile(new ThrowingStore(), [new Mp3TechnicalInspector()], new CapturingRecorder())
            .RecordAsync(tracks, CancellationToken.None);
    }

    // ---- builders ------------------------------------------------------------------------------------------------

    private static MediaAsset SetTrack(string format, Guid bundleId, int sequence, int total) => new(
        Guid.NewGuid(), null, RequestMediaType.Audiobook, format, $"track{sequence}{format}", $"stored{sequence}{format}", 1,
        new string('c', 64), "audio/mpeg", Guid.NewGuid(), null, Now, bundleId: bundleId, bundleSequence: sequence,
        bundleTrackCount: total, identityPreConfirmed: true, origin: AssetOrigin.ExternalProvider);

    private static MemoryStream M4bWith(byte[] track, byte[]? udta = null) => new(Concat(
        Box("ftyp", "M4B "u8.ToArray()), Box("moov", Concat(track, udta ?? [])), Box("mdat", new byte[4096])));

    private static byte[] ChapterEntry(string title)
    {
        var bytes = Encoding.UTF8.GetBytes(title);
        return Concat(new byte[8], [(byte)bytes.Length], bytes);
    }

    private static byte[] ChapterList(string[] titles) =>
        Box("udta", Box("chpl", Concat([1, 0, 0, 0], new byte[4], [(byte)titles.Length], Concat(titles.Select(ChapterEntry).ToArray()))));

    // 128 kbps, 44.1 kHz, stereo, MPEG-1 Layer III: frame length 144 * 128000 / 44100 = 417 bytes.
    private static byte[] Frame(uint? xingFrames = null)
    {
        var frame = new byte[417];
        frame[0] = 0xFF;
        frame[1] = 0xFB;
        frame[2] = 0x90;
        if (xingFrames is { } frames)
        {
            Encoding.ASCII.GetBytes("Xing").CopyTo(frame, 36);
            BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(40), 1);
            BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(44), frames);
        }

        return frame;
    }

    private sealed class BytesStore(byte[] bytes) : IAssetStagingStore
    {
        public Task<StagedFile> WriteToQuarantineAsync(Stream content, string originalFilename, long maxSizeBytes, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Stream> OpenAsync(MediaAssetStorageState zone, string storedFilename, CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new MemoryStream(bytes));

        public Task MoveAsync(MediaAssetStorageState fromZone, MediaAssetStorageState toZone, string storedFilename, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class ThrowingStore : IAssetStagingStore
    {
        public Task<StagedFile> WriteToQuarantineAsync(Stream content, string originalFilename, long maxSizeBytes, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Stream> OpenAsync(MediaAssetStorageState zone, string storedFilename, CancellationToken cancellationToken) =>
            throw new IOException("disk gone");

        public Task MoveAsync(MediaAssetStorageState fromZone, MediaAssetStorageState toZone, string storedFilename, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class CapturingRecorder : IArtifactEvidenceRecorder
    {
        public List<(Guid AssetId, IReadOnlyList<IdentityFact> Findings)> Technical { get; } = [];

        public Task RecordSecurityEvaluationAsync(
            MediaAsset asset, SecurityEvaluation evaluation, IReadOnlyDictionary<string, string?> toolVersions, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RecordIdentityVerificationAsync(MediaAsset asset, AssetIdentityVerificationResult result, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RecordTechnicalInspectionAsync(
            MediaAsset asset, IReadOnlyList<IdentityFact> findings, string source, CancellationToken cancellationToken)
        {
            Technical.Add((asset.Id, findings));
            return Task.CompletedTask;
        }

        public Task RecordIdentityOverrideAsync(
            MediaAsset asset, Guid? administratorUserId, string? overriddenHoldCode, string reason, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class FakeStore : IArtifactEvidenceStore
    {
        private readonly List<ArtifactEvidence> _pending = [];

        public List<ArtifactEvidence> Saved { get; } = [];

        public void Add(IReadOnlyList<ArtifactEvidence> evidence) => _pending.AddRange(evidence);

        public Task<bool> TrySaveAsync(CancellationToken cancellationToken)
        {
            Saved.AddRange(_pending);
            _pending.Clear();
            return Task.FromResult(true);
        }

        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<ArtifactEvidence>>> ListLatestAsync(
            IReadOnlyCollection<Guid> assetIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<ArtifactEvidence>>>(
                new Dictionary<Guid, IReadOnlyList<ArtifactEvidence>>());
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }
}
