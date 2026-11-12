using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Infrastructure.Tests.Acquisition;

[TestClass]
public sealed class RequestReviewCandidateRecordTests
{
    [TestMethod]
    [DataRow("That Night", "Gillian McAllister", "[AUDIOBOOK] Chevy Stevens - That Night")]
    [DataRow("That Night", "Gillian McAllister", "After That Night: Will Trent, Book 11 - Karin Slaughter - 2023")]
    [DataRow("No Prisoners", "Ellis Blake", "Star Wars - The Clone Wars--No Prisoners (NMR")]
    [DataRow("Onyx Storm", "Rebecca Yarros", "The.Empyrean.[03].Onyx.Storm.2.of.2")]
    public void AReleaseOnlyRecordNeverInheritsTheRequestedAuthor(string title, string author, string release)
    {
        var candidate = RequestReviewCandidateRecord.WithCatalogFallback(
            RequestReviewCandidateRecord.From(Guid.NewGuid(), Option(release)), title, author);

        Assert.AreEqual(title, candidate.Title, "The requester label stays neutral; raw releases are administrator-only.");
        Assert.IsTrue(candidate.TitleIsRequestFallback);
        Assert.IsNull(candidate.Author, "Catalog context must not become a source author claim.");
        Assert.AreEqual(release, candidate.ReleaseName);
    }

    [TestMethod]
    public void StructuredSourceClaimsArePreserved()
    {
        var candidate = RequestReviewCandidateRecord.WithCatalogFallback(RequestReviewCandidateRecord.From(Guid.NewGuid(),
            Option("A source release") with { Title = "A source title", Author = "Source Author" }), "Requested title", "Requested Author");
        Assert.AreEqual("A source title", candidate.Title);
        Assert.AreEqual("Source Author", candidate.Author);
        Assert.IsFalse(candidate.TitleIsRequestFallback);
    }

    private static FulfillmentOption Option(string release) => new(
        "example-source", "opaque-result", Guid.Empty, null, RequestMediaType.Audiobook, OptionKind.DirectAcquisition,
        AcquisitionMethod.DirectDownload, "m4b", null, null, null, 0m, null, null, null, null, null,
        ReleaseName: release);
}
