using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Security;

namespace FamilyLibrarian.Application.Security;

public interface ISecurityEvaluationRepository
{
    Task<MediaAsset?> FindAssetAsync(Guid assetId, CancellationToken cancellationToken);

    Task<IReadOnlyList<MediaAsset>> ListReservedDramatizationsAsync(Guid formatId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MediaAsset>>([]);

    /// <summary>Every sibling track sharing one multi-file acquisition's <see cref="MediaAsset.BundleId"/>.</summary>
    Task<IReadOnlyList<MediaAsset>> FindAssetsByBundleIdAsync(Guid bundleId, CancellationToken cancellationToken);

    /// <summary>
    /// Held (unmatched) or rejected copies whose request format no longer needs one: the format is in the library or
    /// cancelled, or the request is closed as available or cancelled. A "not available" request is not settled; a
    /// librarian may still release one of its held copies.
    /// </summary>
    Task<IReadOnlyList<MediaAsset>> ListHeldAssetsOfSettledFormatsAsync(int maximumCount, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MediaAsset>>([]);

    Task<SecurityEvaluation?> FindLatestEvaluationAsync(Guid assetId, CancellationToken cancellationToken);

    void AddEvaluation(SecurityEvaluation evaluation);

    Task SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>Serializes a model release with asset changes, reloads the target, and commits its audit/evidence atomically.</summary>
    Task<ApprovalResult> ExecuteModelReleaseAsync(Guid assetId, Func<Task<ApprovalResult>> action, CancellationToken cancellationToken)
        => Task.FromResult(ApprovalResult.Invalid("Model release requires transactional persistence."));
}
