using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Evidence;

namespace FamilyLibrarian.Application.Upgrades;

/// <summary>One recorded finding about a delivered file. Codes and polarity only: never the file's text.</summary>
public sealed record DeliveredEvidenceFact(EvidenceDimension Dimension, string Code, EvidencePolarity Polarity);

/// <param name="HasIdentityOverride">A person released this copy after the checks held it. Their decision is never undone automatically.</param>
public sealed record DeliveredCopyFacts(AssetOrigin Origin, bool HasIdentityOverride, IReadOnlyList<DeliveredEvidenceFact> Evidence);

/// <param name="Deficient">The copy shows a recorded sign that it may be partial or incomplete, so a better one is worth looking for.</param>
/// <param name="Reasons">Stable evidence codes that justified the flag.</param>
/// <param name="ProtectedFromReplacement">
/// Never proposed for replacement, deficient or not: a person chose it (manual upload, or an identity override). Replacing it would
/// undo a human decision.
/// </param>
public sealed record DeficiencyAssessment(bool Deficient, IReadOnlyList<string> Reasons, bool ProtectedFromReplacement)
{
    /// <summary>True only when a better copy may be searched for and, with the right credentials, swapped in.</summary>
    public bool EligibleForUpgrade => Deficient && !ProtectedFromReplacement;
}

/// <summary>
/// Decides, from evidence already recorded when a file was checked, whether a delivered copy is worth upgrading (A6-UPGRADE-1 U-2).
/// Deliberately narrow and deterministic: it never reads the file, never asks a model, and treats "no cue found" as no reason at all.
/// A copy is only ever flagged for something the checks measured.
/// </summary>
public static class DeficientCopyDetector
{
    /// <summary>The evidence codes that, recorded as a condition or a negative, make a copy worth upgrading.</summary>
    public static IReadOnlyList<string> CodesThatMakeACopyDeficient { get; } =
    [
        EvidenceReasonCodes.ArtifactCompleteness.SampleMarkerInMetadata,
        EvidenceReasonCodes.ArtifactCompleteness.TocTargetsMissing,
        EvidenceReasonCodes.ArtifactCompleteness.SpineContentMissing,
        EvidenceReasonCodes.ArtifactCompleteness.DuplicateReadingOrderItems,
        EvidenceReasonCodes.ArtifactAudio.TrackSetGap,
        EvidenceReasonCodes.ArtifactAudio.TrackSetDuplicate
    ];

    private static readonly HashSet<string> DeficientCodes = new(CodesThatMakeACopyDeficient, StringComparer.Ordinal);

    public static DeficiencyAssessment Assess(DeliveredCopyFacts copy)
    {
        ArgumentNullException.ThrowIfNull(copy);
        var reasons = copy.Evidence
            .Where(fact => DeficientCodes.Contains(fact.Code) && fact.Polarity is EvidencePolarity.Condition or EvidencePolarity.Negative)
            .Select(fact => fact.Code)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToArray();
        var protectedCopy = copy.HasIdentityOverride || copy.Origin == AssetOrigin.ManualUpload;
        return new DeficiencyAssessment(reasons.Length > 0, reasons, protectedCopy);
    }
}
