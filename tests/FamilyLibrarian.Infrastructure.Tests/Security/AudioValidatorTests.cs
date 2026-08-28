using System.Buffers.Binary;
using System.Text;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Security;

namespace FamilyLibrarian.Infrastructure.Tests.Security;

[TestClass]
public sealed class AudioValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    // MPEG1 Audio Layer III, bitrate index 9 (128 kbps), sample-rate index 0
    // (44100 Hz), no padding -- an arbitrary but structurally valid choice.
    private const int Mp3BitrateKbps = 128;
    private const int Mp3SampleRateHz = 44100;

    [TestMethod]
    public async Task AFormatOtherThanMp3OrM4bIsSkippedWithoutInspectingContent()
    {
        var validator = new AudioValidator();
        var asset = CreateAsset(".pdf");

        var outcome = await validator.ValidateAsync(
            asset, new MemoryStream("not audio at all"u8.ToArray()), CancellationToken.None);

        Assert.IsTrue(outcome.IsValid);
    }

    [TestMethod]
    public async Task AStructurallyValidM4bPasses()
    {
        var outcome = await ValidateM4b(BuildValidM4bBytes());

        Assert.IsTrue(outcome.IsValid, outcome.Message);
    }

    [TestMethod]
    public async Task ANonMp4StreamIsRejected()
    {
        var outcome = await ValidateM4b("plain text, not an mp4 container at all"u8.ToArray());

        Assert.IsFalse(outcome.IsValid);
    }

    [TestMethod]
    public async Task AnM4bWithNoFtypBoxIsRejected()
    {
        using var stream = new MemoryStream();
        WriteBox(stream, "moov", [0, 0, 0, 0]);
        WriteBox(stream, "mdat", [1, 2, 3, 4]);

        var outcome = await ValidateM4b(stream.ToArray());

        Assert.IsFalse(outcome.IsValid);
        StringAssert.Contains(outcome.Message, "ftyp");
    }

    [TestMethod]
    public async Task AnM4bWithNoMoovBoxIsRejected()
    {
        using var stream = new MemoryStream();
        WriteBox(stream, "ftyp", BuildFtypPayload());
        WriteBox(stream, "mdat", [1, 2, 3, 4]);

        var outcome = await ValidateM4b(stream.ToArray());

        Assert.IsFalse(outcome.IsValid);
        StringAssert.Contains(outcome.Message, "moov");
    }

    [TestMethod]
    public async Task AnM4bBoxThatOverrunsTheFileIsRejected()
    {
        using var stream = new MemoryStream();
        WriteBox(stream, "ftyp", BuildFtypPayload());

        // A moov box that declares a size far larger than any bytes that follow.
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, 999_999);
        Encoding.ASCII.GetBytes("moov").CopyTo(header[4..]);
        stream.Write(header);
        stream.Write([0, 0, 0, 0]);

        var outcome = await ValidateM4b(stream.ToArray());

        Assert.IsFalse(outcome.IsValid);
        StringAssert.Contains(outcome.Message, "moov");
    }

    [TestMethod]
    public async Task AStructurallyValidMp3Passes()
    {
        using var stream = new MemoryStream();
        stream.Write(BuildMp3Frame(0xAA));
        stream.Write(BuildMp3Frame(0xBB));

        var outcome = await ValidateMp3(stream.ToArray());

        Assert.IsTrue(outcome.IsValid, outcome.Message);
    }

    [TestMethod]
    public async Task AStructurallyValidMp3WithALeadingId3TagPasses()
    {
        using var stream = new MemoryStream();
        stream.Write(BuildId3Header(tagSize: 0));
        stream.Write(BuildMp3Frame(0xAA));
        stream.Write(BuildMp3Frame(0xBB));

        var outcome = await ValidateMp3(stream.ToArray());

        Assert.IsTrue(outcome.IsValid, outcome.Message);
    }

    [TestMethod]
    public async Task NonAudioGarbageIsRejected()
    {
        var garbage = Encoding.ASCII.GetBytes(new string('x', 4096));

        var outcome = await ValidateMp3(garbage);

        Assert.IsFalse(outcome.IsValid);
        StringAssert.Contains(outcome.Message, "No valid MPEG audio frame");
    }

    [TestMethod]
    public async Task AnId3TagWithANonSyncsafeSizeIsRejected()
    {
        var id3Header = BuildId3Header(tagSize: 0);
        id3Header[7] |= 0x80; // Sets the high bit a syncsafe integer must never set.

        using var stream = new MemoryStream();
        stream.Write(id3Header);
        stream.Write(BuildMp3Frame(0xAA));

        var outcome = await ValidateMp3(stream.ToArray());

        Assert.IsFalse(outcome.IsValid);
        StringAssert.Contains(outcome.Message, "syncsafe");
    }

    [TestMethod]
    public async Task AnId3TagClaimingMoreBytesThanTheFileIsRejected()
    {
        var id3Header = BuildId3Header(tagSize: 10_000);

        using var stream = new MemoryStream();
        stream.Write(id3Header);
        stream.Write(BuildMp3Frame(0xAA));

        var outcome = await ValidateMp3(stream.ToArray());

        Assert.IsFalse(outcome.IsValid);
        StringAssert.Contains(outcome.Message, "ID3 tag size exceeds");
    }

    [TestMethod]
    public async Task AValidFirstFrameFollowedByGarbageInsteadOfASecondFrameIsRejected()
    {
        // A single 11-bit sync match is too weak on its own -- it occurs by
        // chance often enough in arbitrary binary data. The chain-to-a-second-
        // frame check exists precisely to catch this: a real-looking first
        // frame with unrelated bytes after it.
        using var stream = new MemoryStream();
        stream.Write(BuildMp3Frame(0xAA));
        stream.Write(Encoding.ASCII.GetBytes(new string('z', 4096)));

        var outcome = await ValidateMp3(stream.ToArray());

        Assert.IsFalse(outcome.IsValid);
    }

    private static Task<ValidationOutcome> ValidateM4b(byte[] content) => Validate(content, ".m4b");

    private static Task<ValidationOutcome> ValidateMp3(byte[] content) => Validate(content, ".mp3");

    private static Task<ValidationOutcome> Validate(byte[] content, string format)
    {
        var validator = new AudioValidator();
        var asset = CreateAsset(format);
        return validator.ValidateAsync(asset, new MemoryStream(content), CancellationToken.None);
    }

    private static MediaAsset CreateAsset(string format) => new(
        Guid.NewGuid(),
        editionId: null,
        RequestMediaType.Audiobook,
        format,
        "book" + format,
        $"{Guid.NewGuid():N}{format}",
        sizeBytes: 1024,
        sha256: new string('a', 64),
        detectedMimeType: "audio/mpeg",
        Guid.NewGuid(),
        sourceAcquisitionCandidateId: null,
        Now);

    private static byte[] BuildValidM4bBytes()
    {
        using var stream = new MemoryStream();
        WriteBox(stream, "ftyp", BuildFtypPayload());
        WriteBox(stream, "moov", [0, 0, 0, 0]);
        WriteBox(stream, "mdat", [1, 2, 3, 4]);
        return stream.ToArray();
    }

    private static byte[] BuildFtypPayload() =>
        [(byte)'M', (byte)'4', (byte)'B', (byte)' ', 0, 0, 0, 0, (byte)'i', (byte)'s', (byte)'o', (byte)'m'];

    private static void WriteBox(Stream stream, string type, byte[] payload)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)(8 + payload.Length));
        Encoding.ASCII.GetBytes(type).CopyTo(header[4..]);
        stream.Write(header);
        stream.Write(payload);
    }

    /// <summary>
    /// One structurally valid MPEG1 Audio Layer III frame header (bitrate
    /// index 9 / 128 kbps, sample-rate index 0 / 44100 Hz, no padding) padded
    /// to its correct declared frame length with <paramref name="fillByte"/>.
    /// </summary>
    private static byte[] BuildMp3Frame(byte fillByte)
    {
        var frameLength = 144 * Mp3BitrateKbps * 1000 / Mp3SampleRateHz;
        var frame = new byte[frameLength];
        frame[0] = 0xFF;
        frame[1] = 0xFB; // sync(3)=111, version(2)=11 (MPEG1), layer(2)=01 (Layer III), protection=1
        frame[2] = 9 << 4; // bitrate index 9, sample-rate index 0, no padding
        frame[3] = 0x00;
        for (var i = 4; i < frame.Length; i++)
        {
            frame[i] = fillByte;
        }

        return frame;
    }

    private static byte[] BuildId3Header(int tagSize)
    {
        var header = new byte[10];
        header[0] = (byte)'I';
        header[1] = (byte)'D';
        header[2] = (byte)'3';
        header[3] = 3; // Major version (irrelevant to this validator).
        header[4] = 0; // Revision.
        header[5] = 0; // Flags.
        header[6] = (byte)((tagSize >> 21) & 0x7F);
        header[7] = (byte)((tagSize >> 14) & 0x7F);
        header[8] = (byte)((tagSize >> 7) & 0x7F);
        header[9] = (byte)(tagSize & 0x7F);
        return header;
    }
}
