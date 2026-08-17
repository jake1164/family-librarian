using FamilyLibrarian.Domain.Acquisition;

namespace FamilyLibrarian.Application.Security;

/// <summary>
/// Confirms that an artifact's embedded identity belongs to the Work it was
/// staged to fulfill.
/// </summary>
/// <remarks>
/// This is intentionally separate from malware and file-format validation:
/// neither a clean scan nor a structurally valid EPUB proves it is the book a
/// requester asked for.
/// </remarks>
public interface IAssetIdentityVerifier
{
    string Id { get; }

    bool Supports(MediaAsset asset);

    Task<AssetIdentityVerificationResult> VerifyAsync(
        MediaAsset asset,
        Stream content,
        CancellationToken cancellationToken);
}

public sealed record AssetIdentityVerificationResult(
    bool IsMatch,
    string VerifierId)
{
    public static AssetIdentityVerificationResult Match(string verifierId) => new(true, verifierId);

    public static AssetIdentityVerificationResult Unmatched(string verifierId) => new(false, verifierId);
}
