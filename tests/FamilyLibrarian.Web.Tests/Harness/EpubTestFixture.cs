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
    public static byte[] BuildMinimalEpubBytes(
        string title = "The Hobbit",
        string author = "J. R. R. Tolkien",
        bool withReadingOrder = true,
        string? subject = null,
        int readingOrderFilesMissing = 0,
        int deadNavigationLinks = 0)
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

            // The reading order: one real chapter, plus (for the completeness tests) items whose files are absent.
            var manifest = "<item id=\"c1\" href=\"chapter1.xhtml\" media-type=\"application/xhtml+xml\"/>";
            var spine = "<itemref idref=\"c1\"/>";
            for (var missing = 1; missing <= readingOrderFilesMissing; missing++)
            {
                manifest += $"<item id=\"m{missing}\" href=\"missing{missing}.xhtml\" media-type=\"application/xhtml+xml\"/>";
                spine += $"<itemref idref=\"m{missing}\"/>";
            }

            if (deadNavigationLinks > 0)
            {
                manifest += "<item id=\"nav\" href=\"nav.xhtml\" properties=\"nav\" media-type=\"application/xhtml+xml\"/>";
                WriteTextEntry(archive, "OEBPS/nav.xhtml",
                    "<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><nav><a href=\"chapter1.xhtml\">One</a>" +
                    string.Concat(Enumerable.Range(1, deadNavigationLinks).Select(i => $"<a href=\"gone{i}.xhtml\">Gone {i}</a>")) +
                    "</nav></body></html>");
            }

            WriteTextEntry(archive, "OEBPS/content.opf", $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <package xmlns="http://www.idpf.org/2007/opf" version="3.0">
                  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                    <dc:title>{title}</dc:title>
                    <dc:creator>{author}</dc:creator>
                    {(subject is null ? string.Empty : $"<dc:subject>{subject}</dc:subject>")}
                  </metadata>
                  {(withReadingOrder ? $"<manifest>{manifest}</manifest><spine>{spine}</spine>" : string.Empty)}
                </package>
                """);

            if (withReadingOrder)
            {
                WriteTextEntry(archive, "OEBPS/chapter1.xhtml", "<html><body><p>Text.</p></body></html>");
            }
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
