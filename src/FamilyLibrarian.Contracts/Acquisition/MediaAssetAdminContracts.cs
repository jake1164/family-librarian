using FamilyLibrarian.Contracts.Security;

namespace FamilyLibrarian.Contracts.Acquisition;

/// <summary>One entry in the admin acquisition/security review queue.</summary>
public sealed record MediaAssetAdminResponse(
    Guid AssetId,
    Guid RequestId,
    Guid WorkId,
    string WorkTitle,
    string MediaType,
    string Format,
    string OriginalFilename,
    long SizeBytes,
    string StorageState,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    SecurityEvaluationDetailResponse? LatestEvaluation,
    string? IdentityMismatchReason = null,
    string? ScanFailureReason = null,
    ArtifactEvidenceResponse? Evidence = null,
    string? IdentityHoldCode = null,
    string? Origin = null,
    AdminArtifactSemanticFindingResponse? SemanticFinding = null);

public sealed record AdminArtifactSemanticClaimResponse(string Kind, string Stance, IReadOnlyList<string> EvidenceIds, string Note);

/// <summary>
/// A model's advisory note about a file's recorded evidence. It never holds, releases, approves or rejects anything.
/// <paramref name="Verdict"/> and the claims are present only when the model gave a valid answer.
/// </summary>
public sealed record AdminArtifactSemanticFindingResponse(
    string Outcome,
    string? Verdict,
    IReadOnlyList<AdminArtifactSemanticClaimResponse> Claims,
    string? FailureDetail,
    string Model,
    bool IsCloud,
    DateTimeOffset CreatedAtUtc);

/// <summary>One persisted artifact-evidence finding. <c>(RecordId, FindingId)</c> cites it.</summary>
public sealed record ArtifactEvidenceItemResponse(
    string RecordKind, Guid RecordId, string FindingId, string Dimension, string Code, string Polarity,
    string Source, string? Value, string? ToolVersion, DateTimeOffset RecordedAtUtc);

/// <param name="Status"><c>Recorded</c>, or <c>NotRecorded</c> for an asset processed before evidence recording existed.</param>
public sealed record ArtifactEvidenceResponse(
    string Status, string? Sha256, int? SchemaVersion, IReadOnlyList<ArtifactEvidenceItemResponse> Items);

public sealed record MediaAssetAdminListResponse(IReadOnlyList<MediaAssetAdminResponse> Assets);
