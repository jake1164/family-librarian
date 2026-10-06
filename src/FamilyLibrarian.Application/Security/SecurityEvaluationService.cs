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
    IClock clock) : ISecurityEvaluationRunner
{
    private const string PolicyVersion = "v1";

    public async Task<SecurityEvaluationResult> EvaluateAsync(Guid assetId, CancellationToken cancellationToken)
    {
        var asset = await repository.FindAssetAsync(assetId, cancellationToken);
        if (asset is null)
        {
            return SecurityEvaluationResult.NotFound();
        }

        if (asset.StorageState == MediaAssetStorageState.Processing)
        {
            await RecoverAbandonedEvaluationAsync(asset, cancellationToken);
        }

        if (asset.StorageState != MediaAssetStorageState.Quarantine)
        {
            return SecurityEvaluationResult.Invalid("Only a quarantined asset can be evaluated.");
        }

        var now = clock.UtcNow;
        await stagingStore.MoveAsync(
            MediaAssetStorageState.Quarantine, MediaAssetStorageState.Processing, asset.StoredFilename, cancellationToken);
        asset.TransitionStorageState(MediaAssetStorageState.Processing, now);
        asset.SetScanFailureReason(null);

        // Persisted immediately, not batched with everything below: from here
        // on, a scanner or validator can throw (see ClamAvMalwareScannerTests
        // for a documented case — a dropped connection propagates raw rather
        // than being swallowed). Before this fix, that left the database still
        // saying Quarantine while the file already sat in processing/, and
        // every retry's MoveAsync then threw FileNotFoundException forever —
        // the file had nowhere to move from. Saving here first means a later
        // failure has a database state to recover *from* instead of a stale
        // one to collide with.
        var evaluation = new SecurityEvaluation(assetId, PolicyVersion, now);
        repository.AddEvaluation(evaluation);
        await repository.SaveChangesAsync(cancellationToken);

        try
        {
            foreach (var scanner in scanners)
            {
                var health = await scanner.CheckHealthAsync(cancellationToken);
                if (!health.IsHealthy)
                {
                    evaluation.RecordScanResult(
                        scanner.Id, scanner.IsRequired, ScanResultStatus.Unavailable, null, health.Version, clock.UtcNow);
                    await repository.SaveChangesAsync(cancellationToken);
                    continue;
                }

                await using var content = await stagingStore.OpenAsync(
                    MediaAssetStorageState.Processing, asset.StoredFilename, cancellationToken);
                var outcome = await scanner.ScanAsync(content, cancellationToken);
                evaluation.RecordScanResult(
                    scanner.Id, scanner.IsRequired, outcome.Status, outcome.ThreatName, health.Version, clock.UtcNow);
                await repository.SaveChangesAsync(cancellationToken);
            }

            foreach (var validator in validators)
            {
                await using var content = await stagingStore.OpenAsync(
                    MediaAssetStorageState.Processing, asset.StoredFilename, cancellationToken);
                var outcome = await validator.ValidateAsync(asset, content, cancellationToken);
                evaluation.RecordValidationResult(validator.Id, outcome.IsValid, outcome.Message, clock.UtcNow);
                await repository.SaveChangesAsync(cancellationToken);
            }
        }
        catch (Exception exception)
        {
            // Re-queues rather than stranding the asset: MediaAssetStorageTransitions
            // already allows Processing -> Quarantine for exactly this
            // "transient scan/validator error" case (see its remarks). This
            // makes the failure retryable through the same path a first
            // attempt uses, rather than a dead end only Rejected/Trusted could
            // previously reach.
            //
            // Any exception, not just IO/socket ones: an unexpected failure that
            // skipped this recovery left the asset in Processing with a Pending
            // evaluation and no way to retry it. And CancellationToken.None, not
            // the caller's token: the usual reason we are here is that the
            // request was cancelled, so a recovery that honoured the same token
            // would itself be cancelled before it could save anything.
            await RecoverToQuarantineAsync(asset, DescribeFailure(exception), exception.GetType().Name);
            throw;
        }

        evaluation.Evaluate(clock.UtcNow);

        if (evaluation.Status == SecurityEvaluationStatus.Failed)
        {
            await stagingStore.MoveAsync(
                MediaAssetStorageState.Processing, MediaAssetStorageState.Rejected, asset.StoredFilename, cancellationToken);
            asset.TransitionStorageState(MediaAssetStorageState.Rejected, clock.UtcNow);
        }

        // Passed or ReviewRequired stays in Processing until an explicit
        // approval — see ApprovalService.

        await repository.SaveChangesAsync(cancellationToken);

        await audit.WriteAsync(
            AuditActions.AssetEvaluated,
            AuditSubjectTypes.MediaAsset,
            assetId.ToString(),
            new { AssetId = assetId, evaluation.Status },
            cancellationToken);

        if (evaluation.Status == SecurityEvaluationStatus.Failed &&
            evaluation.ScanResults.Any(result => result.Status == ScanResultStatus.Detected))
        {
            await DestroyDetectedFileAsync(asset, cancellationToken);
        }

        return SecurityEvaluationResult.Success(
            evaluation.Id, evaluation.Status, evaluation.CreatedAtUtc, evaluation.CompletedAtUtc);
    }

    private const int MaxFailureReasonLength = 1_024;

    /// <summary>
    /// How long a Pending evaluation may sit in Processing before it is
    /// presumed abandoned (the host restarted or the request died mid-scan)
    /// rather than still running. Comfortably above a scan of the largest
    /// permitted file, so a retry cannot start a second scan of a live one.
    /// </summary>
    private static readonly TimeSpan AbandonedEvaluationAge = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Returns an asset stranded in Processing with a Pending evaluation to
    /// Quarantine so it can be evaluated again.
    /// </summary>
    private async Task RecoverAbandonedEvaluationAsync(MediaAsset asset, CancellationToken cancellationToken)
    {
        var latest = await repository.FindLatestEvaluationAsync(asset.Id, cancellationToken);
        if (latest is not { Status: SecurityEvaluationStatus.Pending } ||
            clock.UtcNow - latest.CreatedAtUtc < AbandonedEvaluationAge)
        {
            return;
        }

        await RecoverToQuarantineAsync(
            asset,
            "The previous scan stopped without recording a result (the app may have restarted or the request was cancelled).",
            "Abandoned");
    }

    private async Task RecoverToQuarantineAsync(MediaAsset asset, string reason, string auditReason)
    {
        await stagingStore.MoveAsync(
            MediaAssetStorageState.Processing, MediaAssetStorageState.Quarantine, asset.StoredFilename, CancellationToken.None);
        asset.TransitionStorageState(MediaAssetStorageState.Quarantine, clock.UtcNow);
        asset.SetScanFailureReason(reason);
        await repository.SaveChangesAsync(CancellationToken.None);

        await audit.WriteAsync(
            AuditActions.AssetEvaluationFailed,
            AuditSubjectTypes.MediaAsset,
            asset.Id.ToString(),
            new { AssetId = asset.Id, Reason = auditReason },
            CancellationToken.None);
    }

    private static string DescribeFailure(Exception exception)
    {
        var detail = exception is OperationCanceledException
            ? "The scan was cancelled or timed out before it finished."
            : exception.Message;
        var reason = $"Scan interrupted ({exception.GetType().Name}): {detail}";
        return reason.Length <= MaxFailureReasonLength ? reason : reason[..MaxFailureReasonLength];
    }

    private async Task DestroyDetectedFileAsync(MediaAsset asset, CancellationToken cancellationToken)
    {
        try
        {
            // The rejected state and evaluation have already been committed.
            // If deletion fails, the retained rejected file remains visible to
            // an administrator rather than becoming an untracked orphan.
            await stagingStore.DeleteAsync(
                MediaAssetStorageState.Rejected, asset.StoredFilename, cancellationToken);
            asset.TransitionStorageState(MediaAssetStorageState.Destroyed, clock.UtcNow);
            await repository.SaveChangesAsync(cancellationToken);

            await audit.WriteAsync(
                AuditActions.AssetMalwareDestroyed,
                AuditSubjectTypes.MediaAsset,
                asset.Id.ToString(),
                new { AssetId = asset.Id },
                cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await audit.WriteAsync(
                AuditActions.AssetMalwareDestructionFailed,
                AuditSubjectTypes.MediaAsset,
                asset.Id.ToString(),
                new { AssetId = asset.Id, Reason = exception.GetType().Name },
                cancellationToken);
        }
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
