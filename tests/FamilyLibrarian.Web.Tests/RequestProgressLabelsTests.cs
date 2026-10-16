using FamilyLibrarian.Contracts.Requests;
using FamilyLibrarian.Web.Client.Requests;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// The word beside a request's title must agree with its format chips. The lab: a book whose ebook said "Fetching from the
/// external provider" still showed "Awaiting next check", because only Family Librarian's own downloads counted.
/// </summary>
[TestClass]
public sealed class RequestProgressLabelsTests
{
    private static BookRequestResponse Request(string status, params string?[] progressCodes) => new(
        Guid.NewGuid(), Guid.NewGuid(), "A Book", [], null, status, string.Empty, true,
        [.. progressCodes.Select(code => new BookRequestFormatResponse(Guid.NewGuid(), "Ebook", "Requested", code))],
        null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [], 0);

    [TestMethod]
    [DataRow("AcquisitionInProgress")]
    [DataRow("SecurityScanInProgress")]
    [DataRow("Publishing")]
    public void AFormatThatIsBeingWorkedOnMakesTheRequestWorking(string code) =>
        Assert.AreEqual("Working", RequestProgressLabels.For(Request("PendingAcquisition", "Available", code), hasActiveAcquisition: false));

    [TestMethod]
    public void OwnDownloadsStillCountAsWorking() =>
        Assert.AreEqual("Working", RequestProgressLabels.For(Request("PendingAcquisition", (string?)null), hasActiveAcquisition: true));

    [TestMethod]
    [DataRow(null)]
    [DataRow("AcquisitionRetrying")]
    [DataRow("Available")]
    public void WithNothingInProgressTheRequestIsAwaitingTheNextCheck(string? code) =>
        Assert.AreEqual("Awaiting next check", RequestProgressLabels.For(Request("PendingAcquisition", code), hasActiveAcquisition: false));

    [TestMethod]
    [DataRow("NeedsReview")]
    [DataRow("Available")]
    [DataRow("Cancelled")]
    public void OnlyARequestStillBeingAcquiredHasALabel(string status) =>
        Assert.IsNull(RequestProgressLabels.For(Request(status, "AcquisitionInProgress"), hasActiveAcquisition: true));
}
