using System.Buffers.Binary;
using System.Text;

namespace FamilyLibrarian.Infrastructure.Security;

/// <summary>The identity-relevant values of an MP3's tag. Every value is bounded text; nothing here is audio.</summary>
internal sealed record Id3Tags(
    string Source,
    string? Title,
    string? Album,
    IReadOnlyList<string> Artists,
    IReadOnlyList<string> AlbumArtists,
    IReadOnlyList<string> Composers,
    IReadOnlyList<string> Languages,
    string? Genre,
    string? Comment)
{
    /// <summary>Artist, album artist and composer: any of them may carry the author.</summary>
    public IEnumerable<string> AuthorCandidates => AlbumArtists.Concat(Artists).Concat(Composers);

    public AudioTags ToAudioTags() => new(Source, Title, Album, AuthorCandidates.ToArray(), Languages, Genre, Comment);

    public bool HasAnything =>
        Title is not null || Album is not null || Artists.Count > 0 || AlbumArtists.Count > 0 ||
        Composers.Count > 0 || Languages.Count > 0 || Genre is not null || Comment is not null;
}

/// <summary>
/// Reads title, artist, album, language, genre and comment from an MP3's ID3v2 (2.2, 2.3, 2.4) or ID3v1 tag. Hand-written
/// and bounded like <see cref="Mp3TechnicalInspector"/>: the tag is read as one block of at most 1 MiB, only a closed set of
/// text frames is decoded (each at most 4 KiB), every other frame (including embedded pictures) is skipped by its size, and
/// no audio is decoded. Hostile or truncated input yields <see langword="null"/> or what was read before the defect, never
/// an exception.
/// </summary>
internal static class Id3TagReader
{
    internal const int MaxTagBytes = 1024 * 1024;
    internal const int MaxTextFrameBytes = 4096;
    private const int MaxFrames = 4096;
    private const int HeaderBytes = 10;
    private const int V1Bytes = 128;

    public static async Task<Id3Tags?> ReadAsync(Stream content, CancellationToken cancellationToken)
    {
        if (!content.CanSeek)
        {
            return null;
        }

        try
        {
            return await ReadV2Async(content, cancellationToken) is { HasAnything: true } v2
                ? v2
                : await ReadV1Async(content, cancellationToken);
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

    private static async Task<Id3Tags?> ReadV2Async(Stream content, CancellationToken cancellationToken)
    {
        if (content.Length < HeaderBytes)
        {
            return null;
        }

        content.Position = 0;
        var header = new byte[HeaderBytes];
        await content.ReadExactlyAsync(header, cancellationToken);
        if (header[0] != (byte)'I' || header[1] != (byte)'D' || header[2] != (byte)'3')
        {
            return null;
        }

        var major = header[3];
        var flags = header[5];
        if (major is < 2 or > 4 || !TrySynchsafe(header.AsSpan(6, 4), out var size) ||
            size == 0 || size > MaxTagBytes || HeaderBytes + (long)size > content.Length)
        {
            return null;
        }

        // ID3v2.2 compression is unsupported by every real tagger; refuse the whole tag rather than misread it.
        if (major == 2 && (flags & 0x40) != 0)
        {
            return null;
        }

        var body = new byte[size];
        await content.ReadExactlyAsync(body, cancellationToken);
        // Tag-level unsynchronisation (v2.2/2.3). In v2.4 the flag is per frame and handled below.
        if (major <= 3 && (flags & 0x80) != 0)
        {
            body = Unsynchronise(body);
        }

        var position = 0;
        if (major >= 3 && (flags & 0x40) != 0)
        {
            if (body.Length < 4)
            {
                return null;
            }

            long extended = major == 3
                ? 4L + BinaryPrimitives.ReadUInt32BigEndian(body)
                : TrySynchsafe(body.AsSpan(0, 4), out var extendedSize) ? extendedSize : -1;
            if (extended < 4 || extended > body.Length)
            {
                return null;
            }

            position = (int)extended;
        }

        string? title = null, album = null, genre = null, comment = null;
        var artists = new List<string>();
        var albumArtists = new List<string>();
        var composers = new List<string>();
        var languages = new List<string>();
        var frameHeader = major == 2 ? 6 : HeaderBytes;
        for (var frames = 0; frames < MaxFrames && position + frameHeader <= body.Length; frames++)
        {
            if (body[position] == 0)
            {
                break; // Padding.
            }

            var id = Encoding.ASCII.GetString(body, position, major == 2 ? 3 : 4);
            if (!id.All(character => character is >= 'A' and <= 'Z' or >= '0' and <= '9'))
            {
                break;
            }

            long frameSize;
            if (major == 2)
            {
                frameSize = (body[position + 3] << 16) | (body[position + 4] << 8) | body[position + 5];
            }
            else if (major == 3)
            {
                frameSize = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(position + 4, 4));
            }
            else if (TrySynchsafe(body.AsSpan(position + 4, 4), out var synchsafeSize))
            {
                frameSize = synchsafeSize;
            }
            else
            {
                break;
            }

            var dataStart = position + frameHeader;
            if (frameSize > body.Length - dataStart)
            {
                break; // Does not fit: the tag is truncated or the size is hostile. Nothing past it can be trusted.
            }

            var frameFlags = major >= 3 ? (body[position + 8] << 8) | body[position + 9] : 0;
            position = dataStart + (int)frameSize;

            var kind = Classify(id);
            if (kind == FrameKind.None || frameSize == 0 || (kind != FrameKind.Comment && frameSize > MaxTextFrameBytes))
            {
                continue;
            }

            if (HasUnsupportedFrameFlags(major, frameFlags))
            {
                continue;
            }

            var data = body.AsSpan(dataStart, (int)Math.Min(frameSize, MaxTextFrameBytes));
            if (major == 4 && (frameFlags & 0x0001) != 0)
            {
                if (data.Length <= 4)
                {
                    continue;
                }

                data = data[4..]; // Data length indicator.
            }

            var bytes = major == 4 && (frameFlags & 0x0002) != 0 ? Unsynchronise(data.ToArray()) : data.ToArray();
            var values = kind == FrameKind.Comment ? DecodeComment(bytes) : DecodeText(bytes);
            if (values.Count == 0)
            {
                continue;
            }

            switch (kind)
            {
                case FrameKind.Title: title ??= values[0]; break;
                case FrameKind.Album: album ??= values[0]; break;
                case FrameKind.Artist: artists.AddRange(values); break;
                case FrameKind.AlbumArtist: albumArtists.AddRange(values); break;
                case FrameKind.Composer: composers.AddRange(values); break;
                case FrameKind.Language: languages.AddRange(values); break;
                case FrameKind.Genre: genre ??= string.Join("; ", values); break;
                case FrameKind.Comment: comment ??= values[0]; break;
            }
        }

        return new Id3Tags($"id3v2.{major}", title, album, artists, albumArtists, composers, languages, genre, comment);
    }

    private static async Task<Id3Tags?> ReadV1Async(Stream content, CancellationToken cancellationToken)
    {
        if (content.Length < V1Bytes)
        {
            return null;
        }

        content.Position = content.Length - V1Bytes;
        var block = new byte[V1Bytes];
        await content.ReadExactlyAsync(block, cancellationToken);
        if (block[0] != (byte)'T' || block[1] != (byte)'A' || block[2] != (byte)'G')
        {
            return null;
        }

        var title = Field(block, 3);
        var artist = Field(block, 33);
        var album = Field(block, 63);
        var tags = new Id3Tags(
            "id3v1", title, album, artist is null ? [] : [artist], [], [], [], null, null);
        return tags.HasAnything ? tags : null;
    }

    private static string? Field(byte[] block, int offset)
    {
        var value = Encoding.Latin1.GetString(block, offset, 30).Trim('\0', ' ');
        return value.Length == 0 ? null : value;
    }

    private enum FrameKind { None, Title, Album, Artist, AlbumArtist, Composer, Language, Genre, Comment }

    private static FrameKind Classify(string id) => id switch
    {
        "TIT2" or "TT2" => FrameKind.Title,
        "TALB" or "TAL" => FrameKind.Album,
        "TPE1" or "TP1" => FrameKind.Artist,
        "TPE2" or "TP2" => FrameKind.AlbumArtist,
        "TCOM" or "TCM" => FrameKind.Composer,
        "TLAN" or "TLA" => FrameKind.Language,
        "TCON" or "TCO" => FrameKind.Genre,
        "COMM" or "COM" => FrameKind.Comment,
        _ => FrameKind.None
    };

    /// <summary>Compressed or encrypted frames cannot be read as text.</summary>
    private static bool HasUnsupportedFrameFlags(int major, int flags) => major switch
    {
        3 => (flags & 0x00C0) != 0,
        4 => (flags & 0x000C) != 0,
        _ => false
    };

    /// <summary>One text value per null-separated string, decoded by the frame's encoding byte. Empty when unreadable.</summary>
    private static List<string> DecodeText(byte[] data)
    {
        if (data.Length < 2)
        {
            return [];
        }

        return Split(DecodeString(data[0], data.AsSpan(1)));
    }

    /// <summary>A comment frame is encoding, 3-letter language, a short description, then the text.</summary>
    private static List<string> DecodeComment(byte[] data)
    {
        if (data.Length < 5)
        {
            return [];
        }

        var encoding = data[0];
        var rest = data.AsSpan(4);
        var unicode = encoding is 1 or 2;
        var terminator = -1;
        for (var index = 0; index < rest.Length; index += unicode ? 2 : 1)
        {
            if (unicode ? index + 1 < rest.Length && rest[index] == 0 && rest[index + 1] == 0 : rest[index] == 0)
            {
                terminator = index + (unicode ? 2 : 1);
                break;
            }
        }

        if (terminator < 0 || terminator > rest.Length)
        {
            return [];
        }

        return Split(DecodeString(encoding, rest[terminator..]));
    }

    private static string DecodeString(byte encoding, ReadOnlySpan<byte> payload)
    {
        switch (encoding)
        {
            case 0:
                return Encoding.Latin1.GetString(payload);
            case 1:
                if (payload.Length >= 2 && payload[0] == 0xFE && payload[1] == 0xFF)
                {
                    return Encoding.BigEndianUnicode.GetString(payload[2..]);
                }

                return Encoding.Unicode.GetString(payload.Length >= 2 && payload[0] == 0xFF && payload[1] == 0xFE ? payload[2..] : payload);
            case 2:
                return Encoding.BigEndianUnicode.GetString(payload);
            case 3:
                return Encoding.UTF8.GetString(payload);
            default:
                return string.Empty;
        }
    }

    private static List<string> Split(string text) => text
        .Replace("﻿", string.Empty, StringComparison.Ordinal)
        .Split('\0')
        .Select(value => value.Trim())
        .Where(value => value.Length > 0)
        .ToList();

    private static bool TrySynchsafe(ReadOnlySpan<byte> bytes, out int value)
    {
        value = 0;
        foreach (var part in bytes)
        {
            if ((part & 0x80) != 0)
            {
                return false;
            }

            value = (value << 7) | part;
        }

        return true;
    }

    /// <summary>Undoes unsynchronisation: every <c>FF 00</c> was <c>FF</c>.</summary>
    private static byte[] Unsynchronise(byte[] data)
    {
        var result = new List<byte>(data.Length);
        for (var index = 0; index < data.Length; index++)
        {
            result.Add(data[index]);
            if (data[index] == 0xFF && index + 1 < data.Length && data[index + 1] == 0)
            {
                index++;
            }
        }

        return result.ToArray();
    }
}
