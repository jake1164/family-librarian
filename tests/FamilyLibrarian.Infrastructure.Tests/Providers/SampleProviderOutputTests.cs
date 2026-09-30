using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Security;
using FamilyLibrarian.SampleProvider;

namespace FamilyLibrarian.Infrastructure.Tests.Providers;

/// <summary>
/// The sample provider stands in for a real source in the family-librarian-lab
/// EXTPROV cases, which run its output through FL's real security pipeline. A
/// fixture that FL's own validators reject fails those cases for a reason that
/// has nothing to do with the behavior under test, so pin it here, in the fast
/// suite, where the rejection message is visible.
/// </summary>
[TestClass]
public sealed class SampleProviderOutputTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task TheSampleEpubPassesFamilyLibrariansEpubStructureValidator()
    {
        var bytes = SampleEpub.Build("Pride and Prejudice", "Jane Austen");

        var outcome = await new EpubValidator().ValidateAsync(
            CreateAsset(), new MemoryStream(bytes), CancellationToken.None);

        Assert.IsTrue(outcome.IsValid, outcome.Message);
    }

    [TestMethod]
    public async Task TheSampleEpubPassesFamilyLibrariansFileTypeValidator()
    {
        var bytes = SampleEpub.Build("Pride and Prejudice", "Jane Austen");

        var outcome = await new FileTypeValidator().ValidateAsync(
            CreateAsset(), new MemoryStream(bytes), CancellationToken.None);

        Assert.IsTrue(outcome.IsValid, outcome.Message);
    }

    private static MediaAsset CreateAsset() => new(
        Guid.NewGuid(),
        editionId: null,
        RequestMediaType.Ebook,
        ".epub",
        "book.epub",
        $"{Guid.NewGuid():N}.epub",
        sizeBytes: 1024,
        sha256: new string('a', 64),
        detectedMimeType: "application/epub+zip",
        Guid.NewGuid(),
        sourceAcquisitionCandidateId: null,
        Now);
}
