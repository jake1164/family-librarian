using System.IO.Compression;
using System.Text;
using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Evidence;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Security;
using static FamilyLibrarian.Infrastructure.Tests.Characterization.CharacterizationFixtures;

namespace FamilyLibrarian.Infrastructure.Tests.Security;

[TestClass]
public sealed class EpubCorroboratedAuthorTests
{
    [TestMethod]
    [DataRow("calibre", true)]
    [DataRow("epub3", true)]
    [DataRow("isbn", true)]
    [DataRow("none", false)]
    [DataRow("wrong-series", false)]
    [DataRow("isbn10", false)]
    [DataRow("prefix-title", false)]
    [DataRow("wrong-author", false)]
    public async Task ASpellingVariantNeedsExactTitleAndPackageCorroboration(string basis, bool accepted)
    {
        var metadata = basis switch
        {
            "calibre" or "prefix-title" or "wrong-author" => "<meta name=\"calibre:series\" content=\"The Empyrean\"/><meta name=\"calibre:series_index\" content=\"2\"/>",
            "epub3" => "<meta property=\"belongs-to-collection\" id=\"s\">The Empyrean</meta><meta refines=\"#s\" property=\"group-position\">02</meta>",
            "isbn" => "<dc:identifier>urn:isbn:978-0-00-000001-1</dc:identifier>",
            "wrong-series" => "<meta name=\"calibre:series\" content=\"The Empyrean\"/><meta name=\"calibre:series_index\" content=\"3\"/>",
            "isbn10" => "<dc:identifier>0306406152</dc:identifier>",
            _ => ""
        };
        var expected = new WorkSummary(Guid.NewGuid(), "Iron Flame", "Rebecca Yarros", ["9780000000011"],
            Series: [new BookSeries("The Empyrean", "2")]);
        var verifier = new EpubAssetIdentityVerifier(new Lookup(expected), new DeterministicBookMatcher(), new NoRequestStore());
        using var stream = new MemoryStream(Book(
            basis == "prefix-title" ? "Iron Flame: A Different Work" : "Iron Flame",
            basis == "wrong-author" ? "Robert Young" : "Rebeca Yaross", metadata));
        var result = await verifier.VerifyAsync(Asset(RequestMediaType.Ebook, ".epub"), stream, CancellationToken.None);

        Assert.AreEqual(accepted, result.IsMatch);
        if (accepted)
        {
            var author = result.Facts.Single(f => f.FindingId == "ev-author");
            Assert.AreEqual(EvidencePolarity.Weak, author.Polarity);
            Assert.AreEqual("artifact.identity.author_spelling_variant", author.Code);
        }
    }

    internal static byte[] Book(string title = "Iron Flame", string author = "Rebecca Yarros", string metadata = "", int words = 6000)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Write(string path, string text)
            {
                using var writer = new StreamWriter(archive.CreateEntry(path).Open(), new UTF8Encoding(false));
                writer.Write(text);
            }
            Write("META-INF/container.xml", "<container><rootfiles><rootfile full-path=\"OPS/content.opf\"/></rootfiles></container>");
            Write("OPS/content.opf", "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\">" +
                $"<metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title>{title}</dc:title><dc:creator>{author}</dc:creator><dc:language>en</dc:language>{metadata}</metadata>" +
                "<manifest><item id=\"c1\" href=\"1.xhtml\"/><item id=\"c2\" href=\"2.xhtml\"/><item id=\"c3\" href=\"3.xhtml\"/></manifest>" +
                "<spine><itemref idref=\"c1\"/><itemref idref=\"c2\"/><itemref idref=\"c3\"/></spine></package>");
            for (var index = 1; index <= 3; index++)
            {
                var count = words / 3 + (index == 3 ? words % 3 : 0);
                Write($"OPS/{index}.xhtml", "<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><p>" + string.Join(' ', Enumerable.Repeat("word", count)) + "</p></body></html>");
            }
        }
        return stream.ToArray();
    }

    internal sealed class Lookup(WorkSummary work) : IWorkLookup
    {
        public Task<WorkSummary?> FindAsync(Guid workId, CancellationToken cancellationToken) => Task.FromResult<WorkSummary?>(work);
    }
}
