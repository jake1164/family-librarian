using FamilyLibrarian.Application.Matching;

namespace FamilyLibrarian.Infrastructure.Tests.Matching;

[TestClass]
public sealed class DeterministicBookMatcherTests
{
    private readonly DeterministicBookMatcher matcher = new();

    [TestMethod]
    public void ResolveUniqueWithNoCandidatesIsNoMatch()
    {
        var result = matcher.ResolveUnique([]);

        Assert.AreEqual(BookMatchDecision.NoMatch, result.Decision);
    }

    [TestMethod]
    public void ResolveUniqueWithOneCandidateIsAMatch()
    {
        var candidate = new CandidateBook("1", "Debt of Honor", "Tom Clancy");

        var result = matcher.ResolveUnique([candidate]);

        Assert.AreEqual(BookMatchDecision.Match, result.Decision);
        Assert.AreEqual("1", result.MatchedId);
    }

    [TestMethod]
    public void ResolveUniqueWithMultipleCandidatesIsAmbiguous()
    {
        var result = matcher.ResolveUnique(
            [new CandidateBook("1", "Debt of Honor", "Tom Clancy"), new CandidateBook("2", "Debt of Honor", "Tom Clancy")]);

        Assert.AreEqual(BookMatchDecision.Ambiguous, result.Decision);
        Assert.AreEqual(2, result.Candidates.Count);
    }

    [TestMethod]
    public void ATitleAndAuthorMatchIsCaseInsensitive()
    {
        var candidate = new CandidateBook("1", "DEBT OF HONOR", "tom clancy");

        var result = matcher.MatchByTitleAuthor("Debt of Honor", "Tom Clancy", [candidate]);

        Assert.AreEqual(BookMatchDecision.Match, result.Decision);
    }

    [TestMethod]
    public void ACandidateWithNoAuthorInformationIsNotExcludedByARequestedAuthor()
    {
        var candidate = new CandidateBook("1", "Debt of Honor", null);

        var result = matcher.MatchByTitleAuthor("Debt of Honor", "Tom Clancy", [candidate]);

        Assert.AreEqual(BookMatchDecision.Match, result.Decision);
    }

    [TestMethod]
    public void AConflictingAuthorExcludesAnOtherwiseTitleMatchingCandidate()
    {
        var candidate = new CandidateBook("1", "Debt of Honor", "Someone Else");

        var result = matcher.MatchByTitleAuthor("Debt of Honor", "Tom Clancy", [candidate]);

        Assert.AreEqual(BookMatchDecision.NoMatch, result.Decision);
    }

    [TestMethod]
    public void TwoDistinctTitleMatchesAreAmbiguous()
    {
        var result = matcher.MatchByTitleAuthor(
            "Debt of Honor", "Tom Clancy",
            [
                new CandidateBook("1", "Debt of Honor", "Tom Clancy"),
                new CandidateBook("2", "Debt of Honor", "Tom Clancy")
            ]);

        Assert.AreEqual(BookMatchDecision.Ambiguous, result.Decision);
    }

    [TestMethod]
    public void ASummaryEditionIsNotTreatedAsAMatchEvenThoughItContainsTheTitle()
    {
        var candidate = new CandidateBook("1", "Summary of Debt of Honor by Tom Clancy", "Some Summarizer");

        var result = matcher.MatchByTitleAuthor("Debt of Honor", "Tom Clancy", [candidate]);

        Assert.AreEqual(BookMatchDecision.NoMatch, result.Decision);
    }

    [TestMethod]
    public void ACombinedEditionIsNotTreatedAsAMatchEvenThoughItContainsTheTitle()
    {
        var candidate = new CandidateBook("1", "Debt of Honor / Executive Orders", "Tom Clancy");

        var result = matcher.MatchByTitleAuthor("Debt of Honor", "Tom Clancy", [candidate]);

        Assert.AreEqual(BookMatchDecision.NoMatch, result.Decision);
    }

    [TestMethod]
    public void AWorkWhoseRealTitleIsAnUnwantedVariantMarkerIsStillMatchedExactly()
    {
        // "Box Set" is a derivative-title marker, but an exact title match
        // must still win -- some works are legitimately titled that way.
        var candidate = new CandidateBook("1", "Box Set", "Some Author");

        var result = matcher.MatchByTitleAuthor("Box Set", "Some Author", [candidate]);

        Assert.AreEqual(BookMatchDecision.Match, result.Decision);
    }
}
