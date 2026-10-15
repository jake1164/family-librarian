using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Infrastructure.Tests.Acquisition;

[TestClass]
public sealed class ExternalProviderOutputSelectorTests
{
    private static readonly string[] ExpectedBook = ["book"];
    private static readonly string[] ExpectedFirstSecond = ["first", "second"];
    private static readonly ManualImportPolicy ImportPolicy = new();
    private static readonly ExternalProviderOutputPolicy OutputPolicy = new();

    [TestMethod]
    public void SelectsSingleEbookAndIgnoresCoverSidecar()
    {
        var selected = ExternalProviderOutputSelector.Select(
            [Output("book", ".epub", "ebook"), Output("cover", ".jpg", "cover")],
            RequestMediaType.Ebook, ImportPolicy, OutputPolicy);

        CollectionAssert.AreEqual(ExpectedBook, selected.Select(item => item.OutputId).ToArray());
    }

    [TestMethod]
    public void OrdersAudioPartsBySequenceInsteadOfListingOrder()
    {
        var selected = ExternalProviderOutputSelector.Select(
            [Output("second", "02.mp3", "audio-part", 2), Output("first", "01.mp3", "audio-part", 1)],
            RequestMediaType.Audiobook, ImportPolicy, OutputPolicy);

        CollectionAssert.AreEqual(ExpectedFirstSecond, selected.Select(item => item.OutputId).ToArray());
    }

    [TestMethod]
    public void UsesOnlyStrictLegacyTrackFilenameFallback()
    {
        var selected = ExternalProviderOutputSelector.Select(
            [Output("second", "02-Chapter.mp3", "audio-part"), Output("first", "01_Chapter.mp3", "audio-part")],
            RequestMediaType.Audiobook, ImportPolicy, OutputPolicy);

        CollectionAssert.AreEqual(ExpectedFirstSecond, selected.Select(item => item.OutputId).ToArray());
    }

    [TestMethod]
    public void OrdersTenLegacyTracksNumerically()
    {
        var outputs = Enumerable.Range(1, 10)
            .Reverse()
            .Select(number => Output($"track-{number}", $"{number:00}-Chapter.mp3", "audio-part"))
            .ToArray();

        var selected = ExternalProviderOutputSelector.Select(
            outputs, RequestMediaType.Audiobook, ImportPolicy, OutputPolicy);

        CollectionAssert.AreEqual(
            Enumerable.Range(1, 10).Select(number => $"track-{number}").ToArray(),
            selected.Select(item => item.OutputId).ToArray());
    }

    [TestMethod]
    public void RejectsAmbiguousAudioAndPartialSequence()
    {
        Assert.ThrowsExactly<InvalidExternalProviderOutputException>(() =>
            ExternalProviderOutputSelector.Select(
                [Output("a", "a.mp3", null), Output("b", "b.mp3", null)],
                RequestMediaType.Audiobook, ImportPolicy, OutputPolicy));
        Assert.ThrowsExactly<InvalidExternalProviderOutputException>(() =>
            ExternalProviderOutputSelector.Select(
                [Output("a", "01.mp3", "audio-part", 1), Output("b", "02.mp3", "audio-part")],
                RequestMediaType.Audiobook, ImportPolicy, OutputPolicy));
    }

    [TestMethod]
    public void RejectsInvalidSequenceWithoutUsingValidFilenames()
    {
        foreach (var sequences in new[] { new int?[] { 0, 1 }, [1, 1], [1, 3] })
        {
            Assert.ThrowsExactly<InvalidExternalProviderOutputException>(() =>
                ExternalProviderOutputSelector.Select(
                    [Output("first", "01-Chapter.mp3", "audio-part", sequences[0]),
                     Output("second", "02-Chapter.mp3", "audio-part", sequences[1])],
                    RequestMediaType.Audiobook, ImportPolicy, OutputPolicy));
        }
    }

    [TestMethod]
    public void RejectsSequenceOnOtherKindsOrRoles()
    {
        Assert.ThrowsExactly<InvalidExternalProviderOutputException>(() =>
            ExternalProviderOutputSelector.Select(
                [Output("book", "book.epub", "ebook"), Output("cover", "cover.jpg", "cover", 1)],
                RequestMediaType.Ebook, ImportPolicy, OutputPolicy));
        Assert.ThrowsExactly<InvalidExternalProviderOutputException>(() =>
            ExternalProviderOutputSelector.Select(
                [Output("book", "book.epub", "ebook"),
                 Output("descriptor", "link.txt", "descriptor", 1) with { Kind = ProviderOutputKind.Descriptor }],
                RequestMediaType.Ebook, ImportPolicy, OutputPolicy));
    }

    [TestMethod]
    public void RejectsLegacyLabelsAndGappedLeadingNumbers()
    {
        foreach (var filenames in new[]
        {
            new[] { "Part 1.mp3", "Part 2.mp3" },
            ["01-Chapter.mp3", "03-Chapter.mp3"],
            ["01-Chapter.mp3", "01-Other.mp3"],
            ["01 - Chapter.mp3", "02 - Chapter.mp3"]
        })
        {
            Assert.ThrowsExactly<InvalidExternalProviderOutputException>(() =>
                ExternalProviderOutputSelector.Select(
                    [Output("first", filenames[0], "audio-part"), Output("second", filenames[1], "audio-part")],
                    RequestMediaType.Audiobook, ImportPolicy, OutputPolicy));
        }
    }

    [TestMethod]
    public void RejectsDuplicateOutputIdsAndUnknownKinds()
    {
        Assert.ThrowsExactly<InvalidExternalProviderOutputException>(() =>
            ExternalProviderOutputSelector.Select(
                [Output("same", "a.epub", "ebook"), Output("same", "b.epub", "ebook")],
                RequestMediaType.Ebook, ImportPolicy, OutputPolicy));
        Assert.ThrowsExactly<InvalidExternalProviderOutputException>(() =>
            ExternalProviderOutputSelector.Select(
                [Output("unknown", "a.epub", "ebook") with { Kind = ProviderOutputKind.Unknown }],
                RequestMediaType.Ebook, ImportPolicy, OutputPolicy));
    }

    // The lab: "Rebecca.Yarros.-.[The.Empyrean.03.5].-.Threshing.Day.(azw3.epub.mobi)" downloaded fine and was then refused
    // as ambiguous, because the same book in three formats looked like three competing books.
    [TestMethod]
    public void OneBookInSeveralFormatsIsTheEpub()
    {
        const string stem = "Rebecca Yarros - Threshing Day";
        var selected = ExternalProviderOutputSelector.Select(
            [Output("a", $"{stem}.azw3", "ebook"), Output("e", $"{stem}.epub", "ebook"), Output("m", $"{stem}.mobi", "ebook")],
            RequestMediaType.Ebook, ImportPolicy, OutputPolicy);

        Assert.AreEqual("e", selected.Single().OutputId);
    }

    [TestMethod]
    public void WithoutAnEpubTheNextFormatInTheImportOrderIsTaken()
    {
        var selected = ExternalProviderOutputSelector.Select(
            [Output("m", "Book.MOBI", "ebook"), Output("a", "folder/Book.azw3", null)],
            RequestMediaType.Ebook, ImportPolicy, OutputPolicy);

        Assert.AreEqual("a", selected.Single().OutputId);
    }

    [TestMethod]
    public void FiveFormatsOfOneBookTakeTheFirstInTheImportOrder()
    {
        var selected = ExternalProviderOutputSelector.Select(
            [
                Output("fb2", "Book.fb2", "ebook"), Output("kepub", "Book.kepub", "ebook"), Output("azw", "Book.azw", "ebook"),
                Output("mobi", "Book.mobi", "ebook"), Output("azw3", "Book.azw3", "ebook")
            ],
            RequestMediaType.Ebook, ImportPolicy, OutputPolicy);

        Assert.AreEqual("azw3", selected.Single().OutputId);
    }

    // The second lab run: the same release with obfuscated file names, as Usenet releases often have.
    [TestMethod]
    public void ObfuscatedNamesForOneBookInSeveralFormatsTakeTheEpub()
    {
        var selected = ExternalProviderOutputSelector.Select(
            [
                Output("0001", "7e318bc757af2d592b9b44ea9889c397.mobi", "ebook"),
                Output("0002", "94ee0f5423d60eee6c878c3e6536b29b.epub", "ebook"),
                Output("0003", "9e2f7e4375f894fb7ab5343f6aa0a099.azw3", "ebook")
            ],
            RequestMediaType.Ebook, ImportPolicy, OutputPolicy);

        Assert.AreEqual("0002", selected.Single().OutputId);
    }

    [TestMethod]
    public void TwoBooksInTheSameFormatAreStillAmbiguous()
    {
        Assert.ThrowsExactly<InvalidExternalProviderOutputException>(() => ExternalProviderOutputSelector.Select(
            [Output("one", "Threshing Day.epub", "ebook"), Output("two", "Fourth Wing.epub", "ebook")],
            RequestMediaType.Ebook, ImportPolicy, OutputPolicy));
    }

    [TestMethod]
    public void TwoCopiesOfTheSameFormatAreStillAmbiguous()
    {
        Assert.ThrowsExactly<InvalidExternalProviderOutputException>(() => ExternalProviderOutputSelector.Select(
            [Output("one", "dir1/Book.epub", "ebook"), Output("two", "dir2/Book.epub", "ebook")],
            RequestMediaType.Ebook, ImportPolicy, OutputPolicy));
    }

    [TestMethod]
    public void AFormatBesideAnAudiobookPartIsStillRefused()
    {
        Assert.ThrowsExactly<InvalidExternalProviderOutputException>(() => ExternalProviderOutputSelector.Select(
            [Output("e", "Book.epub", "ebook"), Output("m", "Book.mobi", "audio-part")],
            RequestMediaType.Ebook, ImportPolicy, OutputPolicy));
    }

    private static ExternalProviderOutput Output(string id, string filename, string? role, int? sequence = null) =>
        new(id, ProviderOutputKind.File, role, filename, null, null, null, null, null, null, sequence);
}
