using System.IO.Compression;
using System.Text;

namespace FamilyLibrarian.Web.Tests.Harness;

/// <summary>
/// Builds a structurally valid, minimal EPUB for tests that exercise the
/// approval/trust flow rather than <c>EpubValidator</c> itself.
/// </summary>
/// <remarks>
/// Shared here rather than duplicated per test file — unlike the small
/// per-file fakes elsewhere in this project (a recording audit writer, a
/// fixed clock), a correct EPUB byte layout is genuinely nontrivial and now
/// consumed identically by several test classes; three independent copies
/// would only need to drift once to hide a real bug. Before <c>EpubValidator</c>
/// existed, these callers built a bare ZIP local-file-header with no central
/// directory — never a file <see cref="ZipArchive"/> could actually open, let
/// alone a real EPUB — which worked only because nothing checked archive
/// structure yet.
/// </remarks>
public static class EpubTestFixture
{
    public static byte[] BuildMinimalEpubBytes()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            // Stored (uncompressed) and written first, matching the Open
            // Container Format's mandatory layout for this entry.
            var mimetype = archive.CreateEntry("mimetype", CompressionLevel.NoCompression);
            using (var entryStream = mimetype.Open())
            {
                var mimetypeBytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
                    .GetBytes("application/epub+zip");
                entryStream.Write(mimetypeBytes);
            }

            WriteTextEntry(archive, "META-INF/container.xml", """
                <?xml version="1.0" encoding="UTF-8"?>
                <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                  <rootfiles>
                    <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml" />
                  </rootfiles>
                </container>
                """);

            WriteTextEntry(archive, "OEBPS/content.opf", """
                <?xml version="1.0" encoding="UTF-8"?>
                <package xmlns="http://www.idpf.org/2007/opf" version="3.0"></package>
                """);
        }

        return stream.ToArray();
    }

    private static void WriteTextEntry(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName);
        using var entryStream = entry.Open();
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
        entryStream.Write(bytes);
    }
}
