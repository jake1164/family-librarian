using System.Buffers.Binary;
using System.Text;
using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Evidence;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Security;
using static FamilyLibrarian.Infrastructure.Tests.Characterization.CharacterizationFixtures;

namespace FamilyLibrarian.Infrastructure.Tests.Security;

/// <summary>
/// AUTOFIND 3b: Vorbis comments of an Ogg Vorbis or Ogg Opus audiobook follow the same identity rules as ID3 tags. Ogg bytes
/// are built in the test; no binary fixture is committed.
/// </summary>
[TestClass]
public sealed class OggAssetIdentityVerifierTests
{
    private const string GermanBlurb =
        "Der ehemalige Navy SEAL James Reece kehrt nach einem Hinterhalt zurück, der sein gesamtes Team das Leben kostete. " +
        "Er will wissen, wer ihn verraten hat und warum es dazu kam. Das ist die Geschichte von einem Mann, " +
        "der nicht aufgibt und mit allen Mitteln für die Wahrheit kämpft.";

    // ---- parsing -------------------------------------------------------------------------------------------------

    [TestMethod]
    public void VorbisCommentsAreReadWithCaseInsensitiveKeys()
    {
        var tags = OggCommentReader.Parse(Vorbis("album=The Terminal List", "Artist=Jack Carr", "TITLE=Chapter 1", "LANGUAGE=eng",
            "GENRE=Thriller", "DESCRIPTION=A story"));

        Assert.IsNotNull(tags);
        Assert.AreEqual("vorbis-comments", tags.Source);
        Assert.AreEqual("The Terminal List", tags.Album);
        Assert.AreEqual("Chapter 1", tags.Title);
        AssertValues(tags.AuthorCandidates, "Jack Carr");
        AssertValues(tags.Languages, "eng");
        Assert.AreEqual("Thriller", tags.Genre);
        Assert.AreEqual("A story", tags.Comment);
    }

    [TestMethod]
    public void OpusTagsAreRead()
    {
        var tags = OggCommentReader.Parse(Opus("ALBUM=The Terminal List", "ALBUMARTIST=Jack Carr", "COMPOSER=Someone"));

        Assert.IsNotNull(tags);
        Assert.AreEqual("opus-tags", tags.Source);
        Assert.AreEqual("The Terminal List", tags.Album);
        Assert.HasCount(2, tags.AuthorCandidates);
    }

    [TestMethod]
    public void ACommentPacketSpanningTwoPagesIsReassembled()
    {
        var comments = new[] { "ALBUM=The Terminal List", "ARTIST=Jack Carr", "DESCRIPTION=" + new string('x', 700) };
        var packet = VorbisCommentPacket(comments);
        // Split after 3 x 255 bytes: those three segments all say "the packet continues".
        var first = packet[..765];
        var second = packet[765..];
        var identification = Concat([0x01], "vorbis"u8.ToArray(), new byte[23]);
        var stream = Concat(
            Page(1, 0, 0x02, [(byte)identification.Length], identification),
            Page(1, 1, 0x00, [255, 255, 255], first),
            Page(1, 2, 0x01, Lacing(second.Length), second));

        var tags = OggCommentReader.Parse(stream);

        Assert.IsNotNull(tags);
        Assert.AreEqual("The Terminal List", tags.Album);
        AssertValues(tags.AuthorCandidates, "Jack Carr");
    }

    [TestMethod]
    public void OnlyTheFirstLogicalStreamIsRead()
    {
        var identification = Concat([0x01], "vorbis"u8.ToArray(), new byte[23]);
        var otherComments = VorbisCommentPacket(["ALBUM=Another Book"]);
        var ownComments = VorbisCommentPacket(["ALBUM=The Terminal List"]);
        var stream = Concat(
            Page(7, 0, 0x02, [(byte)identification.Length], identification),
            Page(9, 0, 0x02, [(byte)identification.Length], identification),
            Page(9, 1, 0x00, Lacing(otherComments.Length), otherComments),
            Page(7, 1, 0x00, Lacing(ownComments.Length), ownComments));

        Assert.AreEqual("The Terminal List", OggCommentReader.Parse(stream)?.Album);
    }

    [TestMethod]
    public void AnOggStreamThatIsNeitherVorbisNorOpusHasNoTags()
    {
        var flac = Concat([0x7F], "FLAC"u8.ToArray(), new byte[20]);
        var stream = Concat(Page(1, 0, 0x02, [(byte)flac.Length], flac), Page(1, 1, 0x00, [(byte)flac.Length], flac));

        Assert.IsNull(OggCommentReader.Parse(stream));
    }

    // ---- hostile input -------------------------------------------------------------------------------------------

    [TestMethod]
    public void ACommentClaimingFourGibibytesEndsTheReadWithoutAllocatingIt()
    {
        var packet = Concat(VorbisHeader(), LittleEndian(0), LittleEndian(2), LittleEndian(uint.MaxValue), "ALBUM=x"u8.ToArray());
        var tags = OggCommentReader.Parse(BuildStream(packet));

        Assert.IsNull(tags);
    }

    [TestMethod]
    public void AVendorLengthBeyondThePacketIsNoTag()
    {
        var packet = Concat(VorbisHeader(), LittleEndian(0x7FFFFFFF), "vendor"u8.ToArray());

        Assert.IsNull(OggCommentReader.Parse(BuildStream(packet)));
    }

    [TestMethod]
    public void ACommentCountOfFourBillionIsBoundedToTwoHundred()
    {
        var entries = Enumerable.Range(0, 300).Select(index => Comment($"TITLE=t{index}")).ToArray();
        var packet = Concat(VorbisHeader(), LittleEndian(0), LittleEndian(uint.MaxValue), Concat(entries));

        var tags = OggCommentReader.Parse(BuildStream(packet));

        Assert.IsNotNull(tags);
        Assert.AreEqual("t0", tags.Title);
    }

    [TestMethod]
    public void AnOversizedCommentIsSkippedAndTheNextOneIsStillRead()
    {
        var packet = Concat(VorbisHeader(), LittleEndian(0), LittleEndian(2),
            Comment("DESCRIPTION=" + new string('x', 5000)), Comment("ALBUM=The Terminal List"));

        Assert.AreEqual("The Terminal List", OggCommentReader.Parse(BuildStream(packet))?.Album);
        Assert.IsNull(OggCommentReader.Parse(BuildStream(packet))?.Comment);
    }

    [TestMethod]
    public void ATruncatedPageIsNoTag()
    {
        var whole = Vorbis("ALBUM=The Terminal List");

        Assert.IsNull(OggCommentReader.Parse(whole[..(whole.Length - 6)]));
    }

    [TestMethod]
    public async Task RandomBytesNeverThrow()
    {
        var random = new Random(11);
        for (var run = 0; run < 200; run++)
        {
            var bytes = new byte[random.Next(0, 2048)];
            random.NextBytes(bytes);
            if (run % 2 == 0 && bytes.Length > 40)
            {
                "OggS"u8.CopyTo(bytes);
                bytes[4] = 0;
            }

            await using var stream = new MemoryStream(bytes);
            await OggCommentReader.ReadAsync(stream, CancellationToken.None);
        }
    }

    // ---- verification --------------------------------------------------------------------------------------------

    [TestMethod]
    [DataRow(".ogg", true)]
    [DataRow(".oga", true)]
    [DataRow(".OPUS", true)]
    [DataRow(".mp3", false)]
    [DataRow(".m4b", false)]
    public void OnlyOggFamilyFilesAreSupported(string format, bool supported)
    {
        Assert.AreEqual(supported, CreateVerifier().Supports(Asset(RequestMediaType.Audiobook, format)));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MatchingAlbumAndAuthorClearAnExternalFile(bool opus)
    {
        var bytes = opus
            ? Opus("ALBUM=The Terminal List", "ARTIST=Jack Carr", "LANGUAGE=eng")
            : Vorbis("ALBUM=The Terminal List", "ARTIST=Jack Carr", "LANGUAGE=eng");

        var result = await VerifyAsync(bytes, AssetOrigin.ExternalProvider);

        Assert.IsTrue(result.IsMatch);
        Assert.AreEqual("ogg-vorbis-comments", result.VerifierId);
        Assert.AreEqual(opus ? "opus-tags" : "vorbis-comments", result.Facts.Single(fact => fact.FindingId == "ev-tag-source").Value);
    }

    [TestMethod]
    public async Task ADeclaredGermanLanguageIsUnmatched()
    {
        var result = await VerifyAsync(Vorbis("ALBUM=The Terminal List", "ARTIST=Jack Carr", "LANGUAGE=ger"), AssetOrigin.ExternalProvider);

        Assert.IsFalse(result.IsMatch);
        Assert.IsTrue(result.Facts.Any(fact => fact.Dimension == EvidenceDimension.Language && fact.Polarity == EvidencePolarity.Negative));
    }

    [TestMethod]
    public async Task AGermanDescriptionWithNoLanguageTagPreventsClearing()
    {
        var bytes = Vorbis("ALBUM=The Terminal List - Die Abschussliste", "ARTIST=Jack Carr", "GENRE=Spionage-Thriller", "DESCRIPTION=" + GermanBlurb);

        foreach (var origin in new[] { AssetOrigin.ExternalProvider, AssetOrigin.BuiltInProvider })
        {
            var asset = AssetFor(origin);
            var result = await VerifyAsync(bytes, origin, asset);

            Assert.AreEqual(ArtifactEvidenceRecorder.NoVerifierId, result.VerifierId);
            Assert.AreEqual(NoVerifierOutcome.For(asset).IsMatch, result.IsMatch);
            Assert.AreEqual("inferred:de", result.Facts.Single(fact => fact.FindingId == "ev-language-inferred").Value);
        }
    }

    [TestMethod]
    public async Task ADifferentAlbumIsAContradiction()
    {
        var result = await VerifyAsync(Vorbis("ALBUM=Fourth Wing", "ARTIST=Jack Carr"), AssetOrigin.ExternalProvider);

        Assert.IsFalse(result.IsMatch);
        Assert.IsTrue(result.Facts.Any(fact => fact.FindingId == "ev-title" && fact.Polarity == EvidencePolarity.Negative));
    }

    [TestMethod]
    public async Task ATrackTitleAloneNeverClears()
    {
        var result = await VerifyAsync(Vorbis("TITLE=Chapter 01", "ARTIST=Jack Carr"), AssetOrigin.ExternalProvider);

        Assert.AreEqual(ArtifactEvidenceRecorder.NoVerifierId, result.VerifierId);
        Assert.AreEqual(EvidencePolarity.Unknown, result.Facts.Single(fact => fact.FindingId == "ev-title").Polarity);
    }

    [TestMethod]
    public async Task AFileWithNoCommentsReturnsExactlyTheNoVerifierOutcome()
    {
        foreach (var origin in new[] { AssetOrigin.BuiltInProvider, AssetOrigin.ExternalProvider })
        {
            var asset = AssetFor(origin);
            var result = await VerifyAsync(Vorbis(), origin, asset);
            var expected = NoVerifierOutcome.For(asset);

            Assert.AreEqual(expected.IsMatch, result.IsMatch);
            Assert.AreEqual(expected.VerifierId, result.VerifierId);
            Assert.AreEqual(EvidenceReasonCodes.ArtifactIdentityCorroboration.TagsAbsent, result.Facts.Single().Code);
        }
    }

    [TestMethod]
    public async Task ANonOggFileNamedOggReturnsTheNoVerifierOutcomeAndNeverThrows()
    {
        var result = await VerifyAsync(new byte[2048], AssetOrigin.ExternalProvider);

        Assert.AreEqual(ArtifactEvidenceRecorder.NoVerifierId, result.VerifierId);
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    private static void AssertValues(IEnumerable<string> actual, params string[] expected) =>
        CollectionAssert.AreEqual(expected, actual.ToArray());

    private static OggAssetIdentityVerifier CreateVerifier() =>
        new(new Lookup(), new DeterministicBookMatcher(), new NoRequestStore());

    private static MediaAsset AssetFor(AssetOrigin origin) => new(
        Guid.NewGuid(), null, RequestMediaType.Audiobook, ".ogg", "book.ogg", "stored.ogg", 1, new string('a', 64), "audio/ogg",
        Guid.NewGuid(), null, DateTimeOffset.UtcNow, identityPreConfirmed: true, origin: origin);

    private static Task<AssetIdentityVerificationResult> VerifyAsync(byte[] bytes, AssetOrigin origin, MediaAsset? asset = null) =>
        CreateVerifier().VerifyAsync(asset ?? AssetFor(origin), new MemoryStream(bytes), CancellationToken.None);

    private sealed class Lookup : IWorkLookup
    {
        public Task<WorkSummary?> FindAsync(Guid workId, CancellationToken cancellationToken) =>
            Task.FromResult<WorkSummary?>(new WorkSummary(workId, "The Terminal List", "Jack Carr", []));
    }

    private static byte[] Vorbis(params string[] comments) => BuildStream(VorbisCommentPacket(comments));

    private static byte[] Opus(params string[] comments) =>
        BuildStream(Concat("OpusTags"u8.ToArray(), CommentBlock(comments)), opus: true);

    private static byte[] VorbisHeader() => Concat([0x03], "vorbis"u8.ToArray());

    private static byte[] VorbisCommentPacket(string[] comments) => Concat(VorbisHeader(), CommentBlock(comments));

    /// <summary>Vendor string, comment count, then the length-prefixed comments.</summary>
    private static byte[] CommentBlock(string[] comments) =>
        Concat(LittleEndian(6), "vendor"u8.ToArray(), LittleEndian((uint)comments.Length), Concat(comments.Select(Comment).ToArray()));

    private static byte[] Comment(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return Concat(LittleEndian((uint)bytes.Length), bytes);
    }

    private static byte[] BuildStream(byte[] commentPacket, bool opus = false)
    {
        var identification = opus
            ? Concat("OpusHead"u8.ToArray(), new byte[11])
            : Concat([0x01], "vorbis"u8.ToArray(), new byte[23]);
        return Concat(
            Page(1, 0, 0x02, [(byte)identification.Length], identification),
            Page(1, 1, 0x00, Lacing(commentPacket.Length), commentPacket));
    }

    /// <summary>The lacing values for one complete packet: 255 for each full segment, then the remainder (0 when exact).</summary>
    private static byte[] Lacing(int length)
    {
        var segments = new List<byte>();
        while (length >= 255)
        {
            segments.Add(255);
            length -= 255;
        }

        segments.Add((byte)length);
        return [.. segments];
    }

    private static byte[] Page(uint serial, uint sequence, byte headerType, byte[] lacing, byte[] data)
    {
        var header = new byte[27];
        "OggS"u8.CopyTo(header);
        header[5] = headerType;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(14), serial);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(18), sequence);
        header[26] = (byte)lacing.Length;
        return Concat(header, lacing, data);
    }

    private static byte[] LittleEndian(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(part => part).ToArray();
}
