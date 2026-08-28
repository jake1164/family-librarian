using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Infrastructure.Gutenberg;

namespace FamilyLibrarian.Infrastructure.Tests.Acquisition;

[TestClass]
public sealed class GutenbergFileResolverTests
{
    private static readonly string[] Epub3ExpectedUrls =
    [
        "https://mirror-one.example/cache/epub/1342/pg1342-images-3.epub",
        "https://mirror-two.example/catalog/cache/epub/1342/pg1342-images-3.epub"
    ];

    private static readonly string[] HistoricExpectedUrls =
    [
        "https://mirror-one.example/files/12147/12147-h.zip",
        "https://mirror-two.example/catalog/files/12147/12147-h.zip"
    ];

    private readonly GutenbergFileResolver _resolver = new(new GutenbergMirrorOptions
    {
        BaseUris = ["https://mirror-one.example/", "https://mirror-two.example/catalog/"]
    });

    [TestMethod]
    public void Epub3ImagesUsesTheGeneratedCollectionPathOnEveryMirror()
    {
        var urls = _resolver.Resolve("/ebooks/1342.epub3.images", GutenbergFormatKind.Epub3Images);

        CollectionAssert.AreEqual(
            Epub3ExpectedUrls,
            urls.Select(url => url.ToString()).ToArray());
    }

    [TestMethod]
    public void HistoricFilesPathIsPreserved()
    {
        var urls = _resolver.Resolve("/files/12147/12147-h.zip", GutenbergFormatKind.Other);

        CollectionAssert.AreEqual(
            HistoricExpectedUrls,
            urls.Select(url => url.ToString()).ToArray());
    }
}
