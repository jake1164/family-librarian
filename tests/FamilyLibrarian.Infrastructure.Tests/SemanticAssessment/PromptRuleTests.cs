using FamilyLibrarian.Application.SemanticAssessment;
using FamilyLibrarian.Domain.Evidence;

namespace FamilyLibrarian.Infrastructure.Tests.SemanticAssessment;

/// <summary>
/// AI-PROMPT-RULES-1. Live lab replies broke three rules the prompts only implied. The prompts now state each rule outright, and the
/// parser keeps rejecting the captured replies, so a prompt change can never become a loosened safety check.
/// </summary>
[TestClass]
public sealed class PromptRuleTests
{
    [TestMethod]
    public void TheAmbiguityPromptSaysMissingEvidenceIsUncertainNeverAContradiction()
    {
        var prompt = CandidateSemanticEvidence.SystemPrompt;
        StringAssert.Contains(prompt, "A missing, unknown or weak match is not a contradiction");
        StringAssert.Contains(prompt, "never use contradicts_same_work or the verdict inconsistent");
    }

    [TestMethod]
    public void TheTriagePromptNeverAllowsPreferringTheCurrentPickAndSaysATieMeansKeep()
    {
        var prompt = CandidateTriageEvidence.SystemPrompt;
        StringAssert.Contains(prompt, "Never prefer CURRENT_PICK");
        StringAssert.Contains(prompt, "If CURRENT_PICK is tied or best, answer keep");
    }

    [TestMethod]
    public void TheGuidancePromptSaysACaseOrderOrPunctuationChangeIsNotNewAndOffersGiveUp()
    {
        var prompt = SearchGuidanceEvidence.SystemPrompt;
        StringAssert.Contains(prompt, "A different case, order or punctuation of a requested or ALREADY_TRIED name is not new");
        StringAssert.Contains(prompt, "if no genuinely new name exists, set give_up true");
    }

    [TestMethod]
    public void ACapturedInconsistentVerdictThatCitesNoNegativeEvidenceIsStillRejected()
    {
        // Captured from the lab (mimo-v2.6:9b): five of six ambiguity rejections were exactly this reply.
        const string reply = "{\"schema\":\"candidate-assessment/1\",\"verdict\":\"inconsistent\",\"claims\":[{\"kind\":\"contradicts_same_work\"," +
            "\"evidence_ids\":[\"ev-decision\"],\"note\":\"Evidence does not establish the release is the requested work.\"}]}";
        var parsed = SemanticAssessmentParser.Parse(reply, SemanticSchema.Candidate,
            [new EvidenceFinding("ev-decision", "decision.ambiguous", EvidencePolarity.Unknown, "The deterministic decision was ambiguous.")]);
        Assert.IsFalse(parsed.IsValid);
    }
}
