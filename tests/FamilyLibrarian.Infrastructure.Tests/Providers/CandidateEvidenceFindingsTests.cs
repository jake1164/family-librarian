using System.Reflection;
using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Domain.Evidence;
using FamilyLibrarian.Infrastructure.Tests.Characterization;

namespace FamilyLibrarian.Infrastructure.Tests.Providers;

/// <summary>
/// M1-1: candidate evidence findings are stable, citable and purely descriptive. The
/// 14 M1-0 fixtures are reused so the same inputs that pin the matching decisions
/// (see <see cref="CandidateAssessmentCharacterizationTests"/>, unchanged) also pin the findings.
/// </summary>
[TestClass]
public sealed class CandidateEvidenceFindingsTests
{
    private static HashSet<string> Catalog() =>
        typeof(EvidenceReasonCodes).GetNestedTypes(BindingFlags.Public | BindingFlags.Static)
            .SelectMany(group => group.GetFields(BindingFlags.Public | BindingFlags.Static).Where(field => field.IsLiteral))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

    private static Dictionary<string, CandidateIdentityAssessment> Assessments() =>
        CandidateAssessmentCharacterizationTests.Cases.ToDictionary(
            item => item.Case.Id,
            item => DeterministicCandidateIdentityResolver.Assess(CandidateAssessmentCharacterizationTests.Requested, item.Input),
            StringComparer.Ordinal);

    [TestMethod]
    public void EveryEvidenceStateMapsToACodeInTheDomainCatalog()
    {
        var catalog = Catalog();
        foreach (var state in Enum.GetValues<IdentityEvidenceState>())
        {
            foreach (var facet in new[] { "title", "language", "series" })
                Assert.Contains(CandidateEvidenceFindings.Code(facet, state.ToString()), catalog, $"{facet}/{state}");
        }

        foreach (var kind in Enum.GetValues<AuthorAffinityKind>())
            Assert.Contains(CandidateEvidenceFindings.Code("author", kind.ToString()), catalog, $"author/{kind}");
    }

    [TestMethod]
    public void EveryFindingForEveryFixtureHasAUniqueIdAndACatalogCode()
    {
        var catalog = Catalog();
        foreach (var (id, assessment) in Assessments())
        {
            Assert.IsNotEmpty(assessment.Findings, id);
            Assert.HasCount(assessment.Findings.Select(finding => finding.Id).Distinct().Count(), assessment.Findings, $"{id}: duplicate finding ids");
            foreach (var finding in assessment.Findings)
            {
                Assert.Contains(finding.Code, catalog, $"{id}: {finding.Code} is not in the catalog");
                Assert.IsFalse(string.IsNullOrWhiteSpace(finding.Text), $"{id}/{finding.Id}: empty text");
            }
        }
    }

    [TestMethod]
    public void FindingsAreDeterministicAndOrderedIdentically()
    {
        var first = Assessments();
        var second = Assessments();
        foreach (var (id, assessment) in first)
            CollectionAssert.AreEqual(assessment.Findings.ToArray(), second[id].Findings.ToArray(), id);
    }

    [TestMethod]
    public void ContradictionFindingsAgreeWithTheContradictionList()
    {
        foreach (var (id, assessment) in Assessments())
        {
            var negative = assessment.Findings.Count(finding => finding.Id.StartsWith("ev-contradiction-", StringComparison.Ordinal));
            Assert.AreEqual(assessment.Contradictions.Count, negative, $"{id}: findings and contradictions disagree");
        }
    }

    [TestMethod]
    public void ConditionFindingsAgreeWithTheConditionList()
    {
        foreach (var (id, assessment) in Assessments())
        {
            var conditions = assessment.Findings.Count(finding => finding.Id.StartsWith("ev-condition-", StringComparison.Ordinal));
            Assert.AreEqual(assessment.Conditions.Count, conditions, $"{id}");
        }
    }

    [TestMethod]
    [DataRow("C02", "ev-contradiction-title", "candidate.contradiction.title")]
    [DataRow("C03", "ev-contradiction-series-1", "candidate.contradiction.series_position")]
    [DataRow("C04", "ev-contradiction-author", "candidate.contradiction.author")]
    [DataRow("C10", "ev-contradiction-language", "candidate.contradiction.language")]
    [DataRow("C05", "ev-condition-1", "candidate.condition.qualifier_review")]
    [DataRow("C05", "ev-condition-2", "candidate.condition.qualifier_review")]
    [DataRow("C06", "ev-condition-1", "candidate.condition.collection")]
    [DataRow("C07", "ev-condition-1", "candidate.condition.sample")]
    [DataRow("C09", "ev-condition-1", "candidate.condition.abridged")]
    [DataRow("C14", "ev-decision", "candidate.decision.insufficient_evidence")]
    public void SpecificFindingsCarryTheDocumentedStableCodes(string caseId, string findingId, string expectedCode)
    {
        var finding = Assessments()[caseId].Findings.Single(item => item.Id == findingId);
        Assert.AreEqual(expectedCode, finding.Code);
    }

    [TestMethod]
    public void UnknownEvidenceIsReportedAsUnknownNeverAsNegative()
    {
        var unknownLanguage = Assessments()["C11"];
        var language = unknownLanguage.Findings.Single(finding => finding.Id == "ev-language");
        Assert.AreEqual("candidate.language.unknown", language.Code);
        Assert.AreEqual(EvidencePolarity.Unknown, language.Polarity);
        Assert.IsFalse(unknownLanguage.Findings.Any(finding => finding.Polarity == EvidencePolarity.Negative));

        // C13 keeps its current Match decision; the missing author stays visibly unknown.
        var titleOnly = Assessments()["C13"];
        Assert.AreEqual(WorkIdentityDecision.Match, titleOnly.Decision);
        var author = titleOnly.Findings.Single(finding => finding.Id == "ev-author");
        Assert.AreEqual("candidate.author.unknown", author.Code);
        Assert.AreEqual(EvidencePolarity.Unknown, author.Polarity);
    }

    [TestMethod]
    public void FindingsOnlyDescribeTheDecisionAndNeverChangeIt()
    {
        // The decision recorded on the assessment is exactly what the pre-findings
        // resolver produced for these inputs (pinned independently by the M1-0 baseline).
        foreach (var (fixture, input) in CandidateAssessmentCharacterizationTests.Cases)
        {
            var assessment = DeterministicCandidateIdentityResolver.Assess(CandidateAssessmentCharacterizationTests.Requested, input);
            var withoutFindings = assessment with { Findings = [] };
            Assert.AreEqual(
                CandidateAssessmentCharacterizationTests.Describe(withoutFindings),
                CandidateAssessmentCharacterizationTests.Describe(assessment),
                fixture.Id);
        }
    }
}
