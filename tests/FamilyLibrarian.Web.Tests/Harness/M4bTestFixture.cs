using System.Buffers.Binary;
using System.Text;

namespace FamilyLibrarian.Web.Tests.Harness;

/// <summary>
/// Builds a minimal, structurally valid M4B for tests that exercise the approval/trust flow: matching
/// title and author tags plus one audio track whose standard media header declares a language.
/// </summary>
public static class M4bTestFixture
{
    public static byte[] Build(string title, string author, string? audioLanguage, uint? durationSeconds = null)
    {
        var tags = Concat(Tag("nam", title), Tag("ART", author));
        var udta = Box("udta", Box("meta", Concat(new byte[4], Box("ilst", tags))));
        var track = audioLanguage is null ? [] : AudioTrack(audioLanguage, durationSeconds);
        var moov = Box("moov", Concat(track, udta));
        return Concat(Box("ftyp", "M4B "u8.ToArray()), moov, Box("mdat", new byte[4096]));
    }

    private static byte[] AudioTrack(string language, uint? durationSeconds)
    {
        var packed = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(packed,
            (ushort)(((language[0] - 0x60) << 10) | ((language[1] - 0x60) << 5) | (language[2] - 0x60)));
        var hdlr = Box("hdlr", Concat(new byte[8], "soun"u8.ToArray(), new byte[12], [0]));
        var header = new byte[20];
        if (durationSeconds is { } seconds)
        {
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12), 1_000);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), seconds * 1_000);
        }

        var mdhd = Box("mdhd", Concat(header, packed, new byte[2]));
        return Box("trak", Box("mdia", Concat(hdlr, mdhd)));
    }

    private static byte[] Tag(string name, string value)
    {
        var payload = Concat([0, 0, 0, 1], new byte[4], Encoding.UTF8.GetBytes(value));
        return Box("©" + name, Box("data", payload), latin1Name: true);
    }

    private static byte[] Box(string type, byte[] payload, bool latin1Name = false)
    {
        var size = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(size, (uint)(payload.Length + 8));
        return Concat(size, (latin1Name ? Encoding.Latin1 : Encoding.ASCII).GetBytes(type), payload);
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(part => part).ToArray();
}
