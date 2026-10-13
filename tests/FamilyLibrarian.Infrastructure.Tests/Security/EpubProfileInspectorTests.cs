using System.IO.Compression;
using System.Text;
using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Evidence;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Security;
using static FamilyLibrarian.Infrastructure.Tests.Characterization.CharacterizationFixtures;

namespace FamilyLibrarian.Infrastructure.Tests.Security;

/// <summary>
/// AUTOFIND 3c: the EPUB artifact profile (tier A package metadata and word count, tier B structure titles). Every finding is
/// a Profile-dimension, Unknown-polarity description; none of it changes a verdict. EPUBs are built in the test.
/// </summary>
[TestClass]
public sealed class EpubProfileInspectorTests
{
    private static string WordsOf(int count) => string.Join(' ', Enumerable.Repeat("word", count));

    private static IReadOnlyList<IdentityFact> Inspect(byte[] epub)
    {
        using var archive = new ZipArchive(new MemoryStream(epub), ZipArchiveMode.Read);
        return EpubProfileInspector.Inspect(archive, "OEBPS/content.opf");
    }

    private static string? ValueOf(IReadOnlyList<IdentityFact> facts, string code) => facts.FirstOrDefault(fact => fact.Code == code)?.Value;

    private static void AssertValues(IEnumerable<string> actual, params string[] expected) =>
        CollectionAssert.AreEqual(expected, actual.ToArray());

    private static string[] ValuesOf(IReadOnlyList<IdentityFact> facts, string code) =>
        facts.Where(fact => fact.Code == code).Select(fact => fact.Value!).ToArray();

    // ---- tier A ---------------------------------------------------------------------------------------------------

    [TestMethod]
    public void AThreeChapterBookIsCountedItemByItem()
    {
        var facts = Inspect(Book(chapters: [Chapter("Alpha", 999), Chapter("Beta", 1999), Chapter("Gamma", 2999)]));

        Assert.AreEqual("words=6000;items=3;basis=xhtml_text", ValueOf(facts, EvidenceReasonCodes.ArtifactProfile.TextWordCount));
        Assert.IsTrue(facts.All(fact => fact.Dimension == EvidenceDimension.Profile && fact.Polarity == EvidencePolarity.Unknown),
            "Profile findings describe the file and judge nothing.");
    }

    [TestMethod]
    public void ScriptAndStyleTextIsNotCountedAndNothingButTheCountIsKept()
    {
        var chapter = "<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><style>p { color: red; }</style></head><body>" +
            "<script>var x = 1;</script><p>one two three</p></body></html>";

        var facts = Inspect(Book(chapters: [chapter]));

        Assert.AreEqual("words=3;items=1;basis=xhtml_text", ValueOf(facts, EvidenceReasonCodes.ArtifactProfile.TextWordCount));
        Assert.IsFalse(facts.Any(fact => fact.Value?.Contains("one two", StringComparison.Ordinal) == true), "No running text is stored.");
    }

    [TestMethod]
    public void CalibreSeriesMetadataBecomesNameHashIndex()
    {
        var facts = Inspect(Book(chapters: [Chapter("A", 5)],
            opf: "<meta name=\"calibre:series\" content=\"The Empyrean\"/><meta name=\"calibre:series_index\" content=\"2\"/>"));

        Assert.AreEqual("The Empyrean#2", ValueOf(facts, EvidenceReasonCodes.ArtifactProfile.PackageSeries));
    }

    [TestMethod]
    public void Epub3CollectionMetadataBecomesNameHashIndex()
    {
        var facts = Inspect(Book(chapters: [Chapter("A", 5)],
            opf: "<meta property=\"belongs-to-collection\" id=\"c1\">The Empyrean</meta>" +
                 "<meta refines=\"#c1\" property=\"collection-type\">series</meta>" +
                 "<meta refines=\"#c1\" property=\"group-position\">2</meta>"));

        Assert.AreEqual("The Empyrean#2", ValueOf(facts, EvidenceReasonCodes.ArtifactProfile.PackageSeries));
    }

    [TestMethod]
    public void ACollectionThatIsNotASeriesIsNotASeries()
    {
        var facts = Inspect(Book(chapters: [Chapter("A", 5)],
            opf: "<meta property=\"belongs-to-collection\" id=\"c1\">A Boxed Set</meta>" +
                 "<meta refines=\"#c1\" property=\"collection-type\">set</meta>"));

        Assert.IsNull(ValueOf(facts, EvidenceReasonCodes.ArtifactProfile.PackageSeries));
    }

    [TestMethod]
    public void PublisherDateLanguageAndIsbnLikeIdentifiersAreRecordedAndOtherIdentifiersAreNot()
    {
        var facts = Inspect(Book(chapters: [Chapter("A", 5)],
            opf: "<dc:publisher>Example Press</dc:publisher><dc:date>2023-11-07</dc:date>" +
                 "<dc:identifier>urn:uuid:12345678-1234-1234-1234-123456789abc</dc:identifier>" +
                 "<dc:identifier>978-0-00-000001-1</dc:identifier><dc:identifier>urn:isbn:0306406152</dc:identifier>"));

        Assert.AreEqual("Example Press", ValueOf(facts, EvidenceReasonCodes.ArtifactProfile.PackagePublisher));
        Assert.AreEqual("2023-11-07", ValueOf(facts, EvidenceReasonCodes.ArtifactProfile.PackageDate));
        Assert.AreEqual("en", ValueOf(facts, EvidenceReasonCodes.ArtifactProfile.PackageLanguage));
        AssertValues(ValuesOf(facts, EvidenceReasonCodes.ArtifactProfile.PackageIdentifier), "9780000000011", "0306406152");
    }

    [TestMethod]
    public void NoMoreThanFiveIdentifiersAreRecorded()
    {
        var identifiers = string.Concat(Enumerable.Range(0, 9).Select(index => $"<dc:identifier>978000000000{index}</dc:identifier>"));

        var facts = Inspect(Book(chapters: [Chapter("A", 5)], opf: identifiers));

        Assert.HasCount(EpubProfileInspector.MaxIdentifiers, ValuesOf(facts, EvidenceReasonCodes.ArtifactProfile.PackageIdentifier));
    }

    [TestMethod]
    [DataRow("zh")]
    [DataRow("ja-JP")]
    [DataRow("ko")]
    [DataRow("th")]
    public void ACjkOrThaiPackageHasNoWordCount(string language)
    {
        var facts = Inspect(Book(chapters: [Chapter("A", 50)], language: language));

        Assert.AreEqual("reason=language", ValueOf(facts, EvidenceReasonCodes.ArtifactProfile.TextWordCountUnavailable));
        Assert.IsNull(ValueOf(facts, EvidenceReasonCodes.ArtifactProfile.TextWordCount));
    }

    // ---- bounds and hostile input --------------------------------------------------------------------------------

    [TestMethod]
    public void AChapterWithADoctypeIsRefusedWithoutAnExceptionAndTheCountIsUnavailable()
    {
        const string hostile = "<?xml version=\"1.0\"?><!DOCTYPE html [<!ENTITY xxe SYSTEM \"file:///etc/passwd\">]>" +
            "<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><p>&xxe;</p></body></html>";

        var facts = Inspect(Book(chapters: [Chapter("A", 10), hostile]));

        Assert.AreEqual("reason=unreadable", ValueOf(facts, EvidenceReasonCodes.ArtifactProfile.TextWordCountUnavailable));
        Assert.IsNotNull(ValueOf(facts, EvidenceReasonCodes.ArtifactProfile.PackageLanguage), "The metadata tier still stands.");
    }

    [TestMethod]
    public void AnItemOverTheItemCapMakesTheCountUnavailableWithoutReadingIt()
    {
        var huge = "<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><p>" +
            new string('a', (int)EpubProfileInspector.MaxItemBytes + 10) + "</p></body></html>";

        var facts = Inspect(Book(chapters: [huge]));

        Assert.AreEqual("reason=cap", ValueOf(facts, EvidenceReasonCodes.ArtifactProfile.TextWordCountUnavailable));
    }

    [TestMethod]
    public void AHeadingWithControlCharactersAndAThousandCharactersIsStoredBoundedAndClean()
    {
        // C0 controls cannot appear in well-formed XML at all (that case is the "unreadable" row above); these are legal
        // XML characters that are still control characters.
        var title = "Chapter\u0085\u009F\u007F One " + new string('x', 1000);
        var chapter = $"<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><h1>{title}</h1><p>text</p></body></html>";

        var facts = Inspect(Book(chapters: [chapter]));

        var heading = ValuesOf(facts, EvidenceReasonCodes.ArtifactProfile.Heading).Single();
        Assert.IsLessThanOrEqualTo(ArtifactEvidence.MaxValueLength, heading.Length);
        Assert.IsFalse(heading.Any(char.IsControl));
        StringAssert.StartsWith(heading, "Chapter One ");
    }

    // ---- tier B ---------------------------------------------------------------------------------------------------

    [TestMethod]
    public void SixtyTocEntriesYieldFortyRows()
    {
        var facts = Inspect(Book(chapters: [Chapter("A", 5)], tocTitles: [.. Enumerable.Range(1, 60).Select(index => $"Entry {index}")]));

        var rows = facts.Where(fact => fact.Code == EvidenceReasonCodes.ArtifactProfile.TocEntry).ToArray();
        Assert.HasCount(EpubProfileInspector.MaxTocEntries, rows);
        Assert.AreEqual("ev-toc-1", rows[0].FindingId);
        Assert.AreEqual("Entry 1", rows[0].Value);
        Assert.AreEqual("ev-toc-40", rows[^1].FindingId);
    }

    [TestMethod]
    public void OnlyH1AndH2HeadingsAreKeptAndAtMostForty()
    {
        var chapters = Enumerable.Range(1, 30).Select(index =>
            $"<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><h1>One {index}</h1><h2>Two {index}</h2><h3>Three {index}</h3></body></html>").ToArray();

        var facts = Inspect(Book(chapters: chapters));

        var headings = ValuesOf(facts, EvidenceReasonCodes.ArtifactProfile.Heading);
        Assert.HasCount(EpubProfileInspector.MaxHeadings, headings);
        Assert.IsFalse(headings.Any(heading => heading.StartsWith("Three", StringComparison.Ordinal)));
        Assert.AreEqual("One 1", headings[0]);
        Assert.AreEqual("Two 1", headings[1]);
    }

    [TestMethod]
    public void FrontAndBackMatterSectionNamesComeFromEpubTypeAndAreBounded()
    {
        var front = "<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\"><body epub:type=\"frontmatter\">" +
            "<section epub:type=\"titlepage\"><p>x</p></section><section epub:type=\"bodymatter chapter\"><p>y</p></section></body></html>";
        var back = "<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\"><body><section epub:type=\"backmatter\">" +
            "<section epub:type=\"appendix\"><p>z</p></section></section></body></html>";

        var facts = Inspect(Book(chapters: [front, back]));

        AssertValues(ValuesOf(facts, EvidenceReasonCodes.ArtifactProfile.FrontMatter), "frontmatter", "titlepage");
        AssertValues(ValuesOf(facts, EvidenceReasonCodes.ArtifactProfile.BackMatter), "backmatter", "appendix");
    }

    [TestMethod]
    public void AnNcxTableOfContentsIsReadWhenThereIsNoNavDocument()
    {
        var facts = Inspect(Book(chapters: [Chapter("A", 5)], ncxTitles: ["Prologue", "Chapter One"]));

        AssertValues(ValuesOf(facts, EvidenceReasonCodes.ArtifactProfile.TocEntry), "Prologue", "Chapter One");
    }

    [TestMethod]
    public void ABrokenPackageYieldsNoFindingsAndNoException()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("OEBPS/content.opf");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("<package><metadata>");
        }

        Assert.IsEmpty(Inspect(stream.ToArray()));
    }

    // ---- through the real verifier -------------------------------------------------------------------------------

    [TestMethod]
    public async Task TheVerifierReportsTheProfileButItsVerdictIsUnchanged()
    {
        var epub = Book(chapters: [Chapter("Alpha", 999), Chapter("Beta", 1999)]);
        var verifier = new EpubAssetIdentityVerifier(new StubWorkLookup("Test Title", "Test Author"), new DeterministicBookMatcher(), new NoRequestStore());

        var result = await verifier.VerifyAsync(
            Asset(RequestMediaType.Ebook, ".epub"), new MemoryStream(epub), CancellationToken.None);

        Assert.IsTrue(result.IsMatch);
        Assert.AreEqual("words=3000;items=2;basis=xhtml_text", result.Facts.Single(fact => fact.Code == EvidenceReasonCodes.ArtifactProfile.TextWordCount).Value);
    }

    // ---- builders ------------------------------------------------------------------------------------------------

    private static string Chapter(string heading, int bodyWords) =>
        $"<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><h1>{heading}</h1><p>{WordsOf(bodyWords)}</p></body></html>";

    private static byte[] Book(
        string[] chapters, string opf = "", string language = "en",
        IReadOnlyList<string>? tocTitles = null, IReadOnlyList<string>? ncxTitles = null)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(archive, "mimetype", "application/epub+zip");
            Write(archive, "META-INF/container.xml",
                "<container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\"><rootfiles>" +
                "<rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>");

            var manifest = new StringBuilder();
            var spine = new StringBuilder();
            for (var index = 0; index < chapters.Length; index++)
            {
                manifest.Append(System.Globalization.CultureInfo.InvariantCulture, $"<item id=\"c{index}\" href=\"c{index}.xhtml\" media-type=\"application/xhtml+xml\"/>");
                spine.Append(System.Globalization.CultureInfo.InvariantCulture, $"<itemref idref=\"c{index}\"/>");
                Write(archive, $"OEBPS/c{index}.xhtml", chapters[index]);
            }

            if (tocTitles is not null)
            {
                manifest.Append("<item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/>");
                Write(archive, "OEBPS/nav.xhtml",
                    "<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\"><body><nav epub:type=\"toc\"><ol>" +
                    string.Concat(tocTitles.Select(title => $"<li><a href=\"c0.xhtml\">{title}</a></li>")) + "</ol></nav></body></html>");
            }

            if (ncxTitles is not null)
            {
                manifest.Append("<item id=\"ncx\" href=\"toc.ncx\" media-type=\"application/x-dtbncx+xml\"/>");
                Write(archive, "OEBPS/toc.ncx",
                    "<ncx xmlns=\"http://www.daisy.org/z3986/2005/ncx/\"><navMap>" +
                    string.Concat(ncxTitles.Select((title, index) =>
                        $"<navPoint id=\"n{index}\"><navLabel><text>{title}</text></navLabel><content src=\"c0.xhtml\"/></navPoint>")) +
                    "</navMap></ncx>");
            }

            Write(archive, "OEBPS/content.opf",
                "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"id\">" +
                "<metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\">" +
                "<dc:title>Test Title</dc:title><dc:creator>Test Author</dc:creator>" +
                $"<dc:language>{language}</dc:language>{opf}</metadata>" +
                $"<manifest>{manifest}</manifest><spine>{spine}</spine></package>");
        }

        return stream.ToArray();
    }

    private static void Write(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
