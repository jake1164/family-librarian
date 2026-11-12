using FamilyLibrarian.Domain.Evidence;

namespace FamilyLibrarian.Application.Security;

/// <summary>
/// Gives the identity holds that already exist a stable reason code (alpha.6 M1-5a). Pure labeling of an outcome
/// that has already been decided: it is called only for a result that is already unmatched, adds no hold and
/// changes no verdict. New holds arrive with the M1-5b policy.
/// </summary>
internal static class ExistingHoldCodes
{
    public static string Classify(AssetIdentityVerificationResult unmatched)
    {
        if (unmatched.VerifierId == ArtifactEvidenceRecorder.NoVerifierId)
        {
            // No content verifier exists and the source never vouched for the identity (the existing hold).
            return EvidenceReasonCodes.IdentityHold.NotAttested;
        }

        if (unmatched.Facts.Any(fact =>
                fact.Dimension == EvidenceDimension.IdentityCorroboration && fact.Polarity == EvidencePolarity.Negative))
        {
            return EvidenceReasonCodes.IdentityHold.ContentContradicted;
        }

        if (unmatched.Facts.Any(fact =>
                fact.Dimension == EvidenceDimension.Language && fact.Polarity == EvidencePolarity.Negative))
        {
            return EvidenceReasonCodes.IdentityHold.LanguageConflict;
        }

        // The verifier ran but could not read enough to compare (or the catalog entry has nothing to compare against).
        return EvidenceReasonCodes.IdentityHold.ContentInconclusive;
    }
}
