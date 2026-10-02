using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Acquisition;

namespace FamilyLibrarian.Application.Acquisition;

/// <summary>
/// Runs every successful server-side direct acquisition through the same
/// automated security and publishing path as a successful manual import.
/// </summary>
public sealed class DirectAcquisitionSecurityService(
    DirectAcquisitionService acquisitions,
    AutomatedSecurityPipeline securityPipeline,
    ISecurityEvaluationRepository? assets = null)
{
    public async Task<ManualImportResult> AcquireAndEvaluateAsync(
        Guid requestId,
        Guid requestFormatId,
        string providerId,
        string providerResultId,
        CancellationToken cancellationToken,
        bool confirmLowConfidenceMatch = false,
        bool allowDownloadTimeDrmValidation = false,
        bool isAutomaticAcquisition = false)
    {
        var result = await acquisitions.AcquireAsync(
            requestId,
            requestFormatId,
            providerId,
            providerResultId,
            cancellationToken,
            confirmLowConfidenceMatch,
            allowDownloadTimeDrmValidation,
            isAutomaticAcquisition);

        if (result.Outcome == ManualImportOutcome.Success)
        {
            if (result.MediaAssetIds.Count == 1)
                await securityPipeline.EvaluateAsync(result.MediaAssetIds[0], cancellationToken);
            else
                await securityPipeline.EvaluateBundleAsync(result.MediaAssetIds, cancellationToken);
        }

        return result;
    }

    /// <summary>
    /// Same as <see cref="AcquireAndEvaluateAsync"/>, but additionally reports
    /// whether the bytes that arrived actually verified as the requested book.
    /// </summary>
    /// <returns>
    /// The acquisition result, plus the identity-mismatch reason when the
    /// download succeeded and was clean but the staged file turned out not to
    /// be the requested work. Null means nothing contradicted the request.
    /// </returns>
    /// <remarks>
    /// An identity failure used to be invisible to the caller.
    /// <see cref="AutomatedSecurityPipeline"/> deliberately treats
    /// <c>IdentityUnmatched</c> as a non-error -- correctly, since the asset
    /// must be held rather than published -- so this method returned
    /// <see cref="ManualImportOutcome.Success"/> for a download of the wrong
    /// book, and an unattended acquisition recorded "acquired" and stopped.
    /// The request then sat with a quarantined file and nothing reported.
    /// Surfacing it here, rather than inside the pipeline, keeps the security
    /// layer free of any dependency on requests or acquisition policy: the
    /// pipeline still only decides trust, and the acquisition path decides
    /// what an untrusted result means for the request.
    /// </remarks>
    public async Task<(ManualImportResult Result, string? IdentityFailure)> AcquireEvaluateAndVerifyAsync(
        Guid requestId,
        Guid requestFormatId,
        string providerId,
        string providerResultId,
        CancellationToken cancellationToken,
        bool confirmLowConfidenceMatch = false,
        bool allowDownloadTimeDrmValidation = false,
        bool isAutomaticAcquisition = false)
    {
        var result = await AcquireAndEvaluateAsync(
            requestId, requestFormatId, providerId, providerResultId, cancellationToken,
            confirmLowConfidenceMatch, allowDownloadTimeDrmValidation, isAutomaticAcquisition);

        if (result.Outcome != ManualImportOutcome.Success || assets is null)
        {
            return (result, null);
        }

        foreach (var assetId in result.MediaAssetIds)
        {
            var asset = await assets.FindAssetAsync(assetId, cancellationToken);
            if (asset is null)
            {
                continue;
            }

            // Unmatched is the specific "this is not the book that was asked
            // for" verdict. Quarantine or a still-Processing state is a
            // different thing (a scan that has not finished) and must not be
            // reported as a wrong book.
            if (asset.StorageState == MediaAssetStorageState.Unmatched)
            {
                return (result, asset.IdentityMismatchReason ??
                    "The acquired file could not be verified as the requested book.");
            }
        }

        return (result, null);
    }
}
