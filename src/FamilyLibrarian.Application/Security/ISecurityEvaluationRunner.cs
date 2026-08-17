namespace FamilyLibrarian.Application.Security;

/// <summary>Runs a security evaluation for one quarantined media asset.</summary>
public interface ISecurityEvaluationRunner
{
    Task<SecurityEvaluationResult> EvaluateAsync(Guid assetId, CancellationToken cancellationToken);
}
