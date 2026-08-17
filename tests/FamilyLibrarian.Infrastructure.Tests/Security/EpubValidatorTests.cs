using System.IO.Compression;
using System.Text;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Security;

namespace FamilyLibrarian.Infrastructure.Tests.Security;

[TestClass]
public sealed class EpubValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task AStructurallyValidEpubPasses()
    {
        var outcome = await Validate(BuildValidEpubBytes());

        Assert.IsTrue(outcome.IsValid, outcome.Message);
    }

    [TestMethod]
    public async Task ANonEpubFormatIsSkippedWithoutInspectingContent()
    {
        var validator = new EpubValidator();
        var asset = CreateAsset(".pdf");

        var outcome = await validator.ValidateAsync(
            asset, new MemoryStream("not a zip at all"u8.ToArray()), CancellationToken.None);

        Assert.IsTrue(outcome.IsValid);
    }

    [TestMethod]
    public async Task ANonZipStreamIsRejected()
    {
        var outcome = await Validate("plain text, not a zip"u8.ToArray());

        Assert.IsFalse(outcome.IsValid);
    }

    [TestMethod]
    public async Task AMissingContainerXmlIsRejected()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "mimetype", "application/epub+zip");
        }

        var outcome = await Validate(stream.ToArray());

        Assert.IsFalse(outcome.IsValid);
        StringAssert.Contains(outcome.Message, "container.xml");
    }

    [TestMethod]
    public async Task AContainerXmlPointingAtAMissingOpfIsRejected()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "mimetype", "application/epub+zip");
            WriteEntry(archive, "META-INF/container.xml", """
                <?xml version="1.0" encoding="UTF-8"?>
                <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                  <rootfiles>
                    <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml" />
                  </rootfiles>
                </container>
                """);
            // OEBPS/content.opf is deliberately not added.
        }

        var outcome = await Validate(stream.ToArray());

        Assert.IsFalse(outcome.IsValid);
        StringAssert.Contains(outcome.Message, "OEBPS/content.opf");
    }

    [TestMethod]
    public async Task AnEntryNameThatEscapesTheArchiveIsRejected()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "mimetype", "application/epub+zip");
            // ZipArchiveEntry.FullName round-trips whatever name was given at
            // creation time, "../" segments included — nothing in ZipArchive
            // itself normalizes or rejects this.
            WriteEntry(archive, "../../etc/passwd", "not actually passwd");
        }

        var outcome = await Validate(stream.ToArray());

        Assert.IsFalse(outcome.IsValid);
        StringAssert.Contains(outcome.Message, "unsafe path");
    }

    [TestMethod]
    public async Task AnEntryExceedingTheCompressionRatioLimitIsRejected()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "mimetype", "application/epub+zip");

            // Highly repetitive content compresses far past the 100:1 ceiling
            // under DEFLATE — this is a flat zip bomb in miniature, not a
            // realistic EPUB payload.
            var bomb = archive.CreateEntry("OEBPS/bomb.txt", CompressionLevel.SmallestSize);
            using var entryStream = bomb.Open();
            var line = Encoding.UTF8.GetBytes(new string('A', 1024) + "\n");
            for (var i = 0; i < 20_000; i++)
            {
                await entryStream.WriteAsync(line);
            }
        }

        var outcome = await Validate(stream.ToArray());

        Assert.IsFalse(outcome.IsValid);
        StringAssert.Contains(outcome.Message, "compression ratio");
    }

    private static async Task<ValidationOutcome> Validate(byte[] content)
    {
        var validator = new EpubValidator();
        var asset = CreateAsset(".epub");
        return await validator.ValidateAsync(asset, new MemoryStream(content), CancellationToken.None);
    }

    private static MediaAsset CreateAsset(string format) => new(
        Guid.NewGuid(),
        editionId: null,
        RequestMediaType.Ebook,
        format,
        "book" + format,
        $"{Guid.NewGuid():N}{format}",
        sizeBytes: 1024,
        sha256: new string('a', 64),
        detectedMimeType: "application/epub+zip",
        Guid.NewGuid(),
        sourceAcquisitionCandidateId: null,
        Now);

    private static byte[] BuildValidEpubBytes()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "mimetype", "application/epub+zip", CompressionLevel.NoCompression);
            WriteEntry(archive, "META-INF/container.xml", """
                <?xml version="1.0" encoding="UTF-8"?>
                <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                  <rootfiles>
                    <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml" />
                  </rootfiles>
                </container>
                """);
            WriteEntry(archive, "OEBPS/content.opf", """
                <?xml version="1.0" encoding="UTF-8"?>
                <package xmlns="http://www.idpf.org/2007/opf" version="3.0"></package>
                """);
        }

        return stream.ToArray();
    }

    private static void WriteEntry(
        ZipArchive archive,
        string entryName,
        string content,
        CompressionLevel compressionLevel = CompressionLevel.Optimal)
    {
        var entry = archive.CreateEntry(entryName, compressionLevel);
        using var entryStream = entry.Open();
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
        entryStream.Write(bytes);
    }
}
