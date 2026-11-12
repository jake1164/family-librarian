using FamilyLibrarian.Application.Matching;

namespace FamilyLibrarian.Infrastructure.Tests.Characterization;

/// <summary>
/// Regression guard for <see cref="IAmbiguityResolver"/> (alpha.6 owner requirement 1).
/// M1-0 pinned KnownGap R01: <see cref="BookMatchService"/> promoted ANY non-null
/// resolver pick straight to <see cref="BookMatchDecision.Match"/>. The R01 safety fix
/// made the resolver advisory (<see cref="BookMatchResult.AdvisoryPick"/>), so R01 is
/// now a resolved case that pins the corrected behavior. Broader scenarios live in
/// <c>BookMatchServiceTests</c>; the Web.Tests guard
/// <c>OnlyTheNoOpAmbiguityResolverExists</c> remains as defense in depth until M4-2.
/// </summary>
[TestClass]
public sealed class AmbiguityResolverCharacterizationTests
{
    private static readonly CharacterizationCase R01 = new(
        "R01",
        "A resolver returning a non-null pick on an Ambiguous result",
        "Ambiguous (advisory pick attached, not promoted)");

    internal static IReadOnlyList<CharacterizationCase> Cases { get; } = [R01];

    [TestMethod]
    public async Task ANonNullAdvisoryPickIsAttachedButNeverPromotedToAMatch()
    {
        var first = new CandidateBook("a", "Dune", "Frank Herbert");
        var second = new CandidateBook("b", "Dune", "Frank Herbert");
        Assert.AreEqual(BookMatchDecision.Ambiguous, new DeterministicBookMatcher().MatchByTitleAuthor("Dune", "Frank Herbert", [first, second]).Decision);
        var service = new BookMatchService(new DeterministicBookMatcher(), new PickFirstResolver());

        var result = await service.MatchByTitleAuthorAsync("Dune", "Frank Herbert", [first, second], CancellationToken.None);

        var actual = result is { Decision: BookMatchDecision.Ambiguous, MatchedId: null, AdvisoryPick.ExternalId: "a" }
            ? "Ambiguous (advisory pick attached, not promoted)"
            : $"{result.Decision} matched={result.MatchedId ?? "none"} pick={result.AdvisoryPick?.ExternalId ?? "none"}";
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
