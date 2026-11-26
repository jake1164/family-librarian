using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Web.Client.Requests;

namespace FamilyLibrarian.Web.Tests;

[TestClass]
public sealed class RequestFormatDefaultsTests
{
    [TestMethod]
    [DataRow(null, true, true, true, false)]
    [DataRow("Ebook", true, true, true, false)]
    [DataRow("Audiobook", true, true, false, true)]
    [DataRow("Both", true, true, true, true)]
    [DataRow("Both", true, false, true, false)]
    [DataRow("Both", false, true, false, true)]
    [DataRow("Audiobook", true, false, true, false)]
    [DataRow("Ebook", false, true, false, true)]
    [DataRow("Both", false, false, false, false)]
    public void TheDefaultIsLimitedToWhatCanBeRequested(
        string? preference, bool ebookReady, bool audiobookReady, bool ebook, bool audiobook)
    {
        var formats = new RequestFormatsResponse(
            new FormatReadinessResponse(ebookReady, null),
            new FormatReadinessResponse(audiobookReady, null));

        Assert.AreEqual((ebook, audiobook), RequestFormatDefaults.Choose(preference, formats));
    }

    [TestMethod]
    [DataRow(null, true, false)]
    [DataRow("Audiobook", false, true)]
    [DataRow("Both", true, true)]
    public void AFailedReadinessLookupKeepsTheUsersChoice(string? preference, bool ebook, bool audiobook) =>
        Assert.AreEqual((ebook, audiobook), RequestFormatDefaults.Choose(preference, null));
}
