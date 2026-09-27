using FamilyLibrarian.Application.Security;

namespace FamilyLibrarian.Application.Acquisition;

/// <summary>
/// Runs every successful server-side direct acquisition through the same
/// automated security and publishing path as a successful manual import.
/// </summary>
public sealed class DirectAcquisitionSecurityService(
    DirectAcquisitionService acquisitions,
    AutomatedSecurityPipeline securityPipeline)
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
}
