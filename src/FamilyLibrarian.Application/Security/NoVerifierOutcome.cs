using FamilyLibrarian.Domain.Acquisition;

namespace FamilyLibrarian.Application.Security;

/// <summary>
/// What an asset's identity check returns when no content verifier could judge it. One definition, used by
/// <see cref="AssetIdentityVerificationService"/> for a format with no verifier and by a verifier that found nothing
/// usable to compare (for example an MP3 with no title or author tags), so both reach exactly the same outcome.
/// </summary>
public static class NoVerifierOutcome
{
    /// <summary>
    /// A pre-confirmed match (every built-in-provider acquisition, a manual upload, or an external-provider candidate
    /// strong enough to need no override) has nothing left to prove, so this still passes. An unconfirmed
    /// external-provider match has nothing confirming it at all and is held for a librarian.
    /// </summary>
    public static AssetIdentityVerificationResult For(MediaAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return asset.IdentityPreConfirmed
            ? AssetIdentityVerificationResult.Match(ArtifactEvidenceRecorder.NoVerifierId)
            : AssetIdentityVerificationResult.Unmatched(
                ArtifactEvidenceRecorder.NoVerifierId,
                "No identity verifier exists for this file format, and the match was not independently " +
                "confirmed before this file was fetched. A librarian must inspect it directly.");
    }
}
