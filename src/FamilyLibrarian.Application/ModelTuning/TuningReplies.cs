using System.Text.Json;
using FamilyLibrarian.Application.Conversation;
using FamilyLibrarian.Application.SemanticAssessment;
using FamilyLibrarian.Domain.Evidence;
using FamilyLibrarian.Domain.ModelGateway;

namespace FamilyLibrarian.Application.ModelTuning;

/// <summary>Scripted responses for baselines and corpus contract tests; never duplicates a prompt.</summary>
public static class TuningReplies
{
    private static string[] SearchEvidence(TuningSearchName name, RenderedTuningCase rendered) =>
        [rendered.Findings.FirstOrDefault(f => f.Text.Contains(name.Value, StringComparison.OrdinalIgnoreCase))?.Id ?? "req-title"];

    public static string Render(ModelRole role, TuningAnswer answer, RenderedTuningCase rendered)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(rendered);
        if (role == ModelRole.IntentInterpretation)
            return JsonSerializer.Serialize(new { schema = BookIntentParser.SchemaId, intent = answer.Intent, title = answer.Title, author = answer.Author });
        if (role == ModelRole.SearchGuidance)
            return JsonSerializer.Serialize(new { schema = SemanticAssessmentParser.SearchGuidanceSchema, give_up = answer.GiveUp,
                suggestions = (answer.RequiredSuggestions ?? answer.AllowedSuggestions ?? []).Select(s => new
                { reason = s.Reason, value = s.Value, evidence_ids = SearchEvidence(s, rendered), note = "The supplied result provides this name." }) });
        if (role == ModelRole.CandidateTriage)
        {
            var candidate = answer.TargetKey is null ? null : rendered.CandidateIds[answer.TargetKey];
            var facts = candidate is null ? [] : rendered.Findings.Where(f => f.Id.StartsWith(candidate + ".", StringComparison.Ordinal));
            var ids = answer.Move switch
            {
                "author_variant" => facts.Where(f => f.Polarity == EvidencePolarity.Positive &&
                    (f.Code is "candidate.title.exact" or "candidate.title.compatible" or "candidate.series.exact" ||
                     f.Code is EvidenceReasonCodes.CandidateCorroboration.Identifier or EvidenceReasonCodes.CandidateCorroboration.Year or EvidenceReasonCodes.CandidateCorroboration.Publisher)).Select(f => f.Id).Take(6).ToArray(),
                "veto" => facts.Where(f => f.Polarity is EvidencePolarity.Negative or EvidencePolarity.Condition).Select(f => f.Id).Take(6).ToArray(),
                "prefer" => facts.Where(f => f.Polarity == EvidencePolarity.Positive).Select(f => f.Id).Take(6).ToArray(),
                _ => []
            };
            return JsonSerializer.Serialize(new { schema = SemanticAssessmentParser.CandidateTriageSchema, move = answer.Move,
                candidate, evidence_ids = ids, note = "Supplied observations support this comparison." });
        }
        var candidateAssessment = role == ModelRole.CandidateAmbiguityAssessment;
        var hold = role == ModelRole.ArtifactValidationDecision;
        var kind = "insufficient_information";
        string[] evidence = [];
        if (answer.Verdict is "consistent" or "inconsistent" or "hold")
        {
            var contradicts = answer.Verdict == "inconsistent" || answer.Verdict == "hold" && rendered.Findings.Any(f => f.Polarity == EvidencePolarity.Negative);
            var fact = rendered.Findings.First(f => answer.Verdict == "consistent"
                ? f.Polarity is EvidencePolarity.Positive or EvidencePolarity.Weak
                : contradicts ? f.Polarity == EvidencePolarity.Negative : f.Polarity is EvidencePolarity.Weak or EvidencePolarity.Condition);
            kind = contradicts ? candidateAssessment ? "contradicts_same_work" : "contradicts_declared_identity"
                : answer.Verdict == "hold" ? fact.Code.Contains("abridged", StringComparison.Ordinal) ? "suggests_wrong_edition" : "suggests_incomplete"
                : candidateAssessment ? "supports_same_work" : "supports_declared_identity";
            evidence = [fact.Id];
        }
        var schema = hold ? SemanticAssessmentParser.ArtifactDecisionSchema : candidateAssessment ? SemanticSchema.Candidate.Id : SemanticSchema.Artifact.Id;
        return JsonSerializer.Serialize(new { schema, verdict = answer.Verdict, claims = new[] { new { kind, evidence_ids = evidence, note = "Supplied observations support this assessment." } } });
    }
}
