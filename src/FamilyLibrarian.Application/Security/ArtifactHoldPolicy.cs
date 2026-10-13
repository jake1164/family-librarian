using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Evidence;

namespace FamilyLibrarian.Application.Security;

/// <summary>
/// The one place that decides which recorded evidence holds an otherwise-matching asset before it can be trusted
/// (alpha.6 M1-5b). A hold is the existing <see cref="MediaAssetStorageState.Unmatched"/> state with a stable
/// <c>identity.hold.*</c> code, resolved by the existing retry and administrator override; this policy adds no
/// status, no pipeline stage and no new irreversibility.
/// </summary>
/// <remarks>
/// <para>Pure and deterministic: it reads the verifier result, its facts and the asset's persisted origin, and nothing
/// else. It only ever turns a <em>match</em> into a hold; a result that is already unmatched keeps its own cause.
/// Hard failures (threats, structural defects, the empty-spine rejection) happen earlier and are untouched.</para>
/// <list type="bullet">
/// <item><b>Attested-only formats.</b> When no content verifier exists, the file was never compared with anything.
/// Source attestation then suffices only from a first-party basis: an authenticated administrator's upload
/// (<see cref="AssetOrigin.ManualUpload"/>) or a built-in provider (<see cref="AssetOrigin.BuiltInProvider"/>). An external
/// provider (unvetted third-party code), or an asset with no concrete origin, is held. The provider's id or name never
/// enters into it.</item>
/// <item><b>Completeness cues.</b> An explicit sample/excerpt/preview/teaser marker, or reading-order items whose files
/// are missing, hold the asset for a librarian. Missing table-of-contents targets hold only at two or more missing entries
/// and at least 10% of entries (OQ-4). Smaller navigation defects and duplicate reading-order files remain evidence.
/// A file with no cue is never promoted to complete.</item>
/// <item><b>Expected profile.</b> A conflicting series position, explicit abridgement, or a clear catalog-length shortfall
/// holds. Moderate shortfalls and inferred heading language remain evidence; unknown measurements never hold.</item>
/// <item><b>Legacy rows</b> (created before origin existed) keep their prior behavior: no retroactive holds.</item>
/// </list>
/// </remarks>
public static class ArtifactHoldPolicy
{
    public static AssetIdentityVerificationResult Apply(MediaAsset asset, AssetIdentityVerificationResult result)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(result);

        if (!result.IsMatch || asset.Origin == AssetOrigin.Legacy)
        {
            return result;
        }

        var holds = new List<(string Code, string Reason)>();

        if (result.VerifierId == ArtifactEvidenceRecorder.NoVerifierId &&
            asset.Origin is not (AssetOrigin.ManualUpload or AssetOrigin.BuiltInProvider))
        {
            holds.Add(asset.Origin == AssetOrigin.ExternalProvider
                ? (EvidenceReasonCodes.IdentityHold.AttestedOnlyExternal,
                    "No content check exists for this file format, and the only evidence that it is the requested book is the " +
                    "external provider's own claim. A librarian must confirm it before it is trusted.")
                : (EvidenceReasonCodes.IdentityHold.AttestedOnlyUnknownOrigin,
                    "No content check exists for this file format, and its source could not be established. A librarian must " +
                    "confirm it before it is trusted."));
        }

        foreach (var fact in result.Facts.Where(fact => fact.Dimension == EvidenceDimension.Completeness))
        {
            if (fact.Code == EvidenceReasonCodes.ArtifactCompleteness.SampleMarkerInMetadata)
            {
                holds.Add((EvidenceReasonCodes.IdentityHold.CompletenessSampleMarker,
                    $"The book's own metadata labels it a sample, excerpt, preview or teaser ({fact.Value}). A librarian must confirm " +
                    "it is the complete book."));
            }
            else if (fact.Code == EvidenceReasonCodes.ArtifactCompleteness.SpineContentMissing)
            {
                holds.Add((EvidenceReasonCodes.IdentityHold.CompletenessSpineContentMissing,
                    $"Part of the book's reading order points at files that are not in the EPUB ({fact.Value}), so it may be " +
                    "truncated. A librarian must confirm it is complete."));
            }
        }

        foreach (var fact in result.Facts)
        {
            if (fact.Code == EvidenceReasonCodes.ArtifactCompleteness.TocTargetsMissing &&
                fact.Dimension == EvidenceDimension.Completeness && fact.Polarity == EvidencePolarity.Condition &&
                ArtifactProfileComparer.MissingTocMeetsHoldThreshold(fact.Value))
                holds.Add((EvidenceReasonCodes.IdentityHold.TocEntriesMissing,
                    $"The table of contents points at missing files ({fact.Value}); a librarian must confirm completeness."));
            if (fact.Dimension != EvidenceDimension.Profile) continue;
            var hold = fact.Code switch
            {
                EvidenceReasonCodes.ProfileComparison.SeriesPositionDiffers when fact.Polarity == EvidencePolarity.Negative =>
                    (EvidenceReasonCodes.IdentityHold.SeriesPositionDiffers, "The file declares a different position in the requested series."),
                EvidenceReasonCodes.ProfileComparison.LengthFarBelowExpected when fact.Polarity == EvidencePolarity.Negative =>
                    (EvidenceReasonCodes.IdentityHold.LengthFarBelowExpected, "The text is less than a quarter of the catalog length."),
                EvidenceReasonCodes.ProfileComparison.DurationFarBelowExpected when fact.Polarity == EvidencePolarity.Negative =>
                    (EvidenceReasonCodes.IdentityHold.DurationFarBelowExpected, "The listening time is less than a quarter of the catalog duration."),
                EvidenceReasonCodes.ProfileComparison.AbridgedMarker when fact.Polarity == EvidencePolarity.Condition =>
                    (EvidenceReasonCodes.IdentityHold.AbridgedMarker, "The file explicitly identifies an abridged edition."),
                EvidenceReasonCodes.ProfileComparison.TocEntriesMissing when fact.Polarity == EvidencePolarity.Condition &&
                    ArtifactProfileComparer.MissingTocMeetsHoldThreshold(fact.Value) =>
                    (EvidenceReasonCodes.IdentityHold.TocEntriesMissing, "The table of contents points at missing files above the completeness threshold."),
                _ => ((string?)null, (string?)null)
            };
            if (hold.Item1 is not null && !holds.Any(existing => existing.Code == hold.Item1))
                holds.Add((hold.Item1, hold.Item2!));
        }

        return holds.Count == 0
            ? result
            : AssetIdentityVerificationResult.Unmatched(result.VerifierId, string.Join(" ", holds.Select(hold => hold.Reason)), result.Facts)
                with { HoldCode = holds[0].Code };
    }
}
