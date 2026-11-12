using System.Buffers.Binary;

namespace FamilyLibrarian.Web.Tests.Harness;

/// <summary>
/// A minimal MOBI-family file that passes the signature sniff and the encryption check. It has no content verifier,
/// which is exactly what makes it useful for the attested-only tests.
/// </summary>
public static class MobiTestFixture
{
    public static byte[] Build(string marker = "unmarked")
    {
        var bytes = new byte[128];
        "BOOKMOBI"u8.CopyTo(bytes.AsSpan(60));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(78, 4), 82);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(94, 2), 0); // not encrypted
        System.Text.Encoding.ASCII.GetBytes(marker).AsSpan(0, Math.Min(marker.Length, 20)).CopyTo(bytes.AsSpan(100));
        return bytes;
    }
}
