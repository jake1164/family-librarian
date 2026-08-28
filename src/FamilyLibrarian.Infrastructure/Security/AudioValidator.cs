using System.Buffers.Binary;
using System.Text;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Acquisition;

namespace FamilyLibrarian.Infrastructure.Security;

/// <summary>
/// Structural validation for the audiobook container formats — the
/// <c>AudioValidator</c> named alongside <c>FileTypeValidator</c>/
/// <c>EpubValidator</c> in <c>docs/03-provider-api-contracts.md</c> §8.
/// </summary>
/// <remarks>
/// Structural only, matching <see cref="EpubValidator"/>'s scope: container
/// (M4B) or frame-header (MP3) integrity, not duration, bitstream decoding, or
/// bibliographic content — see
/// <c>family-librarian-deterministic-book-validation-plan.md</c>'s "next
/// smallest useful slice." No external tool dependency, for the same reason
/// <see cref="EpubValidator"/> hand-rolls its ZIP walk rather than taking a
/// package dependency: two formats do not justify one, and <c>ffprobe</c> is
/// explicitly a later-phase "potential check," not this slice's bar. Applies
/// only to the two supported audiobook extensions
/// (<see cref="FamilyLibrarian.Application.Acquisition.KnownFormatContentTypes"/>);
/// every other format is skipped, exactly like <see cref="EpubValidator"/>
/// skips every non-EPUB format.
/// </remarks>
public sealed class AudioValidator : IAssetValidator
{
    /// <summary>Bounds a small/crafted file claiming an implausible number of zero-payload top-level boxes.</summary>
    private const int MaxTopLevelBoxes = 100_000;

    private const int Id3HeaderSize = 10;

    /// <summary>Generous relative to any real encoder's ID3-tag-to-first-frame gap.</summary>
    private const int FrameSyncSearchWindowBytes = 64 * 1024;

    // ISO/IEC 11172-3 Table B.1: bitrate index -> kbit/s. Index 0 ("free") and
    // 15 ("bad") are both -1 here -- rejected uniformly by TryParseFrameHeader
    // since neither yields a computable fixed frame length. Row 0 is an unused
    // placeholder so layer numbers (1/2/3) can index directly.
    private static readonly int[][] BitrateKbpsMpeg1 =
    [
        [],
        [-1, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448, -1],
        [-1, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, -1],
        [-1, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, -1]
    ];

    private static readonly int[][] BitrateKbpsMpeg2 =
    [
        [],
        [-1, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256, -1],
        [-1, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, -1],
        [-1, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, -1]
    ];

    private static readonly int[] SampleRateHzMpeg1 = [44100, 48000, 32000];
    private static readonly int[] SampleRateHzMpeg2 = [22050, 24000, 16000];
    private static readonly int[] SampleRateHzMpeg25 = [11025, 12000, 8000];

    public string Id => "audio-structure";

    public Task<ValidationOutcome> ValidateAsync(
        MediaAsset asset, Stream content, CancellationToken cancellationToken) => asset.Format switch
    {
        var format when string.Equals(format, ".m4b", StringComparison.OrdinalIgnoreCase) =>
            ValidateM4bAsync(content, cancellationToken),
        var format when string.Equals(format, ".mp3", StringComparison.OrdinalIgnoreCase) =>
            ValidateMp3Async(content, cancellationToken),
        _ => Task.FromResult(new ValidationOutcome(true, null))
    };

    /// <summary>
    /// Walks the top-level ISO Base Media File Format (ISO/IEC 14496-12) box
    /// sequence and requires an <c>ftyp</c> and a <c>moov</c> box to both be
    /// present. Every box is skipped via <see cref="Stream.Seek"/>, never
    /// read — an <c>mdat</c> box's declared size can legitimately be
    /// gigabytes, and unlike <see cref="EpubValidator"/>'s read-based ZIP
    /// entry walk, nothing here needs to inspect payload bytes, so there is
    /// no per-entry byte cap to enforce, only a bound on box *count*.
    /// </summary>
    private static async Task<ValidationOutcome> ValidateM4bAsync(Stream content, CancellationToken cancellationToken)
    {
        var length = content.Length;
        content.Position = 0;

        var hasFtyp = false;
        var hasMoov = false;
        var boxCount = 0;
        var header = new byte[8];

        while (content.Position < length)
        {
            if (length - content.Position < 8)
            {
                return new ValidationOutcome(false, "The MP4 container is truncated.");
            }

            if (++boxCount > MaxTopLevelBoxes)
            {
                return new ValidationOutcome(false, "The MP4 container has too many top-level boxes.");
            }

            var boxStart = content.Position;
            await ReadExactAsync(content, header, cancellationToken);
            long declaredSize = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = Encoding.ASCII.GetString(header, 4, 4);
            var headerSize = 8L;

            if (declaredSize == 1)
            {
                if (length - content.Position < 8)
                {
                    return new ValidationOutcome(false, "The MP4 container is truncated.");
                }

                var largeSize = new byte[8];
                await ReadExactAsync(content, largeSize, cancellationToken);
                declaredSize = (long)BinaryPrimitives.ReadUInt64BigEndian(largeSize);
                headerSize = 16L;
            }
            else if (declaredSize == 0)
            {
                // "Extends to the end of the file" -- only meaningful for the
                // last box; the loop naturally terminates once Position reaches length.
                declaredSize = length - boxStart;
            }

            if (declaredSize < headerSize)
            {
                return new ValidationOutcome(false, $"The MP4 box '{type}' has an invalid size.");
            }

            var boxEnd = boxStart + declaredSize;
            if (boxEnd > length)
            {
                return new ValidationOutcome(false, $"The MP4 box '{type}' extends past the end of the file.");
            }

            if (type == "ftyp")
            {
                hasFtyp = true;
            }
            else if (type == "moov")
            {
                hasMoov = true;
            }

            content.Position = boxEnd;
        }

        if (!hasFtyp)
        {
            return new ValidationOutcome(false, "The MP4 container has no ftyp box.");
        }

        if (!hasMoov)
        {
            return new ValidationOutcome(false, "The MP4 container has no moov box.");
        }

        return new ValidationOutcome(true, null);
    }

    /// <summary>
    /// Skips a leading ID3v2 tag (its size field must be a valid syncsafe
    /// integer and fit within the file), then searches a bounded window for a
    /// structurally valid MPEG audio frame header whose declared length
    /// (ISO/IEC 11172-3's bitrate/sample-rate tables and frame-length
    /// formula) lands on a second structurally valid frame header -- a single
    /// 11-bit sync match alone is too weak a signal, since that bit pattern
    /// occurs often enough by chance in arbitrary binary data.
    /// </summary>
    private static async Task<ValidationOutcome> ValidateMp3Async(Stream content, CancellationToken cancellationToken)
    {
        var length = content.Length;
        content.Position = 0;

        var searchStart = 0L;
        if (length >= Id3HeaderSize)
        {
            var id3Header = new byte[Id3HeaderSize];
            await ReadExactAsync(content, id3Header, cancellationToken);
            if (id3Header[0] == (byte)'I' && id3Header[1] == (byte)'D' && id3Header[2] == (byte)'3')
            {
                for (var i = 6; i < 10; i++)
                {
                    if ((id3Header[i] & 0x80) != 0)
                    {
                        return new ValidationOutcome(false, "The ID3 tag size is not a valid syncsafe integer.");
                    }
                }

                var tagSize = (id3Header[6] << 21) | (id3Header[7] << 14) | (id3Header[8] << 7) | id3Header[9];
                var tagEnd = Id3HeaderSize + (long)tagSize;
                if (tagEnd > length)
                {
                    return new ValidationOutcome(false, "The ID3 tag size exceeds the file.");
                }

                searchStart = tagEnd;
            }
        }

        content.Position = searchStart;
        var windowSize = (int)Math.Min(length - searchStart, FrameSyncSearchWindowBytes);
        if (windowSize < 4)
        {
            return new ValidationOutcome(false, "No valid MPEG audio frame was found.");
        }

        var window = new byte[windowSize];
        await ReadExactAsync(content, window, cancellationToken);

        for (var offset = 0; offset <= windowSize - 4; offset++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!TryParseFrameHeader(window.AsSpan(offset, 4), out var frameLength))
            {
                continue;
            }

            var nextFramePosition = searchStart + offset + frameLength;
            if (nextFramePosition >= length)
            {
                // A short file with one structurally valid trailing frame is accepted.
                return new ValidationOutcome(true, null);
            }

            var nextOffsetInWindow = offset + frameLength;
            byte[] nextHeader;
            if (nextOffsetInWindow + 4 <= windowSize)
            {
                nextHeader = window[nextOffsetInWindow..(nextOffsetInWindow + 4)];
            }
            else
            {
                if (nextFramePosition + 4 > length)
                {
                    continue;
                }

                content.Position = nextFramePosition;
                nextHeader = new byte[4];
                await ReadExactAsync(content, nextHeader, cancellationToken);
            }

            if (TryParseFrameHeader(nextHeader, out _))
            {
                return new ValidationOutcome(true, null);
            }
        }

        return new ValidationOutcome(false, "No valid MPEG audio frame was found.");
    }

    private static bool TryParseFrameHeader(ReadOnlySpan<byte> header, out int frameLengthBytes)
    {
        frameLengthBytes = 0;
        if (header[0] != 0xFF || (header[1] & 0xE0) != 0xE0)
        {
            return false;
        }

        var versionId = (header[1] >> 3) & 0x03;
        var layerId = (header[1] >> 1) & 0x03;
        var bitrateIndex = (header[2] >> 4) & 0x0F;
        var sampleRateIndex = (header[2] >> 2) & 0x03;
        var padding = (header[2] >> 1) & 0x01;

        // versionId 1 and layerId 0 are reserved; sampleRateIndex 3 is
        // reserved; bitrateIndex 0 ("free") has no fixed frame length to
        // verify and 15 ("bad") is explicitly invalid.
        if (versionId == 1 || layerId == 0 || sampleRateIndex == 3 || bitrateIndex is 0 or 15)
        {
            return false;
        }

        var isMpeg1 = versionId == 3;
        var sampleRateHz = versionId switch
        {
            3 => SampleRateHzMpeg1[sampleRateIndex],
            2 => SampleRateHzMpeg2[sampleRateIndex],
            _ => SampleRateHzMpeg25[sampleRateIndex]
        };

        var layerNumber = layerId switch
        {
            3 => 1, // Layer I
            2 => 2, // Layer II
            _ => 3  // layerId == 1: Layer III
        };

        var bitrateTable = isMpeg1 ? BitrateKbpsMpeg1[layerNumber] : BitrateKbpsMpeg2[layerNumber];
        var bitrateKbps = bitrateTable[bitrateIndex];
        if (bitrateKbps <= 0)
        {
            return false;
        }

        frameLengthBytes = layerNumber switch
        {
            1 => (12 * bitrateKbps * 1000 / sampleRateHz + padding) * 4,
            2 => 144 * bitrateKbps * 1000 / sampleRateHz + padding,
            _ => (isMpeg1 ? 144 : 72) * bitrateKbps * 1000 / sampleRateHz + padding
        };

        return frameLengthBytes >= 4;
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(totalRead), cancellationToken);
            if (bytesRead == 0)
            {
                throw new EndOfStreamException();
            }

            totalRead += bytesRead;
        }
    }
}
