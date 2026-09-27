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

    private static ExternalProviderOutput Output(string id, string filename, string? role, int? sequence = null) =>
        new(id, ProviderOutputKind.File, role, filename, null, null, null, null, null, null, sequence);
}
