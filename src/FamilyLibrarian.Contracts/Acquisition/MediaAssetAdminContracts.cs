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
    SecurityEvaluationDetailResponse? LatestEvaluation);

public sealed record MediaAssetAdminListResponse(IReadOnlyList<MediaAssetAdminResponse> Assets);
