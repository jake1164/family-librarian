using System.IO.Compression;
using System.Text;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Security;

namespace FamilyLibrarian.Infrastructure.Tests.Security;

[TestClass]
public sealed class EpubAssetIdentityVerifierTests
{
    [TestMethod]
    public async Task MatchingPackageMetadataIsAccepted()
    {
        var verifier = new EpubAssetIdentityVerifier(new StubWorkLookup("Restore Me", "Tahereh Mafi"));

        var result = await verifier.VerifyAsync(
            CreateAsset(),
            BuildEpub("Restore Me", "Tahereh Mafi"),
            CancellationToken.None);

        Assert.IsTrue(result.IsMatch);
        Assert.AreEqual("epub-package-metadata", result.VerifierId);
    }

    [TestMethod]
    public async Task CatalogOrderedCreatorMetadataIsAccepted()
    {
        var verifier = new EpubAssetIdentityVerifier(new StubWorkLookup("Restore Me", "Tahereh Mafi"));

        var result = await verifier.VerifyAsync(
            CreateAsset(),
            BuildEpub("Restore Me", "Mafi, Tahereh"),
            CancellationToken.None);

        Assert.IsTrue(result.IsMatch);
    }

    [TestMethod]
    public async Task ATitleDifferingOnlyByALeadingArticleIsAccepted()
    {
        var verifier = new EpubAssetIdentityVerifier(new StubWorkLookup("Green Mummy", "Fergus Hume"));

        var result = await verifier.VerifyAsync(
            CreateAsset(),
            BuildEpub("The Green Mummy", "Fergus Hume"),
            CancellationToken.None);

        Assert.IsTrue(result.IsMatch);
    }

    [TestMethod]
    public async Task ATitleWithAnOldFashionedSubtitleTheCatalogOmitsIsAccepted()
    {
        var verifier = new EpubAssetIdentityVerifier(new StubWorkLookup("Little Women", "Louisa May Alcott"));

        var result = await verifier.VerifyAsync(
            CreateAsset(),
            BuildEpub("Little Women; Or, Meg, Jo, Beth, and Amy", "Louisa May Alcott"),
            CancellationToken.None);

        Assert.IsTrue(result.IsMatch);
    }

    [TestMethod]
    public async Task ACreatorWithAParentheticalFullNameStillMatches()
    {
        var verifier = new EpubAssetIdentityVerifier(new StubWorkLookup("Peter Pan", "J. M. Barrie"));

        var result = await verifier.VerifyAsync(
            CreateAsset(),
            BuildEpub("Peter Pan", "Barrie, J. M. (James Matthew)"),
            CancellationToken.None);

        Assert.IsTrue(result.IsMatch);
    }

    [TestMethod]
    public async Task DifferentPackageMetadataIsHeldUnmatched()
    {
        var verifier = new EpubAssetIdentityVerifier(new StubWorkLookup("Restore Me", "Tahereh Mafi"));

        var result = await verifier.VerifyAsync(
            CreateAsset(),
            BuildEpub("Defy Me", "Tahereh Mafi"),
            CancellationToken.None);

        Assert.IsFalse(result.IsMatch);
    }

    [TestMethod]
    public async Task MissingCreatorIsHeldUnmatched()
    {
        var verifier = new EpubAssetIdentityVerifier(new StubWorkLookup("Restore Me", "Tahereh Mafi"));

        var result = await verifier.VerifyAsync(
            CreateAsset(),
            BuildEpub("Restore Me", creator: null),
            CancellationToken.None);

        Assert.IsFalse(result.IsMatch);
    }

    private static MediaAsset CreateAsset() => new(
        Guid.NewGuid(),
        editionId: null,
        RequestMediaType.Ebook,
        ".epub",
        "book.epub",
        "stored.epub",
        sizeBytes: 1,
        new string('a', 64),
        "application/epub+zip",
        Guid.NewGuid(),
        sourceAcquisitionCandidateId: null,
        DateTimeOffset.UtcNow);

    private static MemoryStream BuildEpub(string title, string? creator)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "mimetype", "application/epub+zip", CompressionLevel.NoCompression);
            WriteEntry(
                archive,
                "META-INF/container.xml",
                """<?xml version="1.0"?><container><rootfiles><rootfile full-path="OPS/content.opf" /></rootfiles></container>""");
            WriteEntry(
                archive,
                "OPS/content.opf",
                $"""<?xml version="1.0"?><package><metadata><dc:title xmlns:dc="http://purl.org/dc/elements/1.1/">{title}</dc:title>{(creator is null ? string.Empty : $"<dc:creator xmlns:dc=\"http://purl.org/dc/elements/1.1/\">{creator}</dc:creator>")}</metadata></package>""");
        }

        stream.Position = 0;
        return stream;
    }

    private static void WriteEntry(
        ZipArchive archive,
        string name,
        string contents,
        CompressionLevel compression = CompressionLevel.Fastest)
    {
        var entry = archive.CreateEntry(name, compression);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(contents);
    }

    private sealed class StubWorkLookup(string title, string author) : IWorkLookup
    {
        public Task<WorkSummary?> FindAsync(Guid workId, CancellationToken cancellationToken) =>
            Task.FromResult<WorkSummary?>(new WorkSummary(workId, title, author, []));
    }
}
