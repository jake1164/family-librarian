using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Audit;
using FamilyLibrarian.Domain.Security;

namespace FamilyLibrarian.Application.Security;

/// <summary>
/// Runs every registered scanner and validator against one quarantined asset,
/// records the results, and resolves the pass/fail decision.
/// </summary>
/// <remarks>
/// This does not itself decide whether the caller may run it now — that is
/// <see cref="IAcquisitionBoundaryGuard"/>'s job, checked before a file ever
/// reaches quarantine. Once an asset is quarantined, evaluating it is always
/// allowed; a scanner going unhealthy mid-evaluation simply produces an
/// <see cref="ScanResultStatus.Unavailable"/> result, which the fail-closed
/// policy in <see cref="SecurityEvaluation.Evaluate"/> already treats as "never
/// passes."
/// </remarks>
public sealed class SecurityEvaluationService(
    ISecurityEvaluationRepository repository,
    IAssetStagingStore stagingStore,
    IEnumerable<IMalwareScanner> scanners,
    IEnumerable<IAssetValidator> validators,
    IAuditWriter audit,
    IClock clock)
{
    private const string PolicyVersion = "v1";

    public async Task<SecurityEvaluationResult> EvaluateAsync(Guid assetId, CancellationToken cancellationToken)
    {
        var asset = await repository.FindAssetAsync(assetId, cancellationToken);
        if (asset is null)
        {
            return SecurityEvaluationResult.NotFound();
        }

        if (asset.StorageState != MediaAssetStorageState.Quarantine)
        {
            return SecurityEvaluationResult.Invalid("Only a quarantined asset can be evaluated.");
        }

        var now = clock.UtcNow;
        await stagingStore.MoveAsync(
            MediaAssetStorageState.Quarantine, MediaAssetStorageState.Processing, asset.StoredFilename, cancellationToken);
        asset.TransitionStorageState(MediaAssetStorageState.Processing, now);

        var evaluation = new SecurityEvaluation(assetId, PolicyVersion, now);

        foreach (var scanner in scanners)
        {
            var health = await scanner.CheckHealthAsync(cancellationToken);
            if (!health.IsHealthy)
            {
                evaluation.RecordScanResult(
                    scanner.Id, scanner.IsRequired, ScanResultStatus.Unavailable, null, health.Version, now);
                continue;
            }

            await using var content = await stagingStore.OpenAsync(
                MediaAssetStorageState.Processing, asset.StoredFilename, cancellationToken);
            var outcome = await scanner.ScanAsync(content, cancellationToken);
            evaluation.RecordScanResult(
                scanner.Id, scanner.IsRequired, outcome.Status, outcome.ThreatName, health.Version, now);
        }

        foreach (var validator in validators)
        {
            await using var content = await stagingStore.OpenAsync(
                MediaAssetStorageState.Processing, asset.StoredFilename, cancellationToken);
            var outcome = await validator.ValidateAsync(asset, content, cancellationToken);
            evaluation.RecordValidationResult(validator.Id, outcome.IsValid, outcome.Message, now);
        }

        evaluation.Evaluate(now);

        if (evaluation.Status == SecurityEvaluationStatus.Failed)
        {
            await stagingStore.MoveAsync(
                MediaAssetStorageState.Processing, MediaAssetStorageState.Rejected, asset.StoredFilename, cancellationToken);
            asset.TransitionStorageState(MediaAssetStorageState.Rejected, now);
        }

        // Passed or ReviewRequired stays in Processing until an explicit
        // approval — see ApprovalService.

        repository.AddEvaluation(evaluation);
        await repository.SaveChangesAsync(cancellationToken);

        await audit.WriteAsync(
            AuditActions.AssetEvaluated,
            AuditSubjectTypes.MediaAsset,
            assetId.ToString(),
            new { AssetId = assetId, evaluation.Status },
            cancellationToken);

        return SecurityEvaluationResult.Success(
            evaluation.Id, evaluation.Status, evaluation.CreatedAtUtc, evaluation.CompletedAtUtc);
    }
}

public sealed record SecurityEvaluationResult(
    SecurityEvaluationOutcome Outcome,
    Guid? EvaluationId,
    SecurityEvaluationStatus? Status,
    DateTimeOffset? CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? Error)
{
    public static SecurityEvaluationResult Success(
        Guid evaluationId,
        SecurityEvaluationStatus status,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? completedAtUtc) =>
        new(SecurityEvaluationOutcome.Success, evaluationId, status, createdAtUtc, completedAtUtc, null);

    public static SecurityEvaluationResult NotFound() =>
        new(SecurityEvaluationOutcome.NotFound, null, null, null, null, null);

    public static SecurityEvaluationResult Invalid(string error) =>
        new(SecurityEvaluationOutcome.Invalid, null, null, null, null, error);
}

public enum SecurityEvaluationOutcome
{
    Success,
    NotFound,
    Invalid
}
