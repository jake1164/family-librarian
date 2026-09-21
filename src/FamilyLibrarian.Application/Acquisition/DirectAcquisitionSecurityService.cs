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
        bool allowDownloadTimeDrmValidation = false)
    {
        var result = await acquisitions.AcquireAsync(
            requestId,
            requestFormatId,
            providerId,
            providerResultId,
            cancellationToken,
            confirmLowConfidenceMatch,
            allowDownloadTimeDrmValidation);

        if (result.Outcome == ManualImportOutcome.Success)
        {
            // A bundle (e.g. a chaptered Gutenberg audiobook) stages several
            // sibling assets at once; each gets its own independent scan and
            // approval attempt, exactly like a single-file acquisition.
            foreach (var assetId in result.MediaAssetIds)
            {
                await securityPipeline.EvaluateAsync(assetId, cancellationToken);
            }
        }

        return result;
    }
}
