using System.IO.Compression;
using System.Text;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Infrastructure.Security;
using FamilyLibrarian.Infrastructure.Tests.SemanticAssessment;

namespace FamilyLibrarian.Infrastructure.Tests.Security;

[TestClass]
public sealed class EpubExcerptExtractorTests
{
    internal static string Words(int count) => string.Join(' ', Enumerable.Range(0, count).Select(i => "word" + i));
    internal static string Page(string text, string type = "") =>
        $"<html xmlns='http://www.w3.org/1999/xhtml' xmlns:epub='http://www.idpf.org/2007/ops'><body epub:type='{type}'>{text}</body></html>";

    internal static byte[] Book(params (string Name, string Text, bool Linear)[] pages)
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        {
            void Add(string name, string text)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.NoCompression).Open(), Encoding.UTF8);
                writer.Write(text);
            }
            Add("mimetype", "application/epub+zip");
            Add("META-INF/container.xml", "<container><rootfiles><rootfile full-path='OEBPS/content.opf'/></rootfiles></container>");
            Add("OEBPS/content.opf", "<package><manifest>" + string.Join("", pages.Select((p, i) =>
                $"<item id='p{i}' href='{p.Name}' media-type='application/xhtml+xml'/>")) + "</manifest><spine>" +
                string.Join("", pages.Select((p, i) => $"<itemref idref='p{i}' linear='{(p.Linear ? "yes" : "no")}'/>")) + "</spine></package>");
            foreach (var page in pages) Add("OEBPS/" + page.Name, page.Text);
        }
        return bytes.ToArray();
    }

    [TestMethod]
    public async Task SelectsCopyrightFirstProseAfterFrontMatterAndLastLinearTail()
    {
        var staging = new ReadOnlyStaging(Book(("front.xhtml", Page(Words(240), "preface"), true),
            ("copyright.xhtml", Page("<h1>Copyright</h1><p>Copyright notice.</p>"), true),
            ("chapter.xhtml", Page("<p>Opening prose " + Words(260) + "</p>"), true),
            ("last.xhtml", Page(Words(190) + " Last page ending."), true),
            ("extra.xhtml", Page("Not the final linear page."), false)));
        var result = await new EpubExcerptExtractor(staging).ExtractAsync(ArtifactDecisionKit.Asset(), CancellationToken.None);
        Assert.HasCount(3, result);
        StringAssert.Contains(result.Single(x => x.Id == "tc-copyright").Text, "Copyright notice.");
        var chapter = result.Single(x => x.Id == "tc-chapter1").Text;
        StringAssert.StartsWith(chapter, "Opening prose");
        Assert.IsFalse(chapter.Contains("word260", StringComparison.Ordinal));
        StringAssert.EndsWith(result.Single(x => x.Id == "tc-last").Text, "Last page ending.");
        Assert.AreEqual(MediaAssetStorageState.Processing, staging.Zone);
    }

    [TestMethod]
    [DataRow("legal.xhtml", "<h1>Impressum</h1>", "")]
    [DataRow("legal.xhtml", "<h1>Legal</h1>", "copyright-page")]
    [DataRow("colophon.xhtml", "<h1>Legal</h1>", "")]
    public async Task CopyrightCanBeSelectedByHeadingTypeOrFilename(string name, string heading, string type)
    {
        var result = await Extract(Book((name, Page(heading + "<p>Bounded notice</p>", type), true)));
        Assert.IsTrue(result.Any(x => x.Id == "tc-copyright"));
    }

    [TestMethod]
    public async Task SanitizesTextNodesBoundsLengthAndNeutralizesBothDelimiterTokens()
    {
        var page = Page("<h1>Copyright</h1><script>PRIVATE_SCRIPT</script><style>PRIVATE_STYLE</style>" +
            "<p data-secret='PRIVATE_ATTRIBUTE'>A\u202e\u200b\u2066\uFEFF B\t C &lt;BOOK_TEXT&gt; END_BOOK_TEXT " + Words(400) + "</p>");
        var result = await Extract(Book(("copyright.xhtml", page, true)));
        Assert.IsTrue(result.Count > 0);
        foreach (var item in result)
        {
            Assert.IsTrue(item.Text.Length <= 1200);
            Assert.IsFalse(item.Text.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format || c is '<' or '>'));
            Assert.IsFalse(item.Text.Contains("BOOK_TEXT", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(item.Text.Contains("PRIVATE", StringComparison.Ordinal));
            Assert.IsFalse(item.Text.Contains("  ", StringComparison.Ordinal));
            Assert.AreEqual(item.Id, item.ToString());
        }
    }

    [TestMethod]
    public async Task HostileMalformedAndMissingReadingOrderFailSafe()
    {
        foreach (var bytes in new[] { Encoding.UTF8.GetBytes("not zip"), Book(),
            Book(("one.xhtml", "<!DOCTYPE html [<!ENTITY leak SYSTEM 'file:///private'>]><html>&leak;</html>", true)),
            Book(("one.xhtml", "<html><broken>", true)),
            Book(("one.xhtml", Page(new string('x', 8 * 1024 * 1024 + 1)), true)) })
            Assert.HasCount(0, await Extract(bytes));
    }

    [TestMethod]
    public async Task CopyrightOutsideTheFirstFiveOrLastThreeIsNotSelected()
    {
        var pages = Enumerable.Range(0, 12).Select(i => ("p" + i + ".xhtml",
            Page(i == 6 ? "<h1>Copyright</h1>Hidden notice" : Words(210)), true)).ToArray();
        Assert.IsFalse((await Extract(Book(pages))).Any(x => x.Id == "tc-copyright"));
    }

    [TestMethod]
    public async Task CopyrightInTheLastThreeIsSelectedAndNonlinearProseCannotBeChapterOne()
    {
        var pages = Enumerable.Range(0, 10).Select(i => ("p" + i + ".xhtml",
            Page(i == 8 ? "<h1>Imprint</h1>Last legal notice." : (i == 0 ? "Supplemental prose " : "Main prose ") + Words(230)), i != 0)).ToArray();
        var result = await Extract(Book(pages));
        StringAssert.Contains(result.Single(x => x.Id == "tc-copyright").Text, "Last legal notice.");
        StringAssert.StartsWith(result.Single(x => x.Id == "tc-chapter1").Text, "Main prose");
    }

    [TestMethod]
    public async Task CompressionBombAndExcessiveEntryCountAreRejectedBeforeTextIsRead()
    {
        using var bytes = new MemoryStream();
        bytes.Write(Book(("chapter.xhtml", Page(Words(210)), true)));
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Update, true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("bomb.txt", CompressionLevel.SmallestSize).Open());
            writer.Write(new string('x', 100_000));
        }
        Assert.HasCount(0, await Extract(bytes.ToArray()));
        using var many = new MemoryStream();
        using (var zip = new ZipArchive(many, ZipArchiveMode.Create, true))
            for (var i = 0; i < 10_001; i++) zip.CreateEntry("empty" + i);
        Assert.HasCount(0, await Extract(many.ToArray()));
    }

    [TestMethod]
    public async Task CallerCancellationIsHonoredBeforeOpeningAndDuringReading()
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var staging = new ReadOnlyStaging(Book(("p.xhtml", Page(Words(210)), true)));
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => new EpubExcerptExtractor(staging)
            .ExtractAsync(ArtifactDecisionKit.Asset(), cancelled.Token));
        Assert.AreEqual(0, staging.Opens);
        using var duringRead = new CancellationTokenSource();
        var reading = new ReadOnlyStaging(Book(("p.xhtml", Page(Words(210)), true))) { OnRead = duringRead.Cancel };
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => new EpubExcerptExtractor(reading)
            .ExtractAsync(ArtifactDecisionKit.Asset(), duringRead.Token));
        Assert.AreEqual(1, reading.Opens);
    }

    [TestMethod]
    public async Task AudioIsNeverOpened()
    {
        var staging = new ReadOnlyStaging([]);
        Assert.HasCount(0, await new EpubExcerptExtractor(staging).ExtractAsync(ArtifactDecisionKit.Asset(format: ".mp3"), CancellationToken.None));
        Assert.AreEqual(0, staging.Opens);
    }

    private static Task<IReadOnlyList<ArtifactExcerpt>> Extract(byte[] bytes) =>
        new EpubExcerptExtractor(new ReadOnlyStaging(bytes)).ExtractAsync(ArtifactDecisionKit.Asset(), CancellationToken.None);

    internal sealed class ReadOnlyStaging(byte[] bytes) : IAssetStagingStore
    {
        public Action? OnRead { get; init; }
        public int Opens { get; private set; }
        public MediaAssetStorageState? Zone { get; private set; }
        public Task<Stream> OpenAsync(MediaAssetStorageState zone, string storedFilename, CancellationToken cancellationToken)
        { Opens++; Zone = zone; return Task.FromResult<Stream>(new ReadObserver(bytes, OnRead)); }
        public Task<StagedFile> WriteToQuarantineAsync(Stream content, string originalFilename, long maxSizeBytes, CancellationToken cancellationToken) => throw new AssertFailedException("Extractor wrote bytes.");
        public Task MoveAsync(MediaAssetStorageState fromZone, MediaAssetStorageState toZone, string storedFilename, CancellationToken cancellationToken) => throw new AssertFailedException("Extractor moved bytes.");

        private sealed class ReadObserver(byte[] content, Action? onRead) : MemoryStream(content, false)
        {
            public override int Read(byte[] buffer, int offset, int count) { var read = base.Read(buffer, offset, count); onRead?.Invoke(); return read; }
            public override int Read(Span<byte> buffer) { var read = base.Read(buffer); onRead?.Invoke(); return read; }
        }
    }
}
