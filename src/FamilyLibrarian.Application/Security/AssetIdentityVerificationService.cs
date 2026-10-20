using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.SemanticAssessment;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Audit;
using FamilyLibrarian.Domain.Evidence;
using FamilyLibrarian.Application.ModelGateway;
using FamilyLibrarian.Domain.ModelGateway;

namespace FamilyLibrarian.Application.Security;

/// <summary>
/// Holds a clean, valid file unless its embedded identity matches the requested
/// Work. A held asset requires a separately guarded model release or administrator override before approval and publishing.
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
    IExpectedArtifactProfileSource? expectedProfiles = null,
    IArtifactEvidenceStore? profileEvidence = null,
    ArtifactDecisionService? modelDecisions = null, IModelWorkflowObserver? workflow = null) : IAssetIdentityVerificationService
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
        var result = verifier is null
            ? NoVerifierOutcome.For(asset)
            : await VerifySafelyAsync(verifier, asset, MediaAssetStorageState.Processing, cancellationToken);
        var expected = expectedProfiles is null ? null : await expectedProfiles.FindAsync(asset, cancellationToken);
        result = ArtifactHoldPolicy.Apply(asset, await CompareProfileAsync(asset, result, expected, cancellationToken));

        Guid? modelHoldUsageId = null;
        if (result.IsMatch && modelDecisions is not null &&
            await modelDecisions.GetHoldAsync(asset, result, expected, cancellationToken) is { } hold)
        {
            modelHoldUsageId = hold.UsageRecordId;
            result = result with { IsMatch = false, HoldCode = EvidenceReasonCodes.IdentityHold.ModelConcern,
                Reason = $"An automated reviewer raised a concern: {hold.Note}" };
        }

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
        if (modelHoldUsageId is not null && workflow is not null)
            await workflow.ObserveAsync(modelHoldUsageId, ModelWorkflowEffect.FileHoldApplied, cancellationToken);
        if (evidence is not null) await evidence.RecordIdentityVerificationAsync(asset, ForRecording(result), cancellationToken);

        if (!result.IsMatch && modelDecisions is not null)
            result = result with { ModelReleaseFindingId = await modelDecisions.GetReleaseAsync(asset, result, expected, cancellationToken) };

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

        // A recheck cannot release a model concern: only the administrator override may do that.
        if (asset.IdentityHoldCode == EvidenceReasonCodes.IdentityHold.ModelConcern)
            return AssetIdentityVerificationResult.Unmatched("model-concern", asset.IdentityMismatchReason)
                with { HoldCode = EvidenceReasonCodes.IdentityHold.ModelConcern };

        var verifier = verifiers.FirstOrDefault(verifier => verifier.Supports(asset));
        var result = verifier is null
            ? NoVerifierOutcome.For(asset)
            : await VerifySafelyAsync(verifier, asset, MediaAssetStorageState.Unmatched, cancellationToken);
        var expected = expectedProfiles is null ? null : await expectedProfiles.FindAsync(asset, cancellationToken);
        result = ArtifactHoldPolicy.Apply(asset, await CompareProfileAsync(asset, result, expected, cancellationToken));

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

        if (evidence is not null) await evidence.RecordIdentityVerificationAsync(asset, ForRecording(result), cancellationToken);

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
        MediaAsset asset, AssetIdentityVerificationResult result, ExpectedArtifactProfile? expected, CancellationToken cancellationToken)
    {
        if (expected is null) return result;
        var facts = result.Facts.ToList();
        if (profileEvidence is not null)
        {
            // These measurements were recorded by the inspectors. Reuse them for comparison, never re-record them.
            var stored = await profileEvidence.ListLatestAudioMeasurementsAsync(asset, cancellationToken);
            facts.AddRange(stored.Where(f => !facts.Any(current => current.Code == f.Code && current.FindingId == f.FindingId))
                .Select(f => new IdentityFact(f.FindingId, f.Dimension, f.Code, f.Polarity, f.Value)));
        }
        facts.AddRange(ArtifactProfileComparer.Compare(facts, expected));
        if (expected.AcceptedVariantKind == FamilyLibrarian.Domain.Requests.AcquisitionQuestionKind.AbridgedOnly)
            facts = facts.Select(f => f.Code == EvidenceReasonCodes.ProfileComparison.AbridgedMarker && f.Polarity == EvidencePolarity.Condition
                ? f with { Polarity = EvidencePolarity.Weak } : f).ToList();
        return result with { Facts = facts };
    }

    private AssetIdentityVerificationResult ForRecording(AssetIdentityVerificationResult result) => profileEvidence is null
        ? result
        : result with { Facts = result.Facts.Where(f => f.Code is not
            (EvidenceReasonCodes.ArtifactProfile.AudioTotalDuration or EvidenceReasonCodes.ArtifactProfile.AudioChapterTitles)).ToArray() };

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
