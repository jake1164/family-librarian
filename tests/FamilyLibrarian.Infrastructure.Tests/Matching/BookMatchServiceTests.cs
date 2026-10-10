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
        Assert.AreEqual(BookMatchBasis.TitleAuthor, result.Basis);
    }

    [TestMethod]
    public async Task ResolveUniqueStampsAnIdentifierBasisMatch()
    {
        var service = new BookMatchService(new DeterministicBookMatcher(), new NoOpAmbiguityResolver());
        var candidate = new CandidateBook("1", "Debt of Honor", "Tom Clancy");

        var result = await service.ResolveUniqueAsync(
            "Debt of Honor", "Tom Clancy", [candidate], CancellationToken.None);

        Assert.AreEqual(BookMatchDecision.Match, result.Decision);
        Assert.AreEqual(BookMatchBasis.Identifier, result.Basis);
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
        Assert.IsNull(result.AdvisoryPick);
    }

    [TestMethod]
    public async Task AResolverThatForcesAPickCanNeverPromoteAnAmbiguousResultToAMatch()
    {
        var candidates = new[]
        {
            new CandidateBook("1", "Debt of Honor", "Tom Clancy"),
            new CandidateBook("2", "Debt of Honor", "Tom Clancy")
        };
        var service = new BookMatchService(new DeterministicBookMatcher(), new PicksSecondCandidateResolver());

        var result = await service.MatchByTitleAuthorAsync("Debt of Honor", "Tom Clancy", candidates, CancellationToken.None);

        Assert.AreEqual(BookMatchDecision.Ambiguous, result.Decision);
        Assert.IsNull(result.MatchedId);
        Assert.IsNull(result.Basis);
        Assert.HasCount(2, result.Candidates);
        // The suggestion is preserved as advice for a reviewer, not as a decision.
        Assert.AreEqual("2", result.AdvisoryPick?.ExternalId);
    }

    [TestMethod]
    public async Task ResolveUniqueAlsoTreatsTheResolverAsAdvisoryOnly()
    {
        var candidates = new[]
        {
            new CandidateBook("1", "Debt of Honor", "Tom Clancy"),
            new CandidateBook("2", "Debt of Honor", "Tom Clancy")
        };
        var service = new BookMatchService(new DeterministicBookMatcher(), new PicksSecondCandidateResolver());

        var result = await service.ResolveUniqueAsync("Debt of Honor", "Tom Clancy", candidates, CancellationToken.None);

        Assert.AreEqual(BookMatchDecision.Ambiguous, result.Decision);
        Assert.IsNull(result.MatchedId);
        Assert.AreEqual("2", result.AdvisoryPick?.ExternalId);
    }

    [TestMethod]
    public async Task AForcingResolverIsNotConsultedForAnythingButAnAmbiguousResult()
    {
        var resolver = new CountingForcingResolver();
        var service = new BookMatchService(new DeterministicBookMatcher(), resolver);

        var noMatch = await service.MatchByTitleAuthorAsync("Debt of Honor", "Tom Clancy", [], CancellationToken.None);
        var wrongBook = await service.MatchByTitleAuthorAsync(
            "Debt of Honor", "Tom Clancy", [new CandidateBook("9", "Rainbow Six", "Tom Clancy")], CancellationToken.None);
        var languageExcluded = await service.MatchByTitleAuthorAsync(
            "Moby Dick", "Herman Melville", [new CandidateBook("1", "Moby Dick", "Herman Melville", Language: "spa")], CancellationToken.None);
        var exact = await service.MatchByTitleAuthorAsync(
            "Debt of Honor", "Tom Clancy", [new CandidateBook("1", "Debt of Honor", "Tom Clancy")], CancellationToken.None);

        Assert.AreEqual(BookMatchDecision.NoMatch, noMatch.Decision);
        Assert.AreEqual(BookMatchDecision.NoMatch, wrongBook.Decision);
        Assert.AreEqual(BookMatchDecision.LanguageExcluded, languageExcluded.Decision);
        Assert.AreEqual(BookMatchDecision.Match, exact.Decision);
        Assert.AreEqual("1", exact.MatchedId);
        Assert.IsNull(exact.AdvisoryPick);
        Assert.AreEqual(0, resolver.Calls);
    }

    [TestMethod]
    public async Task APickThatIsNotOneOfTheOfferedCandidatesIsIgnored()
    {
        var candidates = new[]
        {
            new CandidateBook("1", "Debt of Honor", "Tom Clancy"),
            new CandidateBook("2", "Debt of Honor", "Tom Clancy")
        };
        var service = new BookMatchService(
            new DeterministicBookMatcher(), new CountingForcingResolver(new CandidateBook("99", "Invented", "Nobody")));

        var result = await service.MatchByTitleAuthorAsync("Debt of Honor", "Tom Clancy", candidates, CancellationToken.None);

        Assert.AreEqual(BookMatchDecision.Ambiguous, result.Decision);
        Assert.IsNull(result.AdvisoryPick);
    }

    [TestMethod]
    public async Task AFailingResolverLeavesTheDeterministicAmbiguousResultIntact()
    {
        var candidates = new[]
        {
            new CandidateBook("1", "Debt of Honor", "Tom Clancy"),
            new CandidateBook("2", "Debt of Honor", "Tom Clancy")
        };

        foreach (var failure in new Exception[] { new InvalidOperationException("model down"), new OperationCanceledException("resolver timeout") })
        {
            var service = new BookMatchService(new DeterministicBookMatcher(), new ThrowingResolver(failure));

            var result = await service.MatchByTitleAuthorAsync("Debt of Honor", "Tom Clancy", candidates, CancellationToken.None);

            Assert.AreEqual(BookMatchDecision.Ambiguous, result.Decision);
            Assert.HasCount(2, result.Candidates);
        }
    }

    [TestMethod]
    public async Task TheCallersOwnCancellationStillPropagatesFromTheResolver()
    {
        var candidates = new[]
        {
            new CandidateBook("1", "Debt of Honor", "Tom Clancy"),
            new CandidateBook("2", "Debt of Honor", "Tom Clancy")
        };
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var service = new BookMatchService(new DeterministicBookMatcher(), new ThrowingResolver(new OperationCanceledException()));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.MatchByTitleAuthorAsync("Debt of Honor", "Tom Clancy", candidates, cancellation.Token));
    }

    [TestMethod]
    public async Task ALanguageExcludedResultPassesThroughUnchangedWithNoBasisStamped()
    {
        var service = new BookMatchService(new DeterministicBookMatcher(), new NoOpAmbiguityResolver());
        var candidate = new CandidateBook("1", "Moby Dick", "Herman Melville", Language: "spa");

        var result = await service.MatchByTitleAuthorAsync(
            "Moby Dick", "Herman Melville", [candidate], CancellationToken.None);

        Assert.AreEqual(BookMatchDecision.LanguageExcluded, result.Decision);
        Assert.IsNull(result.Basis);
        Assert.AreEqual(1, result.Candidates.Count);
    }

    private sealed class PicksSecondCandidateResolver : IAmbiguityResolver
    {
        public Task<CandidateBook?> ResolveAsync(
            string title, string? author, IReadOnlyList<CandidateBook> candidates, CancellationToken cancellationToken) =>
            Task.FromResult<CandidateBook?>(candidates[1]);
    }

    private sealed class CountingForcingResolver(CandidateBook? forced = null) : IAmbiguityResolver
    {
        public int Calls { get; private set; }

        public Task<CandidateBook?> ResolveAsync(
            string title, string? author, IReadOnlyList<CandidateBook> candidates, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<CandidateBook?>(forced ?? new CandidateBook("1", title, author));
        }
    }

    private sealed class ThrowingResolver(Exception exception) : IAmbiguityResolver
    {
        public Task<CandidateBook?> ResolveAsync(
            string title, string? author, IReadOnlyList<CandidateBook> candidates, CancellationToken cancellationToken) =>
            throw exception;
    }
}
