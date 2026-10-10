using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Evidence;

namespace FamilyLibrarian.Infrastructure.Security;

/// <summary>
/// Builds the <see cref="IdentityFact"/> observations the existing identity verifiers already derive.
/// Facts are evidence for the record only; no verdict is computed from them.
/// </summary>
internal static class IdentityFacts
{
    public static IdentityFact Title(bool declared, bool agrees) =>
        !declared
            ? new("ev-title", EvidenceDimension.IdentityCorroboration, EvidenceReasonCodes.ArtifactIdentityCorroboration.TitleAbsent, EvidencePolarity.Unknown)
            : agrees
                ? new("ev-title", EvidenceDimension.IdentityCorroboration, EvidenceReasonCodes.ArtifactIdentityCorroboration.TitleAgrees, EvidencePolarity.Positive)
                : new("ev-title", EvidenceDimension.IdentityCorroboration, EvidenceReasonCodes.ArtifactIdentityCorroboration.TitleDiffers, EvidencePolarity.Negative);

    public static IdentityFact Author(bool declared, bool agrees) =>
        !declared
            ? new("ev-author", EvidenceDimension.IdentityCorroboration, EvidenceReasonCodes.ArtifactIdentityCorroboration.AuthorAbsent, EvidencePolarity.Unknown)
            : agrees
                ? new("ev-author", EvidenceDimension.IdentityCorroboration, EvidenceReasonCodes.ArtifactIdentityCorroboration.AuthorAgrees, EvidencePolarity.Positive)
                : new("ev-author", EvidenceDimension.IdentityCorroboration, EvidenceReasonCodes.ArtifactIdentityCorroboration.AuthorDiffers, EvidencePolarity.Negative);

    /// <param name="declared">The language tags the file declares (short codes only are stored).</param>
    /// <param name="accepted">Whether the existing acceptance rule allowed them.</param>
    public static IdentityFact Language(IReadOnlyCollection<string> declared, bool accepted) =>
        declared.Count == 0
            ? new("ev-language", EvidenceDimension.Language, EvidenceReasonCodes.ArtifactLanguage.NotDeclared, EvidencePolarity.Unknown)
            : accepted
                ? new("ev-language", EvidenceDimension.Language, EvidenceReasonCodes.ArtifactLanguage.DeclaredMatchesRequest, EvidencePolarity.Positive,
                    string.Join(",", declared))
                : new("ev-language", EvidenceDimension.Language, EvidenceReasonCodes.ArtifactLanguage.DeclaredConflicts, EvidencePolarity.Negative,
                    string.Join(",", declared));
}
