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
    string VerifierId,
    string? Reason = null)
{
    public static AssetIdentityVerificationResult Match(string verifierId) => new(true, verifierId);

    /// <param name="reason">
    /// What was compared and why it didn't match, in terms a librarian can
    /// act on without reading source code (e.g. the catalog's expected title
    /// next to what the file actually has embedded). Left <c>null</c> only
    /// when the verifier couldn't safely read enough of the file to compare
    /// anything at all.
    /// </param>
    public static AssetIdentityVerificationResult Unmatched(string verifierId, string? reason = null) =>
        new(false, verifierId, reason);
}
