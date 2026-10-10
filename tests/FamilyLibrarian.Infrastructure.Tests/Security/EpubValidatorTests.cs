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
                <package xmlns="http://www.idpf.org/2007/opf" version="3.0">
                  <manifest><item id="c1" href="chapter1.xhtml" media-type="application/xhtml+xml"/></manifest>
                  <spine><itemref idref="c1"/></spine>
                </package>
                """);
            WriteEntry(archive, "OEBPS/chapter1.xhtml", "<html><body><p>Text.</p></body></html>");
        }

        return stream.ToArray();
    }

    private const string Opf = "http://www.idpf.org/2007/opf";

    /// <summary>An EPUB whose package file and extra entries are chosen by the test.</summary>
    private static byte[] BuildEpubWithPackage(string packageXml, params (string Name, string Content)[] extraEntries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "mimetype", "application/epub+zip", CompressionLevel.NoCompression);
            WriteEntry(archive, "META-INF/container.xml", """
                <?xml version="1.0" encoding="UTF-8"?>
                <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                  <rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml" /></rootfiles>
                </container>
                """);
            WriteEntry(archive, "OEBPS/content.opf", packageXml);
            foreach (var (name, content) in extraEntries) WriteEntry(archive, name, content);
        }

        return stream.ToArray();
    }

    [TestMethod]
    [DataRow("<package xmlns=\"" + Opf + "\" version=\"3.0\"></package>", DisplayName = "no manifest and no spine")]
    [DataRow("<package xmlns=\"" + Opf + "\" version=\"3.0\"><manifest><item id=\"c\" href=\"c.xhtml\"/></manifest></package>", DisplayName = "no spine element")]
    [DataRow("<package xmlns=\"" + Opf + "\" version=\"3.0\"><manifest><item id=\"c\" href=\"c.xhtml\"/></manifest><spine></spine></package>", DisplayName = "empty spine")]
    [DataRow("<package xmlns=\"" + Opf + "\" version=\"3.0\"><manifest><item id=\"c\" href=\"c.xhtml\"/></manifest><spine/></package>", DisplayName = "self-closing spine")]
    [DataRow("<package xmlns=\"" + Opf + "\" version=\"3.0\"><manifest><item id=\"c\" href=\"c.xhtml\"/></manifest><spine><itemref idref=\"missing\"/></spine></package>", DisplayName = "itemref names no manifest item")]
    [DataRow("<package xmlns=\"" + Opf + "\" version=\"3.0\"><manifest><item id=\"c\" href=\"absent.xhtml\"/></manifest><spine><itemref idref=\"c\"/></spine></package>", DisplayName = "listed content file is not in the archive")]
    [DataRow("<package xmlns=\"" + Opf + "\" version=\"3.0\"><manifest><item id=\"c\" href=\"https://example.test/c.xhtml\"/></manifest><spine><itemref idref=\"c\"/></spine></package>", DisplayName = "remote content only")]
    [DataRow("<package xmlns=\"" + Opf + "\" version=\"3.0\"><manifest><item id=\"c\" href=\"../../../etc/c.xhtml\"/></manifest><spine><itemref idref=\"c\"/></spine></package>", DisplayName = "path escaping the archive")]
    public async Task AnEpubWithNoUsableReadingOrderIsRejected(string packageXml)
    {
        var outcome = await Validate(BuildEpubWithPackage(packageXml));

        Assert.IsFalse(outcome.IsValid);
        Assert.AreEqual(EpubValidator.NoReadingOrderMessage, outcome.Message);
    }

    [TestMethod]
    [DataRow("<package xmlns=\"" + Opf + "\" version=\"3.0\"><manifest><item id=\"c\" href=\"only.xhtml\"/></manifest><spine><itemref idref=\"c\"/></spine></package>", "OEBPS/only.xhtml", DisplayName = "single-chapter novella")]
    [DataRow("<package xmlns=\"" + Opf + "\" version=\"3.0\"><manifest><item id=\"c\" href=\"only.xhtml\"/></manifest><spine><itemref idref=\"c\" linear=\"no\"/></spine></package>", "OEBPS/only.xhtml", DisplayName = "non-linear item still counts")]
    [DataRow("<package xmlns=\"" + Opf + "\" version=\"3.0\"><manifest><item id=\"c\" href=\"text/ch%201.xhtml#start\"/></manifest><spine><itemref idref=\"c\"/></spine></package>", "OEBPS/text/ch 1.xhtml", DisplayName = "percent-encoded href with fragment")]
    [DataRow("<package xmlns=\"" + Opf + "\" version=\"3.0\"><manifest><item id=\"c\" href=\"../shared/c.xhtml\"/></manifest><spine><itemref idref=\"c\"/></spine></package>", "shared/c.xhtml", DisplayName = "href leaving the package folder inside the archive")]
    [DataRow("<package version=\"2.0\"><manifest><item id=\"c\" href=\"c.xhtml\"/></manifest><spine toc=\"ncx\"><itemref idref=\"c\"/></spine></package>", "OEBPS/c.xhtml", DisplayName = "package without the OPF namespace")]
    [DataRow("<package xmlns=\"" + Opf + "\" version=\"3.0\"><manifest><item id=\"gone\" href=\"gone.xhtml\"/><item id=\"c\" href=\"c.xhtml\"/></manifest><spine><itemref idref=\"gone\"/><itemref idref=\"c\"/></spine></package>", "OEBPS/c.xhtml", DisplayName = "one resolvable item among unresolvable ones")]
    public async Task UnusualButReadableReadingOrdersStillPass(string packageXml, string contentEntry)
    {
        var outcome = await Validate(BuildEpubWithPackage(packageXml, (contentEntry, "<html><body><p>Text.</p></body></html>")));

        Assert.IsTrue(outcome.IsValid, outcome.Message);
    }

    [TestMethod]
    public async Task APackageThatCannotBeParsedIsLeftToTheIdentityVerifierAsBefore()
    {
        var outcome = await Validate(BuildEpubWithPackage("this is not xml <<<"));

        Assert.IsTrue(outcome.IsValid, "M1-3a is narrow: an unreadable package keeps its previous (structure-valid) outcome.");
    }

    [TestMethod]
    public async Task NoMinimumContentAmountIsRequired()
    {
        var outcome = await Validate(BuildEpubWithPackage(
            "<package xmlns=\"" + Opf + "\" version=\"3.0\"><manifest><item id=\"c\" href=\"c.xhtml\"/></manifest><spine><itemref idref=\"c\"/></spine></package>",
            ("OEBPS/c.xhtml", "x")));

        Assert.IsTrue(outcome.IsValid, outcome.Message);
    }

    [TestMethod]
    public async Task AKepubWithAnEmptySpineIsAlsoRejectedAndNonEpubFormatsAreStillSkipped()
    {
        var empty = BuildEpubWithPackage("<package xmlns=\"" + Opf + "\" version=\"3.0\"></package>");
        var kepub = await new EpubValidator().ValidateAsync(CreateAsset(".kepub"), new MemoryStream(empty), CancellationToken.None);
        var mobi = await new EpubValidator().ValidateAsync(CreateAsset(".mobi"), new MemoryStream(empty), CancellationToken.None);

        Assert.IsFalse(kepub.IsValid);
        Assert.IsTrue(mobi.IsValid, "Other formats are unchanged.");
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
