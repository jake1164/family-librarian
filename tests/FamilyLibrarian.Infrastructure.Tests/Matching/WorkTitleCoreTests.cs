using FamilyLibrarian.Application.Matching;

namespace FamilyLibrarian.Infrastructure.Tests.Matching;

/// <summary>
/// Regressions for the live failure observed on the integration lab: three
/// requests sat in review with candidates nothing could confirm, because the
/// catalog title carried edition packaging the sources' titles did not.
/// </summary>
[TestClass]
public sealed class WorkTitleCoreTests
{
    [TestMethod]
    [DataRow("Moby Dick (Illustrated Classics)", "Moby Dick")]
    [DataRow("Moby Dick (Diversion Classics)", "Moby Dick")]
    [DataRow("Moby Dick [Annotated]", "Moby Dick")]
    [DataRow(
        "Threshing Day: Return to the Empyrean world with thirteen stories starring your favourite Fourth Wing characters.",
        "Threshing Day")]
    [DataRow("Threshing Day (Wing and Claw Collection)", "Threshing Day")]
    [DataRow("Fahrenheit 451 - A Novel", "Fahrenheit 451")]
    [DataRow("Moby Dick", "Moby Dick")]
    public void EditionPackagingIsReducedToTheWork(string title, string expected) =>
        Assert.AreEqual(expected, WorkTitleCore.Reduce(title));

    [TestMethod]
    [DataRow("Debt of Honor (Abridged)")]
    [DataRow("The Expanse (Books 1-3)")]
    [DataRow("Wheel of Time (Box Set)")]
    [DataRow("Dune: Messiah")]
    [DataRow("Mission: Impossible")]
    public void AQualifierThatChangesTheProductIsKept(string title) =>
        Assert.AreEqual(title, WorkTitleCore.Reduce(title));

    [TestMethod]
    public void ATrailingAuthorIsDroppedOnlyWhenItIsTheExpectedAuthor()
    {
        Assert.AreEqual("Moby Dick", WorkTitleCore.Reduce("Moby Dick by Herman Melville", "Herman Melville"));
        Assert.AreEqual("Moby Dick by Herman Melville", WorkTitleCore.Reduce("Moby Dick by Herman Melville"));
    }

    [TestMethod]
    public void AnOrdinaryTitleContainingByIsNeverTruncated() =>
        Assert.AreEqual(
            "Death by Black Hole",
            WorkTitleCore.Reduce("Death by Black Hole", "Neil deGrasse Tyson"));

    [TestMethod]
    public void ReductionNeverEmptiesATitle() =>
        Assert.AreEqual("(Annotated)", WorkTitleCore.Reduce("(Annotated)"));
}
