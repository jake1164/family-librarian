using FamilyLibrarian.Application.Matching;

namespace FamilyLibrarian.Infrastructure.Tests.Matching;

[TestClass]
public sealed class BookMatchServiceTests
{
    [TestMethod]
    public async Task AUniqueMatchPassesThroughUnchanged()
    {
        var service = new BookMatchService(new DeterministicBookMatcher(), new NoOpAmbiguityResolver());
        var candidate = new CandidateBook("1", "Debt of Honor", "Tom Clancy");

        var result = await service.MatchByTitleAuthorAsync(
            "Debt of Honor", "Tom Clancy", [candidate], CancellationToken.None);

        Assert.AreEqual(BookMatchDecision.Match, result.Decision);
        Assert.AreEqual("1", result.MatchedId);
    }

    [TestMethod]
    public async Task ANoMatchPassesThroughUnchanged()
    {
        var service = new BookMatchService(new DeterministicBookMatcher(), new NoOpAmbiguityResolver());

        var result = await service.MatchByTitleAuthorAsync("Debt of Honor", "Tom Clancy", [], CancellationToken.None);

        Assert.AreEqual(BookMatchDecision.NoMatch, result.Decision);
    }

    [TestMethod]
    public async Task AnAmbiguousResultStaysAmbiguousWithTheDefaultNoOpResolver()
    {
        var service = new BookMatchService(new DeterministicBookMatcher(), new NoOpAmbiguityResolver());
        var candidates = new[]
        {
            new CandidateBook("1", "Debt of Honor", "Tom Clancy"),
            new CandidateBook("2", "Debt of Honor", "Tom Clancy")
        };

        var result = await service.MatchByTitleAuthorAsync("Debt of Honor", "Tom Clancy", candidates, CancellationToken.None);

        Assert.AreEqual(BookMatchDecision.Ambiguous, result.Decision);
        Assert.AreEqual(2, result.Candidates.Count);
    }

    [TestMethod]
    public async Task AResolverThatPicksACandidatePromotesTheResultToAMatch()
    {
        var candidates = new[]
        {
            new CandidateBook("1", "Debt of Honor", "Tom Clancy"),
            new CandidateBook("2", "Debt of Honor", "Tom Clancy")
        };
        var service = new BookMatchService(new DeterministicBookMatcher(), new PicksSecondCandidateResolver());

        var result = await service.MatchByTitleAuthorAsync("Debt of Honor", "Tom Clancy", candidates, CancellationToken.None);

        Assert.AreEqual(BookMatchDecision.Match, result.Decision);
        Assert.AreEqual("2", result.MatchedId);
    }

    [TestMethod]
    public async Task ResolveUniqueAlsoRoutesThroughTheAmbiguityResolver()
    {
        var candidates = new[]
        {
            new CandidateBook("1", "Debt of Honor", "Tom Clancy"),
            new CandidateBook("2", "Debt of Honor", "Tom Clancy")
        };
        var service = new BookMatchService(new DeterministicBookMatcher(), new PicksSecondCandidateResolver());

        var result = await service.ResolveUniqueAsync("Debt of Honor", "Tom Clancy", candidates, CancellationToken.None);

        Assert.AreEqual(BookMatchDecision.Match, result.Decision);
        Assert.AreEqual("2", result.MatchedId);
    }

    private sealed class PicksSecondCandidateResolver : IAmbiguityResolver
    {
        public Task<CandidateBook?> ResolveAsync(
            string title, string? author, IReadOnlyList<CandidateBook> candidates, CancellationToken cancellationToken) =>
            Task.FromResult<CandidateBook?>(candidates[1]);
    }
}
