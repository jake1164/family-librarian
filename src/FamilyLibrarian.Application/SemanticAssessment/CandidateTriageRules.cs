using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Domain.Providers;

namespace FamilyLibrarian.Application.SemanticAssessment;

/// <summary>Independent allow-list; a parsed model reply is never permission by itself.</summary>
public static class CandidateTriageRules
{
    public static bool Allows(CandidateTriage move, CandidateTriageContext context, CandidateTriageInput input)
    {
        if (move.Move is "keep" or "uncertain") return move.Candidate is null;
        if (move.Candidate is null || !input.Candidates.TryGetValue(move.Candidate, out var candidate) ||
            !context.Candidates.Any(c => c.ProviderResultId == candidate.ProviderResultId && c.ProviderId == candidate.ProviderId) ||
            !input.Findings.TryGetValue(move.Candidate, out var facts)) return false;
        var cited = facts.Where(f => move.EvidenceIds.Contains(f.Id, StringComparer.Ordinal)).ToArray();
        if (cited.Length != move.EvidenceIds.Count || !SemanticAssessmentParser.IsGroundedTriage(move, cited)) return false;
        return move.Move switch
        {
            "veto" => candidate.ProviderResultId == context.Pick?.ProviderResultId,
            "prefer" => context.TiedResultIds.Count >= 2 && context.TiedResultIds.Contains(candidate.ProviderResultId) &&
                context.Pick is { } pick && ExternalCandidateRanker.AreTied(pick, candidate, candidate.MediaType) &&
                CandidateConfidence.Allows(context.Level, CandidateConfidence.Classify(candidate)) &&
                CandidateConfidence.Allows(context.Level, CandidateConfidenceTier.CloseCall),
            "author_variant" => CandidateConfidence.Allows(context.Level, CandidateConfidenceTier.CloseCall) && IsAuthorOnlyConflict(candidate) &&
                CanAuthorVariantLead(candidate, context.Candidates, context.Level),
            _ => false
        };
    }

    /// <summary>An AI author variant is a close call; deterministic tier/rank ordering still chooses the best copy left.</summary>
    public static bool CanAuthorVariantLead(FulfillmentOption candidate, IReadOnlyList<FulfillmentOption> alternatives,
        ProviderConfidenceLevel level) => !alternatives.Any(other => other.ProviderResultId != candidate.ProviderResultId &&
            CandidateConfidence.Allows(level, CandidateConfidence.Classify(other)) &&
            (CandidateConfidence.Classify(other) == CandidateConfidenceTier.Strong ||
             CandidateConfidence.Classify(other) == CandidateConfidenceTier.CloseCall &&
             ExternalCandidateRanker.SelectBest([candidate, other], candidate.MediaType)?.ProviderResultId == other.ProviderResultId));

    public static bool IsAuthorOnlyConflict(FulfillmentOption option) => option.IdentityAssessment is { } assessment &&
        assessment.AuthorEvidence.Kind == AuthorAffinityKind.Conflict && assessment.Contradictions.Count == 1 &&
        assessment.TitleEvidence.State is IdentityEvidenceState.Exact or IdentityEvidenceState.Compatible && assessment.HasStrongTitle &&
        assessment.LanguageEvidence != IdentityEvidenceState.Conflicting &&
        assessment.SeriesEvidence.All(s => s.State != IdentityEvidenceState.Conflicting) && assessment.Conditions.Count == 0 &&
        !option.RequiresLanguageConfirmation && !option.RequiresNarrationConfirmation &&
        (!option.RequiresReleaseConfirmation || option.ReleaseConcern == ExternalReleasePolicy.UnknownDrmConfirmationReason) &&
        option.AudiobookPart is null && CandidateConfidence.HasIndependentAgreement(assessment);
}

/// <summary>Server-created proof for rechecking one author variant against fresh source facts. Never an API input.</summary>
public sealed class CandidateTriageAuthorization
{
    private readonly Guid _requestId;
    private readonly Guid _formatId;
    private readonly string _fingerprint;
    private readonly string _providerId;
    private readonly string _resultId;
    private readonly string _move;
    private readonly IReadOnlyCollection<string> _excluded;

    internal CandidateTriageAuthorization(Guid requestId, Guid formatId, FulfillmentOption option,
        string move, IReadOnlyCollection<string>? excluded = null)
    {
        _requestId = requestId;
        _formatId = formatId;
        _fingerprint = CandidateTriageEvidence.Fingerprint(option);
        _providerId = option.ProviderId;
        _resultId = option.ProviderResultId;
        _move = move;
        _excluded = excluded?.ToArray() ?? [];
    }

    internal ExternalCandidateExclusions ApplyExclusions(Guid requestId, Guid formatId, string providerId, string resultId,
        ExternalCandidateExclusions existing) => ScopeMatches(requestId, formatId, providerId, resultId)
        ? existing with { ResultIds = existing.ResultIds.Concat(_excluded).ToHashSet(StringComparer.Ordinal) } : existing;

    public bool IsCurrentFor(Guid requestId, Guid formatId, FulfillmentOption fresh, ProviderConfidenceLevel level) =>
        ScopeMatches(requestId, formatId, fresh.ProviderId, fresh.ProviderResultId) &&
        (_move == "veto" || CandidateConfidence.Allows(level, CandidateConfidenceTier.CloseCall)) &&
        _fingerprint == CandidateTriageEvidence.Fingerprint(fresh);

    public bool AllowsAuthorVariant(Guid requestId, Guid formatId, FulfillmentOption fresh, ProviderConfidenceLevel level) =>
        _move == "author_variant" && IsCurrentFor(requestId, formatId, fresh, level) && CandidateTriageRules.IsAuthorOnlyConflict(fresh);

    private bool ScopeMatches(Guid requestId, Guid formatId, string providerId, string resultId) =>
        _requestId == requestId && _formatId == formatId && _providerId == providerId && _resultId == resultId;
}
