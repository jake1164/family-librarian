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
/// A passed evaluation can be approved by the clean-scan policy, while an
/// administrator handles review-required results. The evaluation domain entity
/// enforces the harder rule underneath: a <see cref="SecurityEvaluationStatus.Failed"/>
/// evaluation cannot be approved no matter who calls this.
/// </remarks>
public sealed class ApprovalService(
    ISecurityEvaluationRepository repository,
    IAssetStagingStore stagingStore,
    MediaAssetPublishingCoordinator publishing,
    IAssetIdentityVerificationService identity,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock) : IPolicyAssetApprovalService
{
    public async Task<ApprovalResult> ApproveAsync(Guid assetId, string? reason, CancellationToken cancellationToken)
        => await ApproveCoreAsync(
            assetId,
            ApprovalActorType.Admin,
            currentUser.UserId,
            policyName: null,
            reason,
            cancellationToken);

    /// <summary>
    /// Records the trusted clean-scan policy's decision. This path is intentionally unavailable for
    /// review-required evaluations: an administrator must make that exception decision.
    /// </summary>
    public Task<ApprovalResult> ApproveByPolicyAsync(
        Guid assetId,
        string policyName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);

        return ApproveCoreAsync(
            assetId,
            ApprovalActorType.Policy,
            actorUserId: null,
            policyName,
            reason: null,
            cancellationToken);
    }

    private async Task<ApprovalResult> ApproveCoreAsync(
        Guid assetId,
        ApprovalActorType actorType,
        Guid? actorUserId,
        string? policyName,
        string? reason,
        CancellationToken cancellationToken)
    {
        var asset = await repository.FindAssetAsync(assetId, cancellationToken);
        if (asset is null)
        {
            return ApprovalResult.NotFound();
        }
        if (asset.StorageState != MediaAssetStorageState.Processing)
        {
            return ApprovalResult.Invalid("Only a security-review asset can be approved.");
        }

        var evaluation = await repository.FindLatestEvaluationAsync(assetId, cancellationToken);
        if (evaluation is null)
        {
            return ApprovalResult.Invalid("This asset has not been evaluated yet.");
        }

        // Identity verification is deliberately part of every approval path,
        // including a librarian's approval of a review-required scan. A clean
        // security evaluation proves the file is safe, not that it is the
        // requested book.
        var identityResult = await identity.VerifyAsync(assetId, cancellationToken);
        if (!identityResult.IsMatch)
        {
            return ApprovalResult.IdentityUnmatched();
        }

        var now = clock.UtcNow;
        try
        {
            evaluation.Approve(actorType, actorUserId, policyName, reason, now);
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
            new { AssetId = assetId, ActorType = actorType, PolicyName = policyName, Reason = reason },
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

    public static ApprovalResult IdentityUnmatched() => new(
        ApprovalOutcome.IdentityUnmatched,
        "The file metadata does not match the requested book. It has been held for librarian identification.");
}

public enum ApprovalOutcome
{
    Success,
    NotFound,
    Invalid,
    IdentityUnmatched
}
