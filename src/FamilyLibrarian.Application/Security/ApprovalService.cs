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
    /// Approves an asset the deterministic identity check has already held
    /// as <see cref="MediaAssetStorageState.Unmatched"/>, on a librarian's
    /// explicit say-so after reviewing <see cref="MediaAsset.IdentityMismatchReason"/>
    /// -- e.g. a byline formatting difference the check is too conservative
    /// to accept on its own. This is the one path that does not call
    /// <see cref="IAssetIdentityVerificationService.VerifyAsync"/>: the
    /// deterministic check already failed and would fail identically again,
    /// so re-running it here would just flip the asset straight back to
    /// Unmatched and make the override a no-op. <paramref name="reason"/> is
    /// required so the override -- not just the ordinary approval -- has a
    /// stated justification in the audit trail.
    /// </summary>
    public Task<ApprovalResult> OverrideIdentityAndApproveAsync(
        Guid assetId, string reason, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return ApproveCoreAsync(
            assetId,
            ApprovalActorType.Admin,
            currentUser.UserId,
            policyName: null,
            reason,
            cancellationToken,
            identityOverrideConfirmed: true);
    }

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
        CancellationToken cancellationToken,
        bool identityOverrideConfirmed = false)
    {
        var asset = await repository.FindAssetAsync(assetId, cancellationToken);
        if (asset is null)
        {
            return ApprovalResult.NotFound();
        }

        if (identityOverrideConfirmed)
        {
            if (asset.StorageState != MediaAssetStorageState.Unmatched)
            {
                return ApprovalResult.Invalid("Only an asset held for identity review can have its match overridden.");
            }
        }
        else if (asset.StorageState != MediaAssetStorageState.Processing)
        {
            return ApprovalResult.Invalid("Only a security-review asset can be approved.");
        }

        var evaluation = await repository.FindLatestEvaluationAsync(assetId, cancellationToken);
        if (evaluation is null)
        {
            return ApprovalResult.Invalid("This asset has not been evaluated yet.");
        }

        var now = clock.UtcNow;
        if (identityOverrideConfirmed)
        {
            // The only allowed exit from Unmatched is back through Processing
            // (MediaAssetStorageTransitions) -- an override still passes
            // through it on the way to Trusted below, it just skips
            // re-running the deterministic check that already failed once.
            await stagingStore.MoveAsync(
                MediaAssetStorageState.Unmatched, MediaAssetStorageState.Processing, asset.StoredFilename, cancellationToken);
            asset.TransitionStorageState(MediaAssetStorageState.Processing, now);
            asset.SetIdentityMismatchReason(null);
        }
        else
        {
            // Identity verification is deliberately part of every ordinary
            // approval path, including a librarian's approval of a
            // review-required scan. A clean security evaluation proves the
            // file is safe, not that it is the requested book.
            var identityResult = await identity.VerifyAsync(assetId, cancellationToken);
            if (!identityResult.IsMatch)
            {
                return ApprovalResult.IdentityUnmatched();
            }
        }

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

        if (identityOverrideConfirmed)
        {
            await audit.WriteAsync(
                AuditActions.AssetIdentityOverridden,
                AuditSubjectTypes.MediaAsset,
                assetId.ToString(),
                new { AssetId = assetId, ActorUserId = actorUserId, Reason = reason },
                cancellationToken);
        }

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
        if (asset.BundleId is null)
        {
            await publishing.PublishAsync(asset, cancellationToken);
        }
        else
        {
            // A bundle (e.g. a chaptered Gutenberg audiobook) publishes once,
            // as a single upload, only once every sibling track has reached
            // Trusted. Earlier tracks to arrive here simply wait; the last
            // one triggers the publish for the whole set. If a sibling is
            // instead held (rejected or unmatched), the bundle never
            // completes and its trusted tracks surface for a librarian on
            // the ordinary per-asset security review queue.
            var siblings = await repository.FindAssetsByBundleIdAsync(asset.BundleId.Value, cancellationToken);
            var trustedSiblings = siblings
                .Where(sibling => sibling.StorageState == MediaAssetStorageState.Trusted)
                .OrderBy(sibling => sibling.BundleSequence)
                .ToArray();
            if (trustedSiblings.Length == asset.BundleTrackCount)
            {
                await publishing.PublishBundleAsync(trustedSiblings, cancellationToken);
            }
        }

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
