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
        CancellationToken cancellationToken)
    {
        var result = await acquisitions.AcquireAsync(
            requestId,
            requestFormatId,
            providerId,
            providerResultId,
            cancellationToken);

        if (result.Outcome == ManualImportOutcome.Success)
        {
            await securityPipeline.EvaluateAsync(result.MediaAssetId!.Value, cancellationToken);
        }

        return result;
    }
}
