using FamilyLibrarian.Domain.Publishing;

namespace FamilyLibrarian.Domain.Tests.Publishing;

[TestClass]
public sealed class LibraryImportTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void ANewImportStartsPublishing()
    {
        var import = new LibraryImport(Guid.NewGuid(), Now);

        Assert.AreEqual(LibraryImportStatus.Publishing, import.Status);
        Assert.IsNull(import.CompletedAtUtc);
    }

    [TestMethod]
    public void AnEmptyAssetIdIsRejected()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new LibraryImport(Guid.Empty, Now));
    }

    [TestMethod]
    public void MarkAvailableRequiresAnExternalBookId()
    {
        var import = new LibraryImport(Guid.NewGuid(), Now);

        Assert.ThrowsExactly<ArgumentException>(() => import.MarkAvailable(" ", Now));
    }

    [TestMethod]
    public void MarkAvailableRecordsTheBookIdAndCompletion()
    {
        var import = new LibraryImport(Guid.NewGuid(), Now);
        import.MarkAwaitingVerification("The Hobbit-abc123.epub");

        import.MarkAvailable("42", Now.AddMinutes(1));

        Assert.AreEqual(LibraryImportStatus.Available, import.Status);
        Assert.AreEqual("42", import.ExternalBookId);
        Assert.AreEqual(Now.AddMinutes(1), import.CompletedAtUtc);
        Assert.IsNull(import.FailureReason);
    }

    [TestMethod]
    public void MarkAwaitingVerificationRecordsTheDeliveredFilename()
    {
        var import = new LibraryImport(Guid.NewGuid(), Now);

        import.MarkAwaitingVerification("The Hobbit-abc123.epub");

        Assert.AreEqual(LibraryImportStatus.AwaitingVerification, import.Status);
        Assert.AreEqual("The Hobbit-abc123.epub", import.TargetFilename);
    }

    [TestMethod]
    public void MarkAwaitingVerificationRequiresATargetFilename()
    {
        var import = new LibraryImport(Guid.NewGuid(), Now);

        Assert.ThrowsExactly<ArgumentException>(() => import.MarkAwaitingVerification(" "));
    }

    [TestMethod]
    public void MarkFailedRequiresAReason()
    {
        var import = new LibraryImport(Guid.NewGuid(), Now);

        Assert.ThrowsExactly<ArgumentException>(() => import.MarkFailed(string.Empty, Now));
    }

    [TestMethod]
    public void ResetForRetryClearsFailureAndCompletion()
    {
        var import = new LibraryImport(Guid.NewGuid(), Now);
        import.MarkFailed("transport error", Now.AddMinutes(1));

        import.ResetForRetry();

        Assert.AreEqual(LibraryImportStatus.Publishing, import.Status);
        Assert.IsNull(import.FailureReason);
        Assert.IsNull(import.CompletedAtUtc);
    }
}
