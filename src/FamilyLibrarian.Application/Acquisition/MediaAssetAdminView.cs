using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Application.Acquisition;

/// <summary>One <see cref="MediaAsset"/> as shown to an administrator, with its owning Work/request context resolved.</summary>
public sealed record MediaAssetAdminView(
    Guid AssetId,
    Guid RequestId,
    Guid WorkId,
    string WorkTitle,
    RequestMediaType MediaType,
    string Format,
    string OriginalFilename,
    long SizeBytes,
    MediaAssetStorageState StorageState,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
