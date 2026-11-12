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
    /// librarian may still release one of its held copies. Copies of a format with an upgrade in progress are left out: one of
    /// them may be the better copy that upgrade is waiting for.
    /// </summary>
    Task<IReadOnlyList<MediaAsset>> ListHeldAssetsOfSettledFormatsAsync(int maximumCount, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MediaAsset>>([]);

    /// <summary>
    /// Copies waiting for a person that another copy of the same request format has since replaced: a copy of the same format has
    /// passed every check (trusted or already archived) outside the waiting copy's own multi-file set. Waiting means held
    /// (unmatched), or checked and waiting in processing for a librarian's decision since before
    /// <paramref name="awaitingDecisionBeforeUtc"/> (so a copy that is in the middle of being approved is never taken). The waiting
    /// copy is no longer the only copy, so keeping it only costs disk. Copies of a format with an upgrade in progress are left out:
    /// there the copy already in the library is the one being replaced, and the waiting copy may be its replacement.
    /// </summary>
    Task<IReadOnlyList<MediaAsset>> ListHeldAssetsSupersededByVerifiedCopyAsync(
        int maximumCount, DateTimeOffset awaitingDecisionBeforeUtc, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MediaAsset>>([]);

    Task<SecurityEvaluation?> FindLatestEvaluationAsync(Guid assetId, CancellationToken cancellationToken);

    void AddEvaluation(SecurityEvaluation evaluation);

    Task SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>Serializes a model release with asset changes, reloads the target, and commits its audit/evidence atomically.</summary>
    Task<ApprovalResult> ExecuteModelReleaseAsync(Guid assetId, Func<Task<ApprovalResult>> action, CancellationToken cancellationToken)
        => Task.FromResult(ApprovalResult.Invalid("Model release requires transactional persistence."));
}
