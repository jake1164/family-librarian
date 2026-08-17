namespace FamilyLibrarian.Application.Security;

/// <summary>Records an approval made by a named automated policy.</summary>
public interface IPolicyAssetApprovalService
{
    Task<ApprovalResult> ApproveByPolicyAsync(
        Guid assetId,
        string policyName,
        CancellationToken cancellationToken);
}
