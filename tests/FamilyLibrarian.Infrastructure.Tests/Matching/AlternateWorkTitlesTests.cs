using FamilyLibrarian.Application.Matching;

namespace FamilyLibrarian.Infrastructure.Tests.Matching;

[TestClass]
public sealed class AlternateWorkTitlesTests
{
    private static readonly string[] Authors = ["Rebecca Yarros"];

    // The lab case: an edition of "Onyx Storm" titled with the author's name made "Iron.Flame.by.Rebecca.Yarros" match.
    [TestMethod]
    [DataRow("Rebecca Yarros")]
    [DataRow("Yarros, Rebecca")]
    [DataRow("REBECCA YARROS")]
    [DataRow("Yarros")]
    // Reduces to "Rebecca Yarros" (subtitle after " - " removed), the form search and acceptance use: the second lab run.
    [DataRow("Rebecca Yarros - Onyx Storm (The Empyrean Book 3) (2025)")]
    public void ATitleThatIsOnlyTheAuthorsNameIsNotUsable(string title) =>
        Assert.IsFalse(AlternateWorkTitles.IsUsable(title, Authors));

    [TestMethod]
    [DataRow("Alas de ónix")]
    [DataRow("Onyx Storm Rebecca Yarros")]
    [DataRow("Onyx Storm (Wing and Claw Collection)")]
    public void ATitleWithItsOwnWordsIsKept(string title) =>
        Assert.IsTrue(AlternateWorkTitles.IsUsable(title, Authors));

    [TestMethod]
    public void EveryCreditedAuthorIsConsidered() =>
        Assert.IsFalse(AlternateWorkTitles.IsUsable("Jack Carr", ["Rebecca Yarros", "Carr, Jack (Joint pseudonym)"]));

    [TestMethod]
    public void AnEmptyOrPunctuationOnlyTitleIsNotUsable() =>
        Assert.IsFalse(AlternateWorkTitles.IsUsable(" - ", Authors));

    [TestMethod]
    public void WithNoKnownAuthorAnyTitleWithWordsIsKept() =>
        Assert.IsTrue(AlternateWorkTitles.IsUsable("Rebecca Yarros", [null, " "]));
}
