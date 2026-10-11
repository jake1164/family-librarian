using System.Buffers.Binary;
using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Evidence;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Security;
using static FamilyLibrarian.Infrastructure.Tests.Characterization.CharacterizationFixtures;

namespace FamilyLibrarian.Infrastructure.Tests.Security;

/// <summary>
/// M1-4c: audiobook measurements read from container headers. They are informational (their own evidence
/// dimension) and are never an input to identity verification. A value that cannot be read is reported as
/// unavailable, never guessed.
/// </summary>
[TestClass]
public sealed class AudioTechnicalInspectorTests
{
    private static MemoryStream M4bWith(byte[] track, byte[]? udta = null) => new(Concat(
        Box("ftyp", "M4B "u8.ToArray()), Box("moov", Concat(track, udta ?? [])), Box("mdat", new byte[4096])));

    private static async Task<IReadOnlyList<IdentityFact>> InspectM4bAsync(Stream content) =>
        await new M4bTechnicalInspector().InspectAsync(Asset(RequestMediaType.Audiobook, ".m4b"), content, CancellationToken.None);

    private static string? ValueOf(IReadOnlyList<IdentityFact> facts, string code) => facts.SingleOrDefault(fact => fact.Code == code)?.Value;

    [TestMethod]
    public async Task AnM4bReportsDurationCodecBitrateAndChaptersFromItsHeaders()
    {
        using var m4b = M4bWith(Track(timescale: 1_000, duration: 3_600_000, codec: "mp4a"), Box("udta", Chapters(12)));

        var facts = await InspectM4bAsync(m4b);

        Assert.AreEqual("seconds=3600;basis=media_header", ValueOf(facts, EvidenceReasonCodes.ArtifactAudio.DurationMeasured));
        Assert.AreEqual("mp4a", ValueOf(facts, EvidenceReasonCodes.ArtifactAudio.Codec));
        StringAssert.StartsWith(ValueOf(facts, EvidenceReasonCodes.ArtifactAudio.Bitrate), "kbps_avg=");
        Assert.AreEqual("12", ValueOf(facts, EvidenceReasonCodes.ArtifactAudio.ChaptersCounted));
        Assert.IsTrue(facts.All(fact => fact.Dimension == EvidenceDimension.AudioTechnical && fact.Polarity == EvidencePolarity.Unknown),
            "Measurements are never positive or negative evidence.");
    }

    [TestMethod]
    public async Task AVersionOneMediaHeaderWithA64BitDurationIsRead()
    {
        using var m4b = M4bWith(Track(mdhdVersion: 1, timescale: 44_100, duration: 44_100UL * 7_200, codec: "mp4a"));

        var facts = await InspectM4bAsync(m4b);

        Assert.AreEqual("seconds=7200;basis=media_header", ValueOf(facts, EvidenceReasonCodes.ArtifactAudio.DurationMeasured));
    }

    [TestMethod]
    public async Task AnM4bWithNoChapterListSaysSoAndNeverInventsOne()
    {
        using var m4b = M4bWith(Track(timescale: 1_000, duration: 60_000));

        var facts = await InspectM4bAsync(m4b);

        Assert.IsNotNull(ValueOf(facts, EvidenceReasonCodes.ArtifactAudio.ChaptersNotFound));
        Assert.IsNull(ValueOf(facts, EvidenceReasonCodes.ArtifactAudio.ChaptersCounted));
    }

    [TestMethod]
    public async Task ZeroDurationNoAudioTrackAndTruncatedHeadersAreUnavailableNotGuessed()
    {
        foreach (var track in new[] { Track(timescale: 1_000, duration: 0), Track("vide", timescale: 1_000, duration: 60_000), Track(timescale: 0, duration: 60_000) })
        {
            using var m4b = M4bWith(track);
            var facts = await InspectM4bAsync(m4b);
            Assert.IsNotNull(ValueOf(facts, EvidenceReasonCodes.ArtifactAudio.DurationUnavailable));
            Assert.IsNull(ValueOf(facts, EvidenceReasonCodes.ArtifactAudio.DurationMeasured));
            Assert.IsNull(ValueOf(facts, EvidenceReasonCodes.ArtifactAudio.Bitrate), "No bitrate without a duration.");
        }

        using var truncated = new MemoryStream(Box("ftyp", "M4B "u8.ToArray()).Concat(new byte[] { 0, 0, 0, 200, (byte)'m', (byte)'o', (byte)'o', (byte)'v', 1, 2 }).ToArray());
        var unreadable = await InspectM4bAsync(truncated);
        Assert.IsNotNull(ValueOf(unreadable, EvidenceReasonCodes.ArtifactAudio.DurationUnavailable));
    }

    [TestMethod]
    public void EachInspectorClaimsOnlyItsOwnFormat()
    {
        Assert.IsTrue(new M4bTechnicalInspector().Supports(Asset(RequestMediaType.Audiobook, ".m4b")));
        Assert.IsFalse(new M4bTechnicalInspector().Supports(Asset(RequestMediaType.Audiobook, ".mp3")));
        Assert.IsTrue(new Mp3TechnicalInspector().Supports(Asset(RequestMediaType.Audiobook, ".MP3")));
        Assert.IsFalse(new Mp3TechnicalInspector().Supports(Asset(RequestMediaType.Audiobook, ".m4b")));
    }

    // 128 kbps, 44.1 kHz, stereo, MPEG-1 Layer III: frame length 144 * 128000 / 44100 = 417 bytes.
    private const int FrameLength = 417;

    private static byte[] Frame(uint? xingFrames = null, string tag = "Xing")
    {
        var frame = new byte[FrameLength];
        frame[0] = 0xFF;
        frame[1] = 0xFB;
        frame[2] = 0x90;
        frame[3] = 0x00;
        if (xingFrames is { } frames)
        {
            "\0\0\0\0"u8.CopyTo(frame.AsSpan(4));
            System.Text.Encoding.ASCII.GetBytes(tag).CopyTo(frame, 36);
            BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(40), 1);
            BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(44), frames);
        }

        return frame;
    }

    private static async Task<IReadOnlyList<IdentityFact>> InspectMp3Async(byte[] bytes)
    {
        using var content = new MemoryStream(bytes);
        return await new Mp3TechnicalInspector().InspectAsync(Asset(RequestMediaType.Audiobook, ".mp3"), content, CancellationToken.None);
    }

    [TestMethod]
    public async Task AnMp3WithAFrameCountHeaderGetsAnExactDurationAndItsCodec()
    {
        var facts = await InspectMp3Async(Concat(Frame(xingFrames: 1_000), Frame(), Frame()));

        Assert.AreEqual("seconds=26;basis=frame_count_header", ValueOf(facts, EvidenceReasonCodes.ArtifactAudio.DurationMeasured));
        Assert.AreEqual("mp3;mpeg1;layer3;sample_rate_hz=44100", ValueOf(facts, EvidenceReasonCodes.ArtifactAudio.Codec));
        Assert.AreEqual("kbps=128;mode=vbr_header", ValueOf(facts, EvidenceReasonCodes.ArtifactAudio.Bitrate));
    }

    [TestMethod]
    public async Task AConstantBitrateMp3WithoutAHeaderGetsALabeledEstimate()
    {
        var facts = await InspectMp3Async(Concat(Enumerable.Range(0, 1_000).Select(_ => Frame()).ToArray()));

        Assert.AreEqual("seconds=26;basis=cbr_estimate", ValueOf(facts, EvidenceReasonCodes.ArtifactAudio.DurationMeasured));
        Assert.AreEqual("kbps=128;mode=first_frame", ValueOf(facts, EvidenceReasonCodes.ArtifactAudio.Bitrate));
    }

    [TestMethod]
    public async Task AnId3TagIsSkippedBeforeTheFirstFrame()
    {
        // ID3v2 header declaring 300 bytes of tag body (syncsafe), then the audio.
        var id3 = Concat("ID3"u8.ToArray(), [4, 0, 0, 0, 0, 2, 44], new byte[300]);

        var facts = await InspectMp3Async(Concat(id3, Concat(Enumerable.Range(0, 100).Select(_ => Frame()).ToArray())));

        StringAssert.Contains(ValueOf(facts, EvidenceReasonCodes.ArtifactAudio.DurationMeasured), "basis=cbr_estimate");
        Assert.AreEqual("kbps=128;mode=first_frame", ValueOf(facts, EvidenceReasonCodes.ArtifactAudio.Bitrate));
    }

    [TestMethod]
    public async Task BytesThatAreNotMpegAudioAreUnavailableNotGuessed()
    {
        var facts = await InspectMp3Async(new byte[5_000]);

        Assert.AreEqual(1, facts.Count);
        Assert.AreEqual(EvidenceReasonCodes.ArtifactAudio.DurationUnavailable, facts[0].Code);
    }

    [TestMethod]
    public async Task MeasurementsNeverChangeTheIdentityVerdict()
    {
        // The same M4B verified with and without duration/codec/chapter data produces the identical verdict and facts.
        var verifier = new M4bAssetIdentityVerifier(new StubWorkLookup(Title, Author), new DeterministicBookMatcher());
        var tags = new[] { ("nam", Title), ("ART", Author) };
        using var plain = M4bWithTracks([Track()], tags);
        using var measured = M4bWithTracks([Track(timescale: 1_000, duration: 3_600_000, codec: "mp4a")], tags);

        var before = await verifier.VerifyAsync(Asset(RequestMediaType.Audiobook, ".m4b"), plain, CancellationToken.None);
        var after = await verifier.VerifyAsync(Asset(RequestMediaType.Audiobook, ".m4b"), measured, CancellationToken.None);

        Assert.AreEqual(before.IsMatch, after.IsMatch);
        Assert.AreEqual(before.Facts?.Count, after.Facts?.Count);
        Assert.IsFalse((after.Facts ?? []).Any(fact => fact.Dimension == EvidenceDimension.AudioTechnical),
            "The identity verifier does not produce or consume audio measurements.");
    }
}
