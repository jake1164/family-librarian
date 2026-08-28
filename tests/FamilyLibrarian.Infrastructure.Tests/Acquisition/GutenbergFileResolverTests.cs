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

    private static readonly string[] MainCollectionExpectedUrls =
    [
        "https://mirror-one.example/1/2/1/4/7/12147/12147-h.zip",
        "https://mirror-two.example/catalog/1/2/1/4/7/12147/12147-h.zip"
    ];

    private static readonly string[] AudioExpectedUrls =
    [
        "https://mirror-one.example/2/6/2/9/7/26297/mp3/26297-01.mp3",
        "https://mirror-two.example/catalog/2/6/2/9/7/26297/mp3/26297-01.mp3"
    ];

    private static readonly string[] PublicWebsiteFallbackExpectedUrls =
    [
        "https://mirror-one.example/2/6/2/9/7/26297/mp3/26297-01.mp3",
        "https://www.gutenberg.org/files/26297/mp3/26297-01.mp3"
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
    public void MainCollectionFilesPathUsesTheMirrorDirectoryLayout()
    {
        var urls = _resolver.Resolve("/files/12147/12147-h.zip", GutenbergFormatKind.Other);

        CollectionAssert.AreEqual(
            MainCollectionExpectedUrls,
            urls.Select(url => url.ToString()).ToArray());
    }

    [TestMethod]
    public void AudioFilesUseTheMirrorDirectoryLayout()
    {
        var urls = _resolver.Resolve("/files/26297/mp3/26297-01.mp3", GutenbergFormatKind.AudioMp3);

        CollectionAssert.AreEqual(
            AudioExpectedUrls,
            urls.Select(url => url.ToString()).ToArray());
    }

    [TestMethod]
    public void PublicWebsiteFallsBackToTheOriginalAudioPath()
    {
        var resolver = new GutenbergFileResolver(new GutenbergMirrorOptions
        {
            BaseUris = ["https://mirror-one.example/", "https://www.gutenberg.org/"]
        });

        var urls = resolver.Resolve("/files/26297/mp3/26297-01.mp3", GutenbergFormatKind.AudioMp3);

        CollectionAssert.AreEqual(
            PublicWebsiteFallbackExpectedUrls,
            urls.Select(url => url.ToString()).ToArray());
    }
}
