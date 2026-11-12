using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.DecisionCapture;
using FamilyLibrarian.Domain.DecisionCapture;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Audit;

namespace FamilyLibrarian.Application.Security;

/// <summary>
/// Permanently removes a non-trusted staged artifact while retaining its
/// workflow and audit evidence.
/// </summary>
public sealed class AssetDiscardService(
    ISecurityEvaluationRepository repository,
    IAssetStagingStore stagingStore,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock,
    IDecisionOutcomeRecorder? outcomes = null)
{
    public async Task<AssetDiscardResult> DiscardAsync(Guid assetId, CancellationToken cancellationToken)
    {
        var asset = await repository.FindAssetAsync(assetId, cancellationToken);
        if (asset is null)
        {
            return AssetDiscardResult.NotFound();
        }

        if (!IsDiscardable(asset))
        {
            return AssetDiscardResult.Invalid("Only quarantined, rejected, or unmatched files can be deleted.");
        }

        // A multi-track set is one book: a set with a track removed is useless, and deleting it one track at a time
        // meant twenty clicks for twenty files. Every sibling still awaiting a decision goes with it; a trusted or
        // published track never does.
        var targets = asset.BundleId is { } bundleId
            ? (await repository.FindAssetsByBundleIdAsync(bundleId, cancellationToken)).Where(IsDiscardable).ToArray()
            : [asset];
        if (targets.All(target => target.Id != asset.Id))
        {
            targets = [asset, .. targets];
        }

        foreach (var target in targets)
        {
            await DestroyAsync(target, reason: null, cancellationToken);
            // Only a person's discard is a fact; the automatic clean-up of settled copies (below) is housekeeping, not a judgment.
            await outcomes.ReportAsync(DecisionOutcomeKind.FileDiscarded, "admin-discard", DecisionOutcomeStrength.Weak,
                DecisionOutcomeTarget.File(target.Id), cancellationToken, administratorDecided: true);
        }

        return AssetDiscardResult.Success();
    }

    /// <summary>
    /// Deletes held and rejected copies once their request format is settled (in the library, or cancelled). They were
    /// kept only so a librarian could release one if the checks got it wrong; after that they are disk-space cruft. The
    /// asset rows, evidence and audit trail stay.
    /// </summary>
    /// <returns>How many files were deleted.</returns>
    public async Task<int> DiscardSettledHeldCopiesAsync(int maximumCount, CancellationToken cancellationToken)
    {
        var settled = await repository.ListHeldAssetsOfSettledFormatsAsync(maximumCount, cancellationToken);
        foreach (var asset in settled)
        {
            await DestroyAsync(asset, "The request format is settled; a held copy is no longer needed.", cancellationToken);
        }

        return settled.Count;
    }

    /// <summary>
    /// Removes copies waiting for a person (held, or checked and waiting for a librarian's decision) once a better copy of the same
    /// format has passed its checks. This does not wait for publishing or for the format to settle: the waiting copy is not the only
    /// copy any more, which is what made it worth keeping.
    /// </summary>
    /// <returns>How many files were deleted.</returns>
    public async Task<int> DiscardSupersededHeldCopiesAsync(int maximumCount, CancellationToken cancellationToken)
    {
        var superseded = await repository.ListHeldAssetsSupersededByVerifiedCopyAsync(maximumCount, clock.UtcNow - ApprovalInProgressGrace, cancellationToken);
        foreach (var asset in superseded)
        {
            await DestroyAsync(asset, "A better copy passed its checks and replaced this held copy.", cancellationToken);
        }

        return superseded.Count;
    }

    /// <summary>A checked copy is only called "waiting for a decision" after this long, so one that is being approved right now is never removed.</summary>
    private static readonly TimeSpan ApprovalInProgressGrace = TimeSpan.FromHours(1);

    private async Task DestroyAsync(MediaAsset target, string? reason, CancellationToken cancellationToken)
    {
        var previousState = target.StorageState;
        await stagingStore.DeleteAsync(previousState, target.StoredFilename, cancellationToken);
        target.TransitionStorageState(MediaAssetStorageState.Destroyed, clock.UtcNow);
        await repository.SaveChangesAsync(cancellationToken);

        await audit.WriteAsync(
            AuditActions.AssetDestroyed,
            AuditSubjectTypes.MediaAsset,
            target.Id.ToString(),
            reason is null
                ? new { AssetId = target.Id, ActorUserId = currentUser.UserId, PreviousState = previousState.ToString() }
                : new { AssetId = target.Id, ActorUserId = (Guid?)null, PreviousState = previousState.ToString(), Reason = reason },
            cancellationToken);
    }

    private static bool IsDiscardable(MediaAsset asset) => asset.StorageState is
        MediaAssetStorageState.Quarantine or MediaAssetStorageState.Rejected or MediaAssetStorageState.Unmatched;
}

public sealed record AssetDiscardResult(AssetDiscardOutcome Outcome, string? Error)
{
    public static AssetDiscardResult Success() => new(AssetDiscardOutcome.Success, null);

    public static AssetDiscardResult NotFound() => new(AssetDiscardOutcome.NotFound, null);

    public static AssetDiscardResult Invalid(string error) => new(AssetDiscardOutcome.Invalid, error);
}

public enum AssetDiscardOutcome
{
    Success,
    NotFound,
    Invalid
}
