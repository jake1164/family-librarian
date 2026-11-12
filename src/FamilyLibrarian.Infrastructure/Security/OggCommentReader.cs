using System.Buffers.Binary;
using System.Text;

namespace FamilyLibrarian.Infrastructure.Security;

/// <summary>
/// Reads the Vorbis comment header of an Ogg Vorbis or Ogg Opus stream. Hand-written and bounded like
/// <see cref="Id3TagReader"/>: at most the first 256 KiB of the file is read, the comment packet is reassembled across Ogg
/// pages up to 64 KiB, at most 200 comments are read and each is limited to 4 KiB. No audio is decoded. Hostile or truncated
/// input yields <see langword="null"/> or the comments read before the defect, never an exception.
/// </summary>
internal static class OggCommentReader
{
    internal const int MaxReadBytes = 256 * 1024;
    internal const int MaxPacketBytes = 64 * 1024;
    internal const int MaxComments = 200;
    internal const int MaxCommentBytes = 4096;
    private const int MaxPages = 256;

    private static readonly byte[] VorbisIdentification = [0x01, (byte)'v', (byte)'o', (byte)'r', (byte)'b', (byte)'i', (byte)'s'];
    private static readonly byte[] VorbisComment = [0x03, (byte)'v', (byte)'o', (byte)'r', (byte)'b', (byte)'i', (byte)'s'];
    private static readonly byte[] OpusHead = "OpusHead"u8.ToArray();
    private static readonly byte[] OpusTags = "OpusTags"u8.ToArray();

    public static async Task<AudioTags?> ReadAsync(Stream content, CancellationToken cancellationToken)
    {
        if (!content.CanSeek)
        {
            return null;
        }

        try
        {
            content.Position = 0;
            var buffer = new byte[(int)Math.Min(content.Length, MaxReadBytes)];
            await content.ReadExactlyAsync(buffer, cancellationToken);
            return Parse(buffer);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static AudioTags? Parse(byte[] buffer)
    {
        var packets = ReadFirstTwoPackets(buffer);
        if (packets is not { Count: 2 })
        {
            return null;
        }

        string source;
        int headerLength;
        if (packets[0].AsSpan().StartsWith(VorbisIdentification) && packets[1].AsSpan().StartsWith(VorbisComment))
        {
            (source, headerLength) = ("vorbis-comments", VorbisComment.Length);
        }
        else if (packets[0].AsSpan().StartsWith(OpusHead) && packets[1].AsSpan().StartsWith(OpusTags))
        {
            (source, headerLength) = ("opus-tags", OpusTags.Length);
        }
        else
        {
            return null;
        }

        var comments = ReadComments(packets[1], headerLength);
        string? title = null, album = null, genre = null, comment = null;
        var authors = new List<string>();
        var languages = new List<string>();
        var profile = new AudioProfileTags();
        foreach (var entry in comments)
        {
            var separator = entry.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var key = entry[..separator].Trim().ToUpperInvariant();
            var value = entry[(separator + 1)..].Replace("\0", string.Empty, StringComparison.Ordinal).Trim();
            if (value.Length == 0)
            {
                continue;
            }

            switch (key)
            {
                case "SERIES" or "SERIES-PART" or "ISBN" or "ASIN": profile.Add(key, value); break;
                case "TITLE": title ??= value; break;
                case "ALBUM": album ??= value; break;
                case "ARTIST" or "ALBUMARTIST" or "COMPOSER" or "AUTHOR": authors.Add(value); break;
                case "LANGUAGE": languages.Add(value); break;
                case "GENRE": genre ??= value; break;
                case "COMMENT" or "DESCRIPTION": comment ??= value; break;
            }
        }

        var facts = profile.Facts();
        var tags = new AudioTags(source, title, album, authors, languages, genre, comment, facts);
        return title is null && album is null && authors.Count == 0 && languages.Count == 0 && genre is null && comment is null && facts.Count == 0
            ? null
            : tags;
    }

    /// <summary>Packets 1 and 2 of the first logical stream, reassembled across pages. Fewer when the data ends first.</summary>
    private static List<byte[]>? ReadFirstTwoPackets(byte[] buffer)
    {
        var packets = new List<byte[]>();
        var current = new List<byte>();
        uint? serial = null;
        var position = 0;
        for (var page = 0; page < MaxPages && packets.Count < 2; page++)
        {
            if (position + 27 > buffer.Length || buffer[position] != (byte)'O' || buffer[position + 1] != (byte)'g' ||
                buffer[position + 2] != (byte)'g' || buffer[position + 3] != (byte)'S' || buffer[position + 4] != 0)
            {
                break;
            }

            var pageSerial = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(position + 14, 4));
            var segmentCount = buffer[position + 26];
            var tableStart = position + 27;
            if (tableStart + segmentCount > buffer.Length)
            {
                break;
            }

            var dataLength = 0;
            for (var segment = 0; segment < segmentCount; segment++) dataLength += buffer[tableStart + segment];
            var dataStart = tableStart + segmentCount;
            if (dataStart + dataLength > buffer.Length)
            {
                break;
            }

            serial ??= pageSerial;
            if (pageSerial == serial)
            {
                var offset = dataStart;
                for (var segment = 0; segment < segmentCount && packets.Count < 2; segment++)
                {
                    var length = buffer[tableStart + segment];
                    if (current.Count < MaxPacketBytes)
                    {
                        current.AddRange(buffer.AsSpan(offset, Math.Min(length, MaxPacketBytes - current.Count)).ToArray());
                    }

                    offset += length;
                    if (length < 255)
                    {
                        packets.Add([.. current]);
                        current.Clear();
                    }
                }
            }

            position = dataStart + dataLength;
        }

        return packets;
    }

    private static List<string> ReadComments(byte[] packet, int headerLength)
    {
        var entries = new List<string>();
        var position = headerLength;
        if (position + 4 > packet.Length)
        {
            return entries;
        }

        var vendorLength = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(position, 4));
        position += 4;
        if (vendorLength > packet.Length - position)
        {
            return entries;
        }

        position += (int)vendorLength;
        if (position + 4 > packet.Length)
        {
            return entries;
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(position, 4));
        position += 4;
        for (var index = 0u; index < Math.Min(count, MaxComments) && position + 4 <= packet.Length; index++)
        {
            var length = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(position, 4));
            position += 4;
            if (length > packet.Length - position)
            {
                break; // The comment does not fit: truncated or hostile; nothing after it can be trusted.
            }

            if (length <= MaxCommentBytes)
            {
                entries.Add(Encoding.UTF8.GetString(packet, position, (int)length));
            }

            position += (int)length;
        }

        return entries;
    }
}
