using System.Buffers.Binary;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Evidence;

namespace FamilyLibrarian.Infrastructure.Security;

/// <summary>
/// Measures an MP3 from its first frame (alpha.6 M1-4c): codec, bitrate and duration. The duration is exact when the file
/// carries a Xing/Info or VBRI frame count and otherwise an estimate assuming constant bitrate, labeled as such. Reads one
/// bounded window; never decodes audio.
/// </summary>
public sealed class Mp3TechnicalInspector : IAssetTechnicalInspector
{
    private const int Id3HeaderSize = 10;
    private const int SearchWindowBytes = 64 * 1024;

    public string Id => "mp3-headers";

    public bool Supports(MediaAsset asset) => string.Equals(asset.Format, ".mp3", StringComparison.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<IdentityFact>> InspectAsync(MediaAsset asset, Stream content, CancellationToken cancellationToken)
    {
        var length = content.Length;
        content.Position = 0;
        var audioStart = 0L;
        if (length >= Id3HeaderSize)
        {
            var id3 = new byte[Id3HeaderSize];
            await content.ReadExactlyAsync(id3, cancellationToken);
            if (id3[0] == (byte)'I' && id3[1] == (byte)'D' && id3[2] == (byte)'3' && (id3[6] | id3[7] | id3[8] | id3[9]) < 0x80)
            {
                audioStart = Id3HeaderSize + ((id3[6] << 21) | (id3[7] << 14) | (id3[8] << 7) | id3[9]);
            }
        }

        if (audioStart >= length)
        {
            return Unavailable();
        }

        content.Position = audioStart;
        var window = new byte[(int)Math.Min(length - audioStart, SearchWindowBytes)];
        await content.ReadExactlyAsync(window, cancellationToken);

        for (var offset = 0; offset + 4 <= window.Length; offset++)
        {
            if (!AudioValidator.TryDescribeFrameHeader(window.AsSpan(offset, 4), out var frame))
            {
                continue;
            }

            // Same confidence rule as the structural check: a second valid header right after, or the end of the file.
            var next = offset + frame.FrameLengthBytes;
            if (next + 4 <= window.Length && !AudioValidator.TryDescribeFrameHeader(window.AsSpan(next, 4), out _))
            {
                continue;
            }

            return Describe(frame, window.AsSpan(offset), length - audioStart);
        }

        return Unavailable();
    }

    private static IReadOnlyList<IdentityFact> Describe(AudioValidator.Mp3FrameInfo frame, ReadOnlySpan<byte> frameBytes, long audioBytes)
    {
        var (frames, vbr) = FrameCountFromHeader(frame, frameBytes);
        double seconds;
        string basis;
        if (frames is { } counted && counted > 0)
        {
            seconds = (double)counted * frame.SamplesPerFrame / frame.SampleRateHz;
            basis = "frame_count_header";
        }
        else
        {
            seconds = audioBytes * 8d / (frame.BitrateKbps * 1000d);
            basis = "cbr_estimate";
        }

        return
        [
            Fact("ev-audio-duration", EvidenceReasonCodes.ArtifactAudio.DurationMeasured, $"seconds={Math.Round(seconds)};basis={basis}"),
            Fact("ev-audio-codec", EvidenceReasonCodes.ArtifactAudio.Codec,
                $"mp3;{frame.MpegVersion};layer{frame.Layer};sample_rate_hz={frame.SampleRateHz}"),
            Fact("ev-audio-bitrate", EvidenceReasonCodes.ArtifactAudio.Bitrate,
                $"kbps={frame.BitrateKbps};mode={(vbr ? "vbr_header" : "first_frame")}")
        ];
    }

    /// <summary>Xing/Info (at the side-info offset) or VBRI (fixed offset 36) frame count, when present.</summary>
    private static (uint? Frames, bool Vbr) FrameCountFromHeader(AudioValidator.Mp3FrameInfo frame, ReadOnlySpan<byte> bytes)
    {
        var sideInfo = frame.MpegVersion == "mpeg1" ? (frame.IsMono ? 17 : 32) : (frame.IsMono ? 9 : 17);
        var xing = 4 + sideInfo;
        if (bytes.Length >= xing + 12 && (bytes.Slice(xing, 4).SequenceEqual("Xing"u8) || bytes.Slice(xing, 4).SequenceEqual("Info"u8)))
        {
            var isVbr = bytes.Slice(xing, 4).SequenceEqual("Xing"u8);
            return (BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(xing + 4, 4)) & 1) != 0
                ? (BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(xing + 8, 4)), isVbr)
                : (null, isVbr);
        }

        return bytes.Length >= 50 && bytes.Slice(36, 4).SequenceEqual("VBRI"u8)
            ? (BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(50, 4)), true)
            : (null, false);
    }

    private static IdentityFact Fact(string id, string code, string value) =>
        new(id, EvidenceDimension.AudioTechnical, code, EvidencePolarity.Unknown, value);

    private static IReadOnlyList<IdentityFact> Unavailable() =>
        [new IdentityFact("ev-audio-duration", EvidenceDimension.AudioTechnical,
            EvidenceReasonCodes.ArtifactAudio.DurationUnavailable, EvidencePolarity.Unknown, "no readable MPEG frame header")];
}
