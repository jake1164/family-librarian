using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Security;

namespace FamilyLibrarian.Infrastructure.Tests.Security;

[TestClass]
public sealed class FileTypeValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task ContentMatchingItsClaimedFormatPasses()
    {
        var validator = new FileTypeValidator();
        var asset = CreateAsset(".pdf");

        var outcome = await validator.ValidateAsync(
            asset, new MemoryStream("%PDF-1.7 rest of a pdf"u8.ToArray()), CancellationToken.None);

        Assert.IsTrue(outcome.IsValid, outcome.Message);
    }

    [TestMethod]
    public async Task ContentThatDoesNotMatchItsClaimedFormatIsRejected()
    {
        var validator = new FileTypeValidator();
        var asset = CreateAsset(".epub");

        // Plain text, not a ZIP — the same re-check WriteToQuarantineAsync
        // already ran at staging time, now repeated at the evaluation gate.
        var outcome = await validator.ValidateAsync(
            asset, new MemoryStream("just plain text"u8.ToArray()), CancellationToken.None);

        Assert.IsFalse(outcome.IsValid);
    }

    [TestMethod]
    public async Task AnUnrecognizedFormatIsRejected()
    {
        var validator = new FileTypeValidator();
        var asset = CreateAsset(".exe");

        var outcome = await validator.ValidateAsync(
            asset, new MemoryStream("MZ..."u8.ToArray()), CancellationToken.None);

        Assert.IsFalse(outcome.IsValid);
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
        detectedMimeType: "application/octet-stream",
        Guid.NewGuid(),
        sourceAcquisitionCandidateId: null,
        Now);
}
