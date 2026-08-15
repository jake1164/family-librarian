using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Security;

namespace FamilyLibrarian.Application.Security;

public interface ISecurityEvaluationRepository
{
    Task<MediaAsset?> FindAssetAsync(Guid assetId, CancellationToken cancellationToken);

    Task<SecurityEvaluation?> FindLatestEvaluationAsync(Guid assetId, CancellationToken cancellationToken);

    void AddEvaluation(SecurityEvaluation evaluation);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
