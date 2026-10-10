using FamilyLibrarian.Application.Matching;

namespace FamilyLibrarian.Infrastructure.Tests.Characterization;

/// <summary>
/// M1-0 regression guard for <see cref="IAmbiguityResolver"/> (alpha.6 owner
/// requirement 1). Today <see cref="BookMatchService"/> promotes ANY non-null
/// resolver pick straight to <see cref="BookMatchDecision.Match"/>, which would let
/// advisory (e.g. model) output independently produce a match. The Web.Tests
/// guard <c>OnlyTheNoOpAmbiguityResolverExists</c> keeps that unreachable by
/// forbidding any other implementation; this test pins the contract defect itself
/// as KnownGap R01 so it cannot be forgotten. Fixing it is production work
/// (D-7) and is deliberately not part of M1-0.
/// </summary>
[TestClass]
public sealed class AmbiguityResolverCharacterizationTests
{
    private static readonly CharacterizationCase R01 = new(
        "R01",
        "A resolver returning a non-null pick on an Ambiguous result",
        "Match (advisory pick promoted to a match)",
        "Ambiguous (advisory output never produces a Match by itself)");

    internal static IReadOnlyList<CharacterizationCase> Cases { get; } = [R01];

    [TestMethod]
    public async Task ANonNullAdvisoryPickIsCurrentlyPromotedToAMatch()
    {
        var first = new CandidateBook("a", "Dune", "Frank Herbert");
        var second = new CandidateBook("b", "Dune", "Frank Herbert");
        Assert.AreEqual(BookMatchDecision.Ambiguous, new DeterministicBookMatcher().MatchByTitleAuthor("Dune", "Frank Herbert", [first, second]).Decision);
        var service = new BookMatchService(new DeterministicBookMatcher(), new PickFirstResolver());

        var result = await service.MatchByTitleAuthorAsync("Dune", "Frank Herbert", [first, second], CancellationToken.None);

        var actual = result.Decision == BookMatchDecision.Match ? "Match (advisory pick promoted to a match)" : result.Decision.ToString();
        Assert.AreEqual(R01.Observed, actual);
    }

    [TestMethod]
    public async Task ANullAdvisoryPickLeavesTheResultAmbiguous()
    {
        var candidates = new[] { new CandidateBook("a", "Dune", "Frank Herbert"), new CandidateBook("b", "Dune", "Frank Herbert") };
        var service = new BookMatchService(new DeterministicBookMatcher(), new NoOpAmbiguityResolver());

        var result = await service.MatchByTitleAuthorAsync("Dune", "Frank Herbert", candidates, CancellationToken.None);

        Assert.AreEqual(BookMatchDecision.Ambiguous, result.Decision);
        Assert.IsNull(result.MatchedId);
    }

    private sealed class PickFirstResolver : IAmbiguityResolver
    {
        public Task<CandidateBook?> ResolveAsync(
            string title, string? author, IReadOnlyList<CandidateBook> candidates, CancellationToken cancellationToken) =>
            Task.FromResult<CandidateBook?>(candidates[0]);
    }
}
