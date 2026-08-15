using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Audit;
using FamilyLibrarian.Domain.Security;

namespace FamilyLibrarian.Application.Security;

/// <summary>
/// The only code path allowed to move a <c>MediaAsset</c> into
/// <see cref="MediaAssetStorageState.Trusted"/>.
/// </summary>
/// <remarks>
/// Every approval is an explicit administrator action in V1 — there is no
/// automatic policy-driven approval yet, even for a clean
/// <see cref="SecurityEvaluationStatus.Passed"/> result. The evaluation domain
/// entity still enforces the harder rule underneath this: a
/// <see cref="SecurityEvaluationStatus.Failed"/> evaluation cannot be approved
/// no matter who calls this.
/// </remarks>
public sealed class ApprovalService(
    ISecurityEvaluationRepository repository,
    IAssetStagingStore stagingStore,
    MediaAssetPublishingCoordinator publishing,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock)
{
    public async Task<ApprovalResult> ApproveAsync(Guid assetId, string? reason, CancellationToken cancellationToken)
    {
        var asset = await repository.FindAssetAsync(assetId, cancellationToken);
        if (asset is null)
        {
            return ApprovalResult.NotFound();
        }

        var evaluation = await repository.FindLatestEvaluationAsync(assetId, cancellationToken);
        if (evaluation is null)
        {
            return ApprovalResult.Invalid("This asset has not been evaluated yet.");
        }

        var now = clock.UtcNow;
        try
        {
            evaluation.Approve(ApprovalActorType.Admin, currentUser.UserId, policyName: null, reason, now);
        }
        catch (InvalidOperationException exception)
        {
            return ApprovalResult.Invalid(exception.Message);
        }

        await stagingStore.MoveAsync(
            MediaAssetStorageState.Processing, MediaAssetStorageState.Trusted, asset.StoredFilename, cancellationToken);
        asset.TransitionStorageState(MediaAssetStorageState.Trusted, now);

        await repository.SaveChangesAsync(cancellationToken);

        await audit.WriteAsync(
            AuditActions.AssetApproved,
            AuditSubjectTypes.MediaAsset,
            assetId.ToString(),
            new { AssetId = assetId, Reason = reason },
            cancellationToken);

        // Best-effort: a publish failure must never undo or fail an approval
        // decision that has already been made and committed above. See
        // MediaAssetPublishingCoordinator for why this is safe to call
        // unconditionally and never throws.
        await publishing.PublishAsync(asset, cancellationToken);

        return ApprovalResult.Success();
    }

    public async Task<ApprovalResult> RejectAsync(Guid assetId, string? reason, CancellationToken cancellationToken)
    {
        var asset = await repository.FindAssetAsync(assetId, cancellationToken);
        if (asset is null)
        {
            return ApprovalResult.NotFound();
        }

        var evaluation = await repository.FindLatestEvaluationAsync(assetId, cancellationToken);
        if (evaluation is null)
        {
            return ApprovalResult.Invalid("This asset has not been evaluated yet.");
        }

        var now = clock.UtcNow;
        evaluation.Reject(ApprovalActorType.Admin, currentUser.UserId, reason, now);

        // The fail-closed policy already moved a Failed evaluation's asset to
        // Rejected; an admin rejecting a Passed/ReviewRequired result has to
        // make that move itself.
        if (asset.StorageState != MediaAssetStorageState.Rejected)
        {
            await stagingStore.MoveAsync(
                asset.StorageState, MediaAssetStorageState.Rejected, asset.StoredFilename, cancellationToken);
            asset.TransitionStorageState(MediaAssetStorageState.Rejected, now);
        }

        await repository.SaveChangesAsync(cancellationToken);

        await audit.WriteAsync(
            AuditActions.AssetRejected,
            AuditSubjectTypes.MediaAsset,
            assetId.ToString(),
            new { AssetId = assetId, Reason = reason },
            cancellationToken);

        return ApprovalResult.Success();
    }
}

public sealed record ApprovalResult(ApprovalOutcome Outcome, string? Error)
{
    public static ApprovalResult Success() => new(ApprovalOutcome.Success, null);

    public static ApprovalResult NotFound() => new(ApprovalOutcome.NotFound, null);

    public static ApprovalResult Invalid(string error) => new(ApprovalOutcome.Invalid, error);
}

public enum ApprovalOutcome
{
    Success,
    NotFound,
    Invalid
}
