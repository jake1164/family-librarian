namespace FamilyLibrarian.Application.Providers;

public enum AcquisitionSuitability { EligibleForChecks, IdentityReview, NeedsCompanionParts, EditionReview, Blocked }

/// <summary>Pre-acquisition policy only; EligibleForChecks never bypasses file validation.</summary>
public sealed record CandidateAcquisitionAssessment(AcquisitionSuitability Suitability, IReadOnlyList<string> Reasons);

public enum ReleaseConditionKind { CompanionParts, EditionReview, MalformedStructure }
/// <param name="Code">A specific <c>EvidenceReasonCodes.CandidateCondition</c> value; when null the generic code for <paramref name="Kind"/> is used.</param>
public sealed record ReleaseCondition(ReleaseConditionKind Kind, string Reason, string? Code = null);
