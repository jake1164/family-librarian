namespace FamilyLibrarian.Infrastructure.Tests.Characterization;

/// <summary>
/// The explicit list of known gaps captured by M1-0. A later M1 slice that closes
/// a gap must change the case's Observed/Desired in its own file AND remove the id
/// here in the same PR, so a fix is always a visible, reviewed decision rather than
/// a silent outcome change.
/// </summary>
[TestClass]
public sealed class KnownGapRegistryTests
{
    private static readonly string[] ExpectedKnownGaps = ["A06", "C14", "E02"];

    private static IEnumerable<CharacterizationCase> AllCases() =>
        CandidateAssessmentCharacterizationTests.Cases.Select(item => item.Case)
            .Concat(ArtifactCharacterizationTests.Cases.Select(item => item.Case))
            .Concat(AmbiguityResolverCharacterizationTests.Cases);

    [TestMethod]
    public void CaseIdsAreUnique()
    {
        var duplicates = AllCases().GroupBy(c => c.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
        Assert.IsEmpty(duplicates, $"Duplicate case ids: {string.Join(", ", duplicates)}");
    }

    [TestMethod]
    public void TheSetOfKnownGapsIsExactlyTheApprovedBaseline()
    {
        var actual = AllCases().Where(c => c.IsKnownGap).Select(c => c.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(ExpectedKnownGaps, actual);
    }

    [TestMethod]
    public void EveryKnownGapDescribesADifferentOutcomeFromToday()
    {
        var same = AllCases().Where(c => c.IsKnownGap && c.Desired == c.Observed).Select(c => c.Id).ToArray();
        Assert.IsEmpty(same, $"Known gaps whose Desired equals Observed: {string.Join(", ", same)}");
    }
}
