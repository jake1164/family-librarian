using FamilyLibrarian.Application.Catalog;

namespace FamilyLibrarian.Infrastructure.Tests.Catalog;

[TestClass]
public sealed class CatalogVersionSearchTests
{
    private static BookCandidate Book(string id, string title = "Fahrenheit 451") => new(
        "openlibrary", "Open Library", id, title, ["Ray Bradbury"], "A dystopian novel.", null, null, [], [], Language: "en");

    [TestMethod]
    public void FahrenheitNovelCannotDisappearBehindCollectionWithTheSameDisplayedTitleAndFirstAuthor()
    {
        var novel = Book("OL103123W");
        var school = Book("OL14925748W") with
        {
            Authors = ["Ray Bradbury", "Walter Van Tilburg Clark", "Kurt Vonnegut"],
            Description = "Contains: Fahrenheit 451 / novel by Ray Bradbury -- The portable phonograph / short story by Walter Van Tilburg Clark.",
            Publisher = "McDougal Littell"
        };
        var collection = Book("OL28185143W") with { WorkTitle = "Fahrenheit 451 (Fahrenheit 451 / Playground / Rock Cried Out)" };
        var results = BookCandidateGrouper.GroupMatchingCandidates([school, collection, novel], "Fahrenheit 451");
        Assert.HasCount(3, results);
        Assert.AreEqual(novel.ExternalId, results[0].ExternalId);
        Assert.IsTrue(results.Skip(1).All(candidate => BookCandidateVersion.Assess(candidate).Kind == "Collection"));
    }

    [TestMethod]
    [DataRow("Presented in comic book format.", "GraphicAdaptation")]
    [DataRow("Retells the story of Captain Ahab.", "Adaptation")]
    [DataRow("An abridged version for young readers.", "Abridged")]
    public void MobyDickVersionsWithMelvilleFirstRemainSeparate(string description, string kind)
    {
        var novel = Book("novel", "Moby Dick") with { Authors = ["Herman Melville", "Nigel Cliff"] };
        var other = novel with { ExternalId = "other", Description = description };
        var results = BookCandidateGrouper.GroupMatchingCandidates([other, novel], "Moby Dick");
        Assert.HasCount(2, results);
        Assert.AreEqual("novel", results[0].ExternalId);
        Assert.AreEqual(kind, BookCandidateVersion.Assess(other).Kind);
    }

    [TestMethod]
    public void GuideEvidenceInPublisherAndEditionIsVisibleWithoutDescription()
    {
        var guide = Book("guide") with { Description = null, Publisher = "Cliffs Notes",
            Editions = [new("Notes on Bradbury's Fahrenheit 451", "9780822004585", "Unknown format", null)] };
        Assert.AreEqual("StudyGuide", BookCandidateVersion.Assess(guide).Kind);
    }

    [TestMethod]
    public void UnknownVersionAndLanguageStayUnknownAndMissingAuthorsNeverMerge()
    {
        var unknown = Book("unknown") with { Description = null, Language = null };
        Assert.AreEqual("Unspecified", BookCandidateVersion.Assess(unknown).Kind);
        Assert.HasCount(2, BookCandidateGrouper.GroupMatchingCandidates([unknown, Book("known")]));
        Assert.HasCount(2, BookCandidateGrouper.GroupMatchingCandidates([unknown with { Authors = [] }, unknown with { Authors = [], ExternalId = "other" }]));
    }

    [TestMethod]
    public void CompatibleEditionsFromEverySourceAreRetained()
    {
        var first = Book("first") with { Editions = [new("Fahrenheit 451", "9780006546061", "Paperback", null, "en", "Publisher A")] };
        var second = Book("second") with { ProviderId = "other", Editions = [new("Fahrenheit 451", "9781451673319", "Hardcover", null, "en", "Publisher B")] };
        var result = BookCandidateGrouper.GroupMatchingCandidates([first, second]).Single();
        Assert.HasCount(2, result.Editions);
        Assert.HasCount(2, result.MergedSources);
    }

    [TestMethod]
    public void ExplicitGraphicSearchPrefersGraphicVersionOnTiedTitleEvidence()
    {
        var novel = Book("novel", "Moby Dick") with { Authors = ["Herman Melville"] };
        var graphic = novel with { ExternalId = "graphic", Description = "Presented in comic book format." };
        Assert.AreEqual("graphic", BookCandidateGrouper.GroupMatchingCandidates([novel, graphic], "Moby Dick graphic novel")[0].ExternalId);
    }

    [TestMethod]
    public void ContributorCountDoesNotOutvoteMoreDescriptiveMetadata()
    {
        var sparse = Book("sparse") with { Description = null, Authors = ["Ray Bradbury", "Editor A", "Editor B", "Editor C"] };
        var detailed = Book("detailed");
        Assert.AreEqual("detailed", BookCandidateGrouper.GroupMatchingCandidates([sparse, detailed]).Single().ExternalId);
    }
    [TestMethod]
    public void SparkNotesContributorIsAGuideAndAuthorlessStubDoesNotOutrankNovel()
    {
        var guide = Book("sparknotes") with { Authors = ["SparkNotes"], Description = null, Publisher = "Spark Publishing" };
        Assert.AreEqual("StudyGuide", BookCandidateVersion.Assess(guide).Kind);
        var novel = Book("novel");
        var unknown = novel with { Authors = [], ExternalId = "unknown" };
        Assert.AreEqual("novel", BookCandidateGrouper.GroupMatchingCandidates([unknown, novel], "Fahrenheit 451")[0].ExternalId);
    }
    [TestMethod]
    public void GroupingKeepsVersionEvidenceAfterCombiningSeveralEditionTitles()
    {
        var first = Book("first") with { Description = null, Editions = [new("Notes on Fahrenheit 451", "9780822004585", "Paperback", null)] };
        var second = first with { ExternalId = "second", Editions = [new("Notes on Fahrenheit 451", "9781451673319", "Hardcover", null)] };
        var grouped = BookCandidateGrouper.GroupMatchingCandidates([first, second]).Single();
        Assert.HasCount(2, grouped.Editions);
        Assert.AreEqual("StudyGuide", BookCandidateVersion.Assess(grouped).Kind);
        Assert.IsTrue(BookCandidateVersion.IsRelated(grouped));
    }
}
