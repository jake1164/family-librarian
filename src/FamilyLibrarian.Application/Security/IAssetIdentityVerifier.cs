using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Evidence;

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

/// <summary>
/// One observation a verifier already made while comparing a file with its Work, reported so it
/// can be recorded as evidence. Facts never influence <see cref="AssetIdentityVerificationResult.IsMatch"/>.
/// </summary>
/// <param name="FindingId">Unique within one verification (for example <c>ev-title</c>).</param>
/// <param name="Value">A bounded, non-content detail such as a language tag. Never book text.</param>
public sealed record IdentityFact(
    string FindingId, EvidenceDimension Dimension, string Code, EvidencePolarity Polarity, string? Value = null);

public sealed record AssetIdentityVerificationResult(
    bool IsMatch,
    string VerifierId,
    string? Reason = null,
    IReadOnlyList<IdentityFact>? Facts = null)
{
    /// <summary>The stable <c>identity.hold.*</c> code when <see cref="ArtifactHoldPolicy"/> turned a match into a hold; otherwise null.</summary>
    public string? HoldCode { get; init; }

    /// <summary>What the verifier observed while deciding. Empty when it reported none.</summary>
    public IReadOnlyList<IdentityFact> Facts { get; init; } = Facts ?? [];

    public static AssetIdentityVerificationResult Match(string verifierId, IReadOnlyList<IdentityFact>? facts = null) =>
        new(true, verifierId, null, facts);

    /// <param name="reason">
    /// What was compared and why it didn't match, in terms a librarian can
    /// act on without reading source code (e.g. the catalog's expected title
    /// next to what the file actually has embedded). Left <c>null</c> only
    /// when the verifier couldn't safely read enough of the file to compare
    /// anything at all.
    /// </param>
    public static AssetIdentityVerificationResult Unmatched(
        string verifierId, string? reason = null, IReadOnlyList<IdentityFact>? facts = null) =>
        new(false, verifierId, reason, facts);
}
