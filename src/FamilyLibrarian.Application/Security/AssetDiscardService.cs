using FamilyLibrarian.Application.Abstractions;
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
    IClock clock)
{
    public async Task<AssetDiscardResult> DiscardAsync(Guid assetId, CancellationToken cancellationToken)
    {
        var asset = await repository.FindAssetAsync(assetId, cancellationToken);
        if (asset is null)
        {
            return AssetDiscardResult.NotFound();
        }

        if (asset.StorageState is not (
            MediaAssetStorageState.Quarantine or
            MediaAssetStorageState.Rejected or
            MediaAssetStorageState.Unmatched))
        {
            return AssetDiscardResult.Invalid("Only quarantined, rejected, or unmatched files can be deleted.");
        }

        var previousState = asset.StorageState;
        await stagingStore.DeleteAsync(previousState, asset.StoredFilename, cancellationToken);
        asset.TransitionStorageState(MediaAssetStorageState.Destroyed, clock.UtcNow);
        await repository.SaveChangesAsync(cancellationToken);

        await audit.WriteAsync(
            AuditActions.AssetDestroyed,
            AuditSubjectTypes.MediaAsset,
            assetId.ToString(),
            new { AssetId = assetId, ActorUserId = currentUser.UserId, PreviousState = previousState.ToString() },
            cancellationToken);

        return AssetDiscardResult.Success();
    }
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
