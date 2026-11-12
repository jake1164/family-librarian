using System.Text.Json;
using FamilyLibrarian.Application.SemanticAssessment;
using FamilyLibrarian.Domain.Evidence;

namespace FamilyLibrarian.Infrastructure.Tests.SemanticAssessment;

[TestClass]
public sealed class CandidateTriageParserTests
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<EvidenceFinding>> Supplied =
        new Dictionary<string, IReadOnlyList<EvidenceFinding>>(StringComparer.Ordinal)
        {
            ["c1"] = [new("c1.ev-title", "candidate.title.exact", EvidencePolarity.Positive, "Title matches."),
                new("c1.ev-author", "candidate.author.conflict", EvidencePolarity.Negative, "Author differs."),
                new("c1.ev-series-1", "candidate.series.exact", EvidencePolarity.Positive, "Series and position agree."),
                new("c1.ev-condition-1", "candidate.condition.sample", EvidencePolarity.Condition, "Sample."),
                new("c1.ev-year", "candidate.year.exact", EvidencePolarity.Positive, "Year agrees."),
                new("c1.ev-publisher", "candidate.imprint.exact", EvidencePolarity.Positive, "Publisher agrees.")],
            ["c2"] = [new("c2.ev-title", "candidate.title.exact", EvidencePolarity.Positive, "Title matches.")]
        };

    private static string Reply(string move, string? candidate, params string[] ids) => JsonSerializer.Serialize(new
        { schema = "candidate-triage/1", move, candidate, evidence_ids = ids, note = "A grounded comparison." });

    private static bool Valid(string? text) => SemanticAssessmentParser.ParseCandidateTriage(text, Supplied).IsValid;

    [TestMethod]
    [DataRow("keep")]
    [DataRow("uncertain")]
    public void NonMovesRequireNullCandidate(string move)
    {
        Assert.IsTrue(Valid(Reply(move, null)));
        Assert.IsFalse(Valid(Reply(move, "c1")));
    }

    [TestMethod]
    public void GroundedMovesRequireTheirOwnEvidence()
    {
        Assert.IsTrue(Valid(Reply("veto", "c1", "c1.ev-author")));
        Assert.IsTrue(Valid(Reply("veto", "c1", "c1.ev-condition-1")));
        Assert.IsFalse(Valid(Reply("veto", "c1", "c1.ev-title")));
        Assert.IsTrue(Valid(Reply("prefer", "c2", "c2.ev-title")));
        Assert.IsFalse(Valid(Reply("prefer", "c1", "c1.ev-author")));
        Assert.IsFalse(Valid(Reply("prefer", "c1", "c2.ev-title")));
    }

    [TestMethod]
    public void AuthorVariantsRequireTitleAndIndependentCorroboration()
    {
        Assert.IsTrue(Valid(Reply("author_variant", "c1", "c1.ev-title", "c1.ev-series-1")));
        Assert.IsTrue(Valid(Reply("author_variant", "c1", "c1.ev-title", "c1.ev-year", "c1.ev-publisher")));
        Assert.IsFalse(Valid(Reply("author_variant", "c1", "c1.ev-title")));
        Assert.IsFalse(Valid(Reply("author_variant", "c1", "c1.ev-title", "c1.ev-year")));
        Assert.IsFalse(Valid(Reply("author_variant", "c1", "c1.ev-series-1")));
    }

    [TestMethod]
    [DataRow("download", "c1", "c1.ev-title")]
    [DataRow("prefer", "c99", "c1.ev-title")]
    [DataRow("prefer", "https://example.test", "c1.ev-title")]
    [DataRow("prefer", "c1", "c1.ev-invented")]
    [DataRow("prefer", null, "c1.ev-title")]
    public void UnknownMovesCandidatesAndEvidenceAreRejected(string move, string? candidate, string id) =>
        Assert.IsFalse(Valid(Reply(move, candidate, id)));

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("not json")]
    [DataRow("{}")]
    [DataRow("{\"schema\":\"candidate-triage/1\",\"move\":\"keep\",\"candidate\":null,\"evidence_ids\":[],\"note\":\"OK\",\"target\":\"c1\"}")]
    [DataRow("{\"schema\":\"candidate-triage/1\",\"move\":\"keep\",\"move\":\"veto\",\"candidate\":null,\"evidence_ids\":[],\"note\":\"OK\"}")]
    public void MalformedAndNonExactSchemasAreRejected(string? value) => Assert.IsFalse(Valid(value));

    [TestMethod]
    public void BoundsDuplicateCitationsNotesAndReply()
    {
        Assert.IsFalse(Valid(Reply("prefer", "c1", "c1.ev-title", "c1.ev-title")));
        Assert.IsFalse(Valid(Reply("prefer", "c1", "c1.ev-title").Replace("A grounded comparison.", "<script>", StringComparison.Ordinal)));
        Assert.IsFalse(Valid(Reply("keep", null).Replace("A grounded comparison.", new string('x', 201), StringComparison.Ordinal)));
        Assert.IsFalse(Valid(new string(' ', 4001)));
    }
}
