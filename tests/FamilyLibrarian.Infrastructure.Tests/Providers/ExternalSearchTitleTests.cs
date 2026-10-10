using FamilyLibrarian.Application.Providers;

namespace FamilyLibrarian.Infrastructure.Tests.Providers;

[TestClass]
public sealed class ExternalSearchTitleTests
{
    [TestMethod]
    [DataRow(
        "Threshing Day: Return to the Empyrean world with thirteen stories starring your favourite Fourth Wing characters.",
        "Threshing Day")]
    [DataRow("Threshing Day (Wing and Claw Collection)", "Threshing Day")]
    [DataRow("Fahrenheit 451 - A Novel", "Fahrenheit 451")]
    [DataRow("Moby Dick", "Moby Dick")]
    // A one-word core with a short subtitle is part of the title, not noise.
    [DataRow("Dune: Messiah", "Dune: Messiah")]
    [DataRow("Mission: Impossible", "Mission: Impossible")]
    public void BuildDropsASubtitleOnlyWhenTheCoreTitleStillIdentifiesTheBook(string title, string expected) =>
        Assert.AreEqual(expected, ExternalSearchTitle.Build(title));
}
