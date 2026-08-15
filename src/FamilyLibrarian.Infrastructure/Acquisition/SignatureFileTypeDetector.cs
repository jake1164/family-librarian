using System.Text;

namespace FamilyLibrarian.Infrastructure.Acquisition;

/// <summary>
/// Minimal magic-byte content-type sniffer for the formats Family Librarian
/// currently accepts.
/// </summary>
/// <remarks>
/// Hand-rolled rather than a NuGet dependency: four formats do not justify a
/// new package, and their byte layouts are stable and well documented. This
/// exists to catch a renamed/mislabeled file independent of malware scanning,
/// which arrives in M10.
/// </remarks>
public static class SignatureFileTypeDetector
{
    /// <summary>
    /// Large enough to contain an EPUB's leading ZIP local-file-header plus its
    /// mandatory first "mimetype" entry, and comfortably more than PDF/MP3/M4B need.
    /// </summary>
    public const int RequiredHeaderLength = 128;

    public static string Detect(ReadOnlySpan<byte> header, int headerLength)
    {
        var bytes = header[..Math.Min(headerLength, header.Length)];

        if (StartsWith(bytes, 0x50, 0x4B, 0x03, 0x04))
        {
            // A ZIP archive. EPUB requires "mimetype" to be the first, stored
            // (uncompressed) entry, so a conforming EPUB's declared content type
            // appears within this header buffer.
            return ContainsAscii(bytes, "application/epub+zip")
                ? "application/epub+zip"
                : "application/zip";
        }

        if (StartsWith(bytes, 0x25, 0x50, 0x44, 0x46, 0x2D))
        {
            return "application/pdf";
        }

        if (StartsWith(bytes, 0x49, 0x44, 0x33))
        {
            return "audio/mpeg"; // ID3-tagged MP3
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && (bytes[1] & 0xE0) == 0xE0)
        {
            return "audio/mpeg"; // Bare MPEG frame sync, no ID3 tag
        }

        if (bytes.Length >= 8 &&
            bytes[4] == (byte)'f' && bytes[5] == (byte)'t' && bytes[6] == (byte)'y' && bytes[7] == (byte)'p')
        {
            return "audio/mp4"; // ISO base media file (M4A/M4B) ftyp box
        }

        return "application/octet-stream";
    }

    private static bool StartsWith(ReadOnlySpan<byte> bytes, params ReadOnlySpan<byte> signature) =>
        bytes.Length >= signature.Length && bytes[..signature.Length].SequenceEqual(signature);

    private static bool ContainsAscii(ReadOnlySpan<byte> haystack, string needle)
    {
        Span<byte> needleBytes = stackalloc byte[needle.Length];
        Encoding.ASCII.GetBytes(needle, needleBytes);
        return haystack.IndexOf(needleBytes) >= 0;
    }
}
