using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Evidence;
using static FamilyLibrarian.Domain.Evidence.EvidenceReasonCodes;

namespace FamilyLibrarian.Application.Security;

/// <summary>Deterministic eligibility for the narrowly scoped model release permission.</summary>
public static class ModelReleasePolicy
{
    public static bool IsEligible(MediaAsset asset, IReadOnlyList<IdentityFact> facts)
    {
        if (asset.StorageState != MediaAssetStorageState.Unmatched ||
            asset.Origin is not (AssetOrigin.ExternalProvider or AssetOrigin.BuiltInProvider) ||
            asset.Format.TrimStart('.').ToUpperInvariant() is not ("EPUB" or "KEPUB" or "MP3" or "M4B" or "OGG") ||
            asset.IdentityHoldCode == IdentityHold.ModelConcern) return false;
        if (!ArtifactHoldPolicy.Apply(asset, AssetIdentityVerificationResult.Match("model-release", facts)).IsMatch) return false;

        // The recorder's summary repeats the detailed identity observations, rather than adding another observation.
        // Expand only this known generated summary; every other Negative, including unknown codes, blocks release.
        var negatives = facts.Where(f => f.Polarity == EvidencePolarity.Negative &&
            !(f.FindingId == "ev-corroboration" && f.Dimension == EvidenceDimension.IdentityCorroboration &&
              f.Code == ArtifactIdentityCorroboration.Contradicted && facts.Any(d => d.FindingId != f.FindingId &&
                  d.Dimension == EvidenceDimension.IdentityCorroboration && d.Polarity == EvidencePolarity.Negative))).ToArray();
        return asset.IdentityHoldCode switch
        {
            IdentityHold.AttestedOnlyExternal or IdentityHold.ContentInconclusive => negatives.Length == 0,
            IdentityHold.ContentContradicted => negatives.Length == 1 &&
                negatives[0].Code == ArtifactIdentityCorroboration.AuthorDiffers &&
                negatives[0].Dimension == EvidenceDimension.IdentityCorroboration && HasAuthorAgreements(facts),
            IdentityHold.CollectionMissingRequestedWork =>
                facts.Any(f => f.Code == ArtifactIdentityAttestation.CollectionConsentRecorded && f.Dimension == EvidenceDimension.IdentityAttestation && f.Polarity == EvidencePolarity.Condition) &&
                negatives.Length == 1 && negatives[0].Code == ArtifactIdentityCorroboration.TitleDiffers &&
                negatives[0].Dimension == EvidenceDimension.IdentityCorroboration && facts.Any(IsCollectionContentsFact),
            _ => false
        };
    }

    public static bool HasAuthorAgreements(IReadOnlyList<IdentityFact> facts) =>
        facts.Any(IsTitleAgreement) && facts.Any(IsLanguageAgreement) && facts.Any(IsIndependentAgreement);

    public static bool IsTitleAgreement(IdentityFact fact) => fact.Dimension == EvidenceDimension.IdentityCorroboration &&
        fact.Code == ArtifactIdentityCorroboration.TitleAgrees && fact.Polarity == EvidencePolarity.Positive;
    public static bool IsLanguageAgreement(IdentityFact fact) => fact.Dimension == EvidenceDimension.Language &&
        fact.Code is ArtifactLanguage.DeclaredMatchesRequest or ArtifactLanguage.CueMatchesRequest && fact.Polarity == EvidencePolarity.Positive;
    public static bool IsIndependentAgreement(IdentityFact fact) => fact.Dimension == EvidenceDimension.Profile &&
        fact.Code is ProfileComparison.SeriesPositionAgrees or ProfileComparison.IdentifierAgrees && fact.Polarity == EvidencePolarity.Positive;
    public static bool IsPositiveIdentityFact(IdentityFact fact) => IsTitleAgreement(fact) || IsIndependentAgreement(fact) ||
        (fact.Dimension == EvidenceDimension.IdentityCorroboration && fact.Polarity == EvidencePolarity.Positive &&
         fact.Code is ArtifactIdentityCorroboration.AuthorAgrees or ArtifactIdentityCorroboration.ContainsRequestedWork);
    public static bool IsCollectionContentsFact(IdentityFact fact) => fact.Dimension == EvidenceDimension.Profile &&
        fact.Code is ArtifactProfile.TocEntry or ArtifactProfile.Heading &&
        fact.Polarity is EvidencePolarity.Positive or EvidencePolarity.Weak or EvidencePolarity.Unknown && !string.IsNullOrWhiteSpace(fact.Value);
}
