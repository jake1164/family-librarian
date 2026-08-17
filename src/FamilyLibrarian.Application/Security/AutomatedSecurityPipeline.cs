using FamilyLibrarian.Domain.Security;

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
    IPolicyAssetApprovalService approvals)
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
            if (approval.Outcome == ApprovalOutcome.IdentityUnmatched)
            {
                return result;
            }
            if (approval.Outcome != ApprovalOutcome.Success)
            {
                throw new InvalidOperationException(
                    approval.Error ?? "The clean security result could not be approved.");
            }
        }

        return result;
    }
}
