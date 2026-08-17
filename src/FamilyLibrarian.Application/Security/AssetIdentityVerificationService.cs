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
}

public sealed class AssetIdentityVerificationService(
    ISecurityEvaluationRepository repository,
    IAssetStagingStore stagingStore,
    IEnumerable<IAssetIdentityVerifier> verifiers,
    IAuditWriter audit,
    IClock clock) : IAssetIdentityVerificationService
{
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
        // This slice verifies EPUB package metadata. Other supported formats
        // retain their established security and review workflow until they
        // gain a format-specific identity verifier.
        var result = verifier is null
            ? AssetIdentityVerificationResult.Match("not-applicable")
            : await VerifySafelyAsync(verifier, asset, cancellationToken);

        if (!result.IsMatch)
        {
            await stagingStore.MoveAsync(
                MediaAssetStorageState.Processing,
                MediaAssetStorageState.Unmatched,
                asset.StoredFilename,
                cancellationToken);
            asset.TransitionStorageState(MediaAssetStorageState.Unmatched, clock.UtcNow);
            await repository.SaveChangesAsync(cancellationToken);
        }

        await audit.WriteAsync(
            result.IsMatch ? AuditActions.AssetIdentityVerified : AuditActions.AssetIdentityUnmatched,
            AuditSubjectTypes.MediaAsset,
            assetId.ToString(),
            new { AssetId = assetId, result.VerifierId, result.IsMatch },
            cancellationToken);

        return result;
    }

    private async Task<AssetIdentityVerificationResult> VerifyWithAsync(
        IAssetIdentityVerifier verifier,
        MediaAsset asset,
        CancellationToken cancellationToken)
    {
        await using var content = await stagingStore.OpenAsync(
            MediaAssetStorageState.Processing,
            asset.StoredFilename,
            cancellationToken);
        return await verifier.VerifyAsync(asset, content, cancellationToken);
    }

    private async Task<AssetIdentityVerificationResult> VerifySafelyAsync(
        IAssetIdentityVerifier verifier,
        MediaAsset asset,
        CancellationToken cancellationToken)
    {
        try
        {
            return await VerifyWithAsync(verifier, asset, cancellationToken);
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
