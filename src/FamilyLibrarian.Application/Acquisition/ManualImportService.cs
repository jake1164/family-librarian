using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Domain.Audit;

namespace FamilyLibrarian.Application.Acquisition;

/// <summary>
/// The one trusted path for an administrator to attach a file to a request,
/// before any provider can return one.
/// </summary>
/// <remarks>
/// Validates the request/format, then delegates staging to
/// <see cref="AcquisitionStagingService"/> — the same staging path a bundled
/// provider's automated fetch (M11) now also uses.
/// </remarks>
public sealed class ManualImportService(
    IRequestRepository requests,
    AcquisitionStagingService staging)
{
    private const string ManualProviderId = "manual";

    public async Task<ManualImportResult> ImportAsync(
        Guid requestId,
        Guid requestFormatId,
        Stream fileContent,
        string originalFilename,
        CancellationToken cancellationToken)
    {
        var request = await requests.FindRequestForAdminAsync(requestId, cancellationToken);
        if (request is null)
        {
            return ManualImportResult.Invalid("That request does not exist.");
        }

        var format = request.Formats.FirstOrDefault(format => format.Id == requestFormatId);
        if (format is null)
        {
            return ManualImportResult.Invalid("That format is not part of this request.");
        }

        return await staging.StageAsync(
            request,
            format,
            fileContent,
            originalFilename,
            ManualProviderId,
            AuditActions.ManualImportStaged,
            candidateTitle: null,
            candidateAuthor: null,
            cancellationToken);
    }
}

public sealed record ManualImportResult(
    ManualImportOutcome Outcome,
    Guid? AcquisitionJobId,
    Guid? MediaAssetId,
    string? Error)
{
    public static ManualImportResult Success(Guid jobId, Guid assetId) =>
        new(ManualImportOutcome.Success, jobId, assetId, null);

    public static ManualImportResult Invalid(string error) =>
        new(ManualImportOutcome.Invalid, null, null, error);

    public static ManualImportResult DuplicateDetected() =>
        new(
            ManualImportOutcome.DuplicateDetected,
            null,
            null,
            "A file with the same content has already been staged for this format.");

    public static ManualImportResult WaitingForSecurityScanner() =>
        new(
            ManualImportOutcome.WaitingForSecurityScanner,
            null,
            null,
            "A required security scanner is unavailable. Try again once it has recovered.");
}

public enum ManualImportOutcome
{
    Success,
    Invalid,
    DuplicateDetected,
    WaitingForSecurityScanner
}
