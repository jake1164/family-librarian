using FamilyLibrarian.Application.Matching;

namespace FamilyLibrarian.Infrastructure.Tests.Matching;

/// <summary>
/// The lab: catalog "Seek the Traitor’s Son" was not recognised in release "Seek.the.Traitors.Son", because the apostrophe
/// split the catalog word in two while the release name never had one.
/// </summary>
[TestClass]
public sealed class ApostropheTitleMatchingTests
{
    private static readonly string[] ExpectedTokens = ["SEEK", "THE", "TRAITORS", "SON"];

    [TestMethod]
    [DataRow("Seek the Traitor’s Son")]
    [DataRow("Seek the Traitor's Son")]
    [DataRow("Seek the Traitorʼs Son")]
    public void AnApostropheInTheTitleDoesNotSplitTheWord(string title)
    {
        var release = ReleaseTitleMatcher.Tokens("Veronica.Roth.Seek.the.Traitors.Son.2026.RETAiL.EPUB.eBook-NODE");

        Assert.IsTrue(ReleaseTitleMatcher.Evaluate(title, release).IsPositive);
        CollectionAssert.AreEqual(ExpectedTokens, ReleaseTitleMatcher.Tokens(title));
    }

    [TestMethod]
    public void ADifferentTitleStillDoesNotMatch()
    {
        var release = ReleaseTitleMatcher.Tokens("Veronica.Roth.Seek.the.Traitors.Daughter.2026.EPUB");

        Assert.IsFalse(ReleaseTitleMatcher.Evaluate("Seek the Traitor’s Son", release).IsPositive);
    }
}
