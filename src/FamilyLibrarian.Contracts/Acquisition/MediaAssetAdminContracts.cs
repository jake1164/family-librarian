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
    string? Origin = null);

/// <summary>One persisted artifact-evidence finding. <c>(RecordId, FindingId)</c> cites it.</summary>
public sealed record ArtifactEvidenceItemResponse(
    string RecordKind, Guid RecordId, string FindingId, string Dimension, string Code, string Polarity,
    string Source, string? Value, string? ToolVersion, DateTimeOffset RecordedAtUtc);

/// <param name="Status"><c>Recorded</c>, or <c>NotRecorded</c> for an asset processed before evidence recording existed.</param>
public sealed record ArtifactEvidenceResponse(
    string Status, string? Sha256, int? SchemaVersion, IReadOnlyList<ArtifactEvidenceItemResponse> Items);

public sealed record MediaAssetAdminListResponse(IReadOnlyList<MediaAssetAdminResponse> Assets);
