using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Domain.Providers;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Application.Providers;

public enum CandidateConfidenceTier { Strong = 1, CloseCall = 2, LiberalOnly = 3, NotEligible = 4 }

/// <summary>Deterministic download confidence, subordinate to identity and release gates.</summary>
public static class CandidateConfidence
{
    public static CandidateConfidenceTier Classify(FulfillmentOption option)
    {
        ArgumentNullException.ThrowIfNull(option);
        if (option.RequiresLanguageConfirmation ||
            option.RequiresReleaseConfirmation && option.ReleaseConcern != ExternalReleasePolicy.UnknownDrmConfirmationReason ||
            option.AcquisitionAssessment?.Suitability == AcquisitionSuitability.Blocked ||
            option.IdentityAssessment?.Decision == WorkIdentityDecision.Mismatch ||
            option.IdentityAssessment?.TitleEvidence.State == IdentityEvidenceState.Fuzzy)
            return CandidateConfidenceTier.NotEligible;

        if (option.MatchBasis is BookMatchBasis.Identifier or BookMatchBasis.StrictTitleAuthor ||
            option.MatchBasis == BookMatchBasis.StrictTitle && option.AuthorAffinity?.Kind == AuthorAffinityKind.ContextCorroborated)
            return CandidateConfidenceTier.Strong;
        if (option.MatchBasis == BookMatchBasis.StrictTitle)
        {
            if (option.AuthorAffinity?.Kind == AuthorAffinityKind.Fuzzy) return CandidateConfidenceTier.CloseCall;
            if (option.AuthorAffinity is null || option.AuthorAffinity.Kind is AuthorAffinityKind.Unknown or AuthorAffinityKind.LastOnly or AuthorAffinityKind.FirstOnly)
                return option.IdentityAssessment is { } assessment && HasIndependentAgreement(assessment)
                    ? CandidateConfidenceTier.CloseCall : CandidateConfidenceTier.LiberalOnly;
        }
        return option.MatchBasis == BookMatchBasis.TitleAuthor && option.HasPlausibleTitle
            ? CandidateConfidenceTier.LiberalOnly : CandidateConfidenceTier.NotEligible;
    }

    public static bool Allows(ProviderConfidenceLevel level, CandidateConfidenceTier tier) => level switch
    {
        ProviderConfidenceLevel.Strict => tier == CandidateConfidenceTier.Strong,
        ProviderConfidenceLevel.Standard => tier is CandidateConfidenceTier.Strong or CandidateConfidenceTier.CloseCall,
        ProviderConfidenceLevel.Liberal => tier is CandidateConfidenceTier.Strong or CandidateConfidenceTier.CloseCall or CandidateConfidenceTier.LiberalOnly,
        _ => false
    };

    public static bool HasIndependentAgreement(CandidateIdentityAssessment assessment) =>
        HasIndependentAgreement(assessment.RequestedMetadata, assessment.SourceMetadata.Edition, assessment.SeriesEvidence);

    public static IReadOnlyList<FulfillmentOption> Rank(IEnumerable<FulfillmentOption> options, RequestMediaType mediaType) =>
        options.GroupBy(Classify).OrderBy(group => group.Key)
            .SelectMany(group => ExternalCandidateRanker.Rank(group, mediaType)).ToArray();

    public static bool HasIndependentAgreement(BookIdentity requested, ExternalProviderEditionEvidence? edition,
        IReadOnlyList<SeriesIdentityEvidence> seriesEvidence) =>
        seriesEvidence.Any(series => series.State == IdentityEvidenceState.Exact) ||
        edition?.Identifiers.Any(identifier => identifier.Scheme.Equals("isbn13", StringComparison.OrdinalIgnoreCase) &&
            requested.Isbn13Candidates.Any(isbn => NormalizeIdentifier(isbn) == NormalizeIdentifier(identifier.Value))) == true ||
        requested.PublicationYear is not null && requested.PublicationYear == edition?.PublicationYear &&
        !string.IsNullOrWhiteSpace(requested.Publisher) && !string.IsNullOrWhiteSpace(edition?.Publisher) &&
        string.Equals(DeterministicBookMatcher.NormalizeWords(requested.Publisher),
            DeterministicBookMatcher.NormalizeWords(edition.Publisher), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeIdentifier(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
