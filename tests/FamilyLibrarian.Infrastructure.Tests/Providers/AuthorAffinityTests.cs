using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Providers;

namespace FamilyLibrarian.Infrastructure.Tests.Providers;

[TestClass]
public sealed class AuthorAffinityTests
{
    [TestMethod]
    [DataRow("Rebecca Yarros", AuthorAffinityKind.Exact, 100)]
    [DataRow("Yarros, Rebecca", AuthorAffinityKind.Exact, 100)]
    [DataRow("Rebecca L Yarros", AuthorAffinityKind.Compatible, 90)]
    [DataRow("R Yarros", AuthorAffinityKind.Compatible, 90)]
    [DataRow("R. Yarros", AuthorAffinityKind.Compatible, 90)]
    [DataRow("Rececca Yarros", AuthorAffinityKind.LastExactFirstFuzzy, 80)]
    [DataRow("Yarros", AuthorAffinityKind.LastOnly, 70)]
    [DataRow("Rebecca Yaros", AuthorAffinityKind.FirstExactLastFuzzy, 60)]
    [DataRow("Rebecca", AuthorAffinityKind.FirstOnly, 50)]
    [DataRow("Rebeca Yaros", AuthorAffinityKind.Fuzzy, 40)]
    [DataRow(null, AuthorAffinityKind.Unknown, 0)]
    [DataRow("Stephen King", AuthorAffinityKind.Conflict, -100)]
    [DataRow("Rebecca Ross", AuthorAffinityKind.Conflict, -100)]
    [DataRow("David Yarros", AuthorAffinityKind.Conflict, -100)]
    [DataRow("X Yarros", AuthorAffinityKind.Conflict, -100)]
    [DataRow("  REBECCA.  YARROS Jr. ", AuthorAffinityKind.Exact, 100)]
    public void StructuredNamesHaveExplicitEvidence(string? detected, AuthorAffinityKind kind, int score)
    {
        var result = AuthorAffinity.Evaluate("Rebecca Yarros", detected);
        Assert.AreEqual(kind, result.Kind);
        Assert.AreEqual(score, result.Score);
        Assert.AreEqual(detected, result.DetectedAuthor);
    }

    [TestMethod]
    [DataRow("Fourth Wing - Rebecca Yarros", AuthorAffinityKind.Exact, true)]
    [DataRow("Fourth Wing - Yarros, Rebecca", AuthorAffinityKind.Exact, true)]
    [DataRow("Fourth Wing - R Yarros", AuthorAffinityKind.Compatible, true)]
    [DataRow("Fourth Wing - R. Yarros", AuthorAffinityKind.Compatible, true)]
    [DataRow("Fourth.Wing.by.Rececca.Yarros", AuthorAffinityKind.LastExactFirstFuzzy, true)]
    [DataRow("Fourth Wing - Rebecca Yaros", AuthorAffinityKind.FirstExactLastFuzzy, true)]
    [DataRow("Fourth Wing - Rebeca Yaros", AuthorAffinityKind.Fuzzy, true)]
    [DataRow("Fourth Wing - Yarros", AuthorAffinityKind.LastOnly, true)]
    [DataRow("Fourth Wing - Rebecca", AuthorAffinityKind.FirstOnly, true)]
    [DataRow("Fourth.Wing.Book.1.m4b", AuthorAffinityKind.Unknown, true)]
    [DataRow("req.Fourth.Wing.Fourth.Wing.Book.1.m4b", AuthorAffinityKind.Unknown, true)]
    [DataRow("Fourth Wing by Stephen King", AuthorAffinityKind.Conflict, false)]
    [DataRow("Fourth Wing - Rebecca Ross", AuthorAffinityKind.Conflict, false)]
    [DataRow("Fourth Wing - David Yarros", AuthorAffinityKind.Conflict, false)]
    public void ReleaseEvidenceSeparatesAffinityFromAutomaticIdentity(string name, AuthorAffinityKind kind, bool strict)
    {
        var result = ExternalReleaseNameEvidence.Evaluate(name, ["Fourth Wing"], "Rebecca Yarros");
        Assert.IsTrue(result.AssertsExpectedTitle);
        Assert.AreEqual(kind, result.AuthorAffinity?.Kind);
        Assert.AreEqual(strict, result.IsStrictWorkAssertion);
    }

    [TestMethod]
    public void ConservativeTyposAndNormalization()
    {
        Assert.AreEqual(AuthorAffinityKind.Exact, AuthorAffinity.Evaluate("Yarros, Rebecca Jr.", "Rebecca Yarros").Kind);
        Assert.AreEqual(AuthorAffinityKind.Exact, AuthorAffinity.Evaluate("Jos\u00e9 Silva", "Jose\u0301 Silva").Kind);
        Assert.AreEqual(AuthorAffinityKind.Conflict, AuthorAffinity.Evaluate("Ann Lee", "Ana Lee").Kind);
        Assert.AreEqual(AuthorAffinityKind.Conflict, AuthorAffinity.Evaluate("Rebecca Yarros", "R").Kind);
        var typo = AuthorAffinity.Evaluate("Rebecca Yarros", "Rececca Yarros");
        Assert.AreEqual("fuzzy", typo.FirstNameMatch);
        Assert.AreEqual("exact", typo.LastNameMatch);
        Assert.IsFalse(ExternalReleaseNameEvidence.Evaluate(
            "Fourth Wing - Rececca Yarros - Part 2 Extended", ["Fourth Wing"], "Rebecca Yarros").IsStrictWorkAssertion);
    }
}
