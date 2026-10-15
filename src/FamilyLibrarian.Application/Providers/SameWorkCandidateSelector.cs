using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Domain.Providers;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Application.Providers;

/// <summary>A pool-level close call; the winner's original identity evidence stays unchanged.</summary>
public sealed record SameWorkCandidateSelection(FulfillmentOption Winner)
{
    public CandidateConfidenceTier Tier { get; } = CandidateConfidenceTier.CloseCall;
}

public static class SameWorkCandidateSelector
{
    public static SameWorkCandidateSelection? TrySelect(IReadOnlyList<FulfillmentOption> options,
        BookIdentity requested, RequestMediaType mediaType, ProviderConfidenceLevel level)
    {
        var reviewable = options.Where(option => option.AcquisitionAssessment?.Suitability != AcquisitionSuitability.Blocked &&
            option.IdentityAssessment?.Decision != WorkIdentityDecision.Mismatch &&
            LanguageAcceptance.IsAcceptedOrUnspecified(option.Language, requested.Language)).ToArray();
        if (reviewable.Length < 2 ||
            reviewable.Any(option => CandidateConfidence.Allows(level, CandidateConfidence.Classify(option)))) return null;

        var core = ReleaseTitleMatcher.Tokens(WorkTitleCore.Reduce(requested.Title, requested.Author));
        if (core.Length == 0 || !reviewable.All(option =>
                option.MediaType == mediaType && !string.IsNullOrWhiteSpace(option.Title) &&
                option.IdentityAssessment is { Conditions.Count: 0, Contradictions.Count: 0 } assessment &&
                assessment.TitleEvidence.State != IdentityEvidenceState.Fuzzy &&
                !option.RequiresLanguageConfirmation &&
                (!option.RequiresReleaseConfirmation || option.ReleaseConcern == ExternalReleasePolicy.UnknownDrmConfirmationReason) &&
                option.AuthorAffinity?.Kind != AuthorAffinityKind.Conflict &&
                assessment.AuthorEvidence.Kind != AuthorAffinityKind.Conflict &&
                core.SequenceEqual(ReleaseTitleMatcher.Tokens(WorkTitleCore.Reduce(option.Title, requested.Author))))) return null;
        if (!reviewable.Any(option => option.AuthorAffinity is { HasStrongSupport: true } or { Kind: AuthorAffinityKind.Fuzzy })) return null;

        var winner = ExternalCandidateRanker.SelectBest(reviewable, mediaType);
        return winner is null ? null : new(winner);
    }
}
