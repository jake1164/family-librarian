using FamilyLibrarian.Domain.Security;
using FamilyLibrarian.Domain.Acquisition;

namespace FamilyLibrarian.Application.Security;

/// <summary>
/// Evaluates a staged asset and immediately records policy approval when every required check passes.
/// </summary>
/// <remarks>
/// A failed or inconclusive evaluation never reaches the approval service through this path. A file
/// whose identity cannot be verified is held unmatched rather than approved or published.
/// </remarks>
public sealed class AutomatedSecurityPipeline(
    ISecurityEvaluationRunner evaluations,
    IPolicyAssetApprovalService approvals,
    IAssetIdentityVerificationService identity,
    ISecurityEvaluationRepository? repository = null)
{
    private const string CleanScanPolicyName = "clean-security-evaluation-v1";

    public async Task<SecurityEvaluationResult> EvaluateAsync(
        Guid assetId,
        CancellationToken cancellationToken)
    {
        var result = await evaluations.EvaluateAsync(assetId, cancellationToken);
        if (result is { Outcome: SecurityEvaluationOutcome.Success, Status: SecurityEvaluationStatus.Passed })
        {
            var approval = await approvals.ApproveByPolicyAsync(assetId, CleanScanPolicyName, cancellationToken);
            if (approval.Outcome != ApprovalOutcome.Success && approval.Outcome != ApprovalOutcome.IdentityUnmatched)
                throw new InvalidOperationException(approval.Error ?? "The clean security result could not be approved.");
        }
        return result;
    }

    /// <summary>
    /// Evaluates every sibling before policy approval begins. This prevents a
    /// clean early track from becoming Trusted before a later track fails or
    /// cannot be scanned/identity-checked.
    /// </summary>
    public async Task<IReadOnlyList<SecurityEvaluationResult>> EvaluateBundleAsync(
        IReadOnlyList<Guid> assetIds,
        CancellationToken cancellationToken)
    {
        var securityRepository = repository ?? throw new InvalidOperationException(
            "Bundle security evaluation requires an asset and evaluation repository.");
        if (assetIds.Count == 0 || assetIds.Distinct().Count() != assetIds.Count)
            throw new ArgumentException("A non-empty set of distinct asset IDs is required.", nameof(assetIds));

        var results = new Dictionary<Guid, SecurityEvaluationResult>();
        foreach (var assetId in assetIds)
        {
            var asset = await securityRepository.FindAssetAsync(assetId, cancellationToken);
            if (asset is null)
                return assetIds.Select(_ => SecurityEvaluationResult.NotFound()).ToArray();

            var previous = await securityRepository.FindLatestEvaluationAsync(assetId, cancellationToken);
            if (asset.StorageState == MediaAssetStorageState.Quarantine ||
                (asset.StorageState == MediaAssetStorageState.Processing &&
                 previous?.Status == SecurityEvaluationStatus.Pending))
                results[assetId] = await evaluations.EvaluateAsync(assetId, cancellationToken);
            else if (previous is not null)
                results[assetId] = SecurityEvaluationResult.Success(
                    previous.Id, previous.Status, previous.CreatedAtUtc, previous.CompletedAtUtc);
            else
                results[assetId] = SecurityEvaluationResult.Invalid("The staged asset has no security evaluation.");
        }

        var assets = new List<MediaAsset>(assetIds.Count);
        foreach (var assetId in assetIds)
        {
            var asset = await securityRepository.FindAssetAsync(assetId, cancellationToken);
            if (asset is null)
                return assetIds.Select(_ => SecurityEvaluationResult.NotFound()).ToArray();
            assets.Add(asset);
        }

        foreach (var asset in assets)
        {
            var evaluation = await securityRepository.FindLatestEvaluationAsync(asset.Id, cancellationToken);
            if (evaluation?.Status != SecurityEvaluationStatus.Passed ||
                asset.StorageState is not (MediaAssetStorageState.Processing or MediaAssetStorageState.Trusted))
                return assetIds.Select(id => results.GetValueOrDefault(id) ?? SecurityEvaluationResult.Invalid(
                    "A sibling track did not pass every security check.")).ToArray();
        }

        foreach (var asset in assets.Where(asset =>
                     asset.StorageState == MediaAssetStorageState.Processing))
        {
            var identityResult = await identity.VerifyAsync(asset.Id, cancellationToken);
            if (!identityResult.IsMatch)
                return assetIds.Select(id => results[id]).ToArray();
        }

        // Verify identity for the whole set before approving any sibling.
        // ApprovalService repeats its own identity check as a defense-in-depth
        // guard at the trust boundary.
        foreach (var assetId in assetIds)
        {
            var asset = await securityRepository.FindAssetAsync(assetId, cancellationToken);
            if (asset?.StorageState == MediaAssetStorageState.Processing)
            {
                var approval = await approvals.ApproveByPolicyAsync(assetId, CleanScanPolicyName, cancellationToken);
                if (approval.Outcome == ApprovalOutcome.IdentityUnmatched)
                    break;
                if (approval.Outcome != ApprovalOutcome.Success)
                    throw new InvalidOperationException(approval.Error ?? "The clean security result could not be approved.");
            }
        }

        return assetIds.Select(id => results[id]).ToArray();
    }

    /// <summary>
    /// Retries an unmatched asset's deterministic identity check, then sends a
    /// match through the same clean-scan approval policy as a new upload.
    /// </summary>
    public async Task<ApprovalResult> RetryIdentityAsync(
        Guid assetId,
        CancellationToken cancellationToken)
    {
        var result = await identity.RetryUnmatchedAsync(assetId, cancellationToken);
        return result.IsMatch
            ? await approvals.ApproveByPolicyAsync(assetId, CleanScanPolicyName, cancellationToken)
            : ApprovalResult.IdentityUnmatched();
    }
}
