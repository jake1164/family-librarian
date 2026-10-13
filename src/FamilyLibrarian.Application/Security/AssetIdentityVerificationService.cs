using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Audit;

namespace FamilyLibrarian.Application.Security;

/// <summary>
/// Holds a clean, valid file unless its embedded identity matches the requested
/// Work. A held asset cannot reach policy approval or a publishing destination.
/// </summary>
public interface IAssetIdentityVerificationService
{
    Task<AssetIdentityVerificationResult> VerifyAsync(Guid assetId, CancellationToken cancellationToken);

    Task<AssetIdentityVerificationResult> RetryUnmatchedAsync(Guid assetId, CancellationToken cancellationToken);
}

public sealed class AssetIdentityVerificationService(
    ISecurityEvaluationRepository repository,
    IAssetStagingStore stagingStore,
    IEnumerable<IAssetIdentityVerifier> verifiers,
    IAuditWriter audit,
    IClock clock,
    IArtifactEvidenceRecorder? evidence = null,
    IExpectedArtifactProfileSource? expectedProfiles = null) : IAssetIdentityVerificationService
{
    private readonly IExpectedArtifactProfileSource? _expectedProfiles = expectedProfiles;

    public async Task<AssetIdentityVerificationResult> VerifyAsync(
        Guid assetId,
        CancellationToken cancellationToken)
    {
        var asset = await repository.FindAssetAsync(assetId, cancellationToken)
            ?? throw new InvalidOperationException("The staged asset no longer exists.");
        if (asset.StorageState != MediaAssetStorageState.Processing)
        {
            throw new InvalidOperationException("Only a security-processed asset can have its identity verified.");
        }

        var verifier = verifiers.FirstOrDefault(verifier => verifier.Supports(asset));
        var result = verifier is null
            ? NoVerifierOutcome.For(asset)
            : await VerifySafelyAsync(verifier, asset, MediaAssetStorageState.Processing, cancellationToken);
        result = ArtifactHoldPolicy.Apply(asset, await CompareProfileAsync(asset, result, cancellationToken));

        if (!result.IsMatch)
        {
            await stagingStore.MoveAsync(
                MediaAssetStorageState.Processing,
                MediaAssetStorageState.Unmatched,
                asset.StoredFilename,
                cancellationToken);
            asset.TransitionStorageState(MediaAssetStorageState.Unmatched, clock.UtcNow);
            asset.SetIdentityMismatchReason(result.Reason);
            asset.SetIdentityHoldCode(result.HoldCode ?? ExistingHoldCodes.Classify(result));
            await repository.SaveChangesAsync(cancellationToken);
        }

        await audit.WriteAsync(
            result.IsMatch ? AuditActions.AssetIdentityVerified : AuditActions.AssetIdentityUnmatched,
            AuditSubjectTypes.MediaAsset,
            assetId.ToString(),
            new { AssetId = assetId, result.VerifierId, result.IsMatch, result.Reason },
            cancellationToken);

        // Evidence only: recorded after the verdict above is saved and audited.
        if (evidence is not null) await evidence.RecordIdentityVerificationAsync(asset, result, cancellationToken);

        return result;
    }

    public async Task<AssetIdentityVerificationResult> RetryUnmatchedAsync(
        Guid assetId,
        CancellationToken cancellationToken)
    {
        var asset = await repository.FindAssetAsync(assetId, cancellationToken)
            ?? throw new InvalidOperationException("The staged asset no longer exists.");
        if (asset.StorageState != MediaAssetStorageState.Unmatched)
        {
            throw new InvalidOperationException("Only an unmatched asset can have its identity verification retried.");
        }

        var verifier = verifiers.FirstOrDefault(verifier => verifier.Supports(asset));
        var result = verifier is null
            ? NoVerifierOutcome.For(asset)
            : await VerifySafelyAsync(verifier, asset, MediaAssetStorageState.Unmatched, cancellationToken);
        result = ArtifactHoldPolicy.Apply(asset, await CompareProfileAsync(asset, result, cancellationToken));

        if (result.IsMatch)
        {
            await stagingStore.MoveAsync(
                MediaAssetStorageState.Unmatched,
                MediaAssetStorageState.Processing,
                asset.StoredFilename,
                cancellationToken);
            asset.TransitionStorageState(MediaAssetStorageState.Processing, clock.UtcNow);
            asset.SetIdentityMismatchReason(null);
            asset.SetIdentityHoldCode(null);
            await repository.SaveChangesAsync(cancellationToken);
        }
        else if (result.Reason != asset.IdentityMismatchReason)
        {
            // The comparison itself may have changed (e.g. the catalog Work's
            // title was corrected) even though the outcome is still a
            // mismatch -- keep the displayed reason current rather than
            // stale from the original hold.
            asset.SetIdentityMismatchReason(result.Reason);
            asset.SetIdentityHoldCode(result.HoldCode ?? ExistingHoldCodes.Classify(result));
            await repository.SaveChangesAsync(cancellationToken);
        }

        await audit.WriteAsync(
            result.IsMatch ? AuditActions.AssetIdentityVerified : AuditActions.AssetIdentityUnmatched,
            AuditSubjectTypes.MediaAsset,
            assetId.ToString(),
            new { AssetId = assetId, result.VerifierId, result.IsMatch, result.Reason, Retried = true },
            cancellationToken);

        if (evidence is not null) await evidence.RecordIdentityVerificationAsync(asset, result, cancellationToken);

        return result;
    }

    private async Task<AssetIdentityVerificationResult> VerifyWithAsync(
        IAssetIdentityVerifier verifier,
        MediaAsset asset,
        MediaAssetStorageState storageState,
        CancellationToken cancellationToken)
    {
        await using var content = await stagingStore.OpenAsync(
            storageState,
            asset.StoredFilename,
            cancellationToken);
        return await verifier.VerifyAsync(asset, content, cancellationToken);
    }

    private async Task<AssetIdentityVerificationResult> CompareProfileAsync(
        MediaAsset asset, AssetIdentityVerificationResult result, CancellationToken cancellationToken)
    {
        if (_expectedProfiles is null) return result;
        var expected = await _expectedProfiles.FindAsync(asset, cancellationToken);
        if (expected is null) return result;
        return result with { Facts = [.. result.Facts, .. ArtifactProfileComparer.Compare(result.Facts, expected)] };
    }

    private async Task<AssetIdentityVerificationResult> VerifySafelyAsync(
        IAssetIdentityVerifier verifier,
        MediaAsset asset,
        MediaAssetStorageState storageState,
        CancellationToken cancellationToken)
    {
        try
        {
            return await VerifyWithAsync(verifier, asset, storageState, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // A verifier outage or malformed input must never turn into an
            // implicit approval. Keep the artifact for a future retry or
            // librarian decision without recording untrusted details.
            return AssetIdentityVerificationResult.Unmatched(verifier.Id);
        }
    }
}
