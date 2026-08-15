using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Audit;

namespace FamilyLibrarian.Application.Acquisition;

/// <summary>
/// The one trusted path for an administrator to attach a file to a request,
/// before any provider can return one.
/// </summary>
/// <remarks>
/// Every artifact — manual today, provider-returned from M11 on — enters
/// Family Librarian-controlled staging through code shaped like this: nothing
/// here (or in <see cref="IAssetStagingStore"/>) ever hands the caller a
/// browser-servable path, and the resulting <see cref="MediaAsset"/> starts in
/// <see cref="MediaAssetStorageState.Quarantine"/> with no path to
/// <see cref="MediaAssetStorageState.Trusted"/> until an M10
/// <c>ApprovalService</c> decision moves it there.
/// <para>
/// <see cref="IAcquisitionBoundaryGuard"/> is checked before a single byte is
/// staged: required-scanner unavailability blocks the whole acquisition
/// boundary, not merely later delivery/approval.
/// </para>
/// </remarks>
public sealed class ManualImportService(
    IRequestRepository requests,
    IAcquisitionRepository acquisitions,
    IAssetStagingStore stagingStore,
    IAcquisitionBoundaryGuard boundaryGuard,
    ManualImportPolicy policy,
    IAuditWriter audit,
    IClock clock)
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

        var extension = Path.GetExtension(originalFilename);
        if (string.IsNullOrEmpty(extension) || !policy.IsExtensionAllowed(format.MediaType, extension))
        {
            return ManualImportResult.Invalid(
                $"'{extension}' is not an allowed file type for {format.MediaType}.");
        }

        if (!await boundaryGuard.CanAcceptNewArtifactAsync(cancellationToken))
        {
            await audit.WriteAsync(
                AuditActions.ManualImportRejectedNoScanner,
                AuditSubjectTypes.BookRequest,
                requestId.ToString(),
                new { RequestId = requestId, RequestFormatId = requestFormatId },
                cancellationToken);
            return ManualImportResult.WaitingForSecurityScanner();
        }

        StagedFile staged;
        try
        {
            staged = await stagingStore.WriteToQuarantineAsync(
                fileContent,
                originalFilename,
                policy.MaxUploadSizeBytes,
                cancellationToken);
        }
        catch (AssetTooLargeException)
        {
            return ManualImportResult.Invalid(
                $"The file exceeds the {policy.MaxUploadSizeBytes}-byte upload limit.");
        }

        // The file is already in quarantine at this point, which is safe: it is
        // untrusted storage with no browser access and no path to Trusted. A
        // rejected upload is simply left there rather than deleted — quarantine
        // retention/cleanup is a separate, later concern.
        if (!KnownFormatContentTypes.ByExtension.TryGetValue(extension, out var expectedContentType) ||
            !string.Equals(staged.DetectedMimeType, expectedContentType, StringComparison.OrdinalIgnoreCase))
        {
            return ManualImportResult.Invalid(
                "The uploaded file's contents do not match its file extension.");
        }

        if (await acquisitions.ExistsAssetWithChecksumForFormatAsync(
                requestFormatId, staged.Sha256, cancellationToken))
        {
            return ManualImportResult.DuplicateDetected();
        }

        var now = clock.UtcNow;
        var job = new AcquisitionJob(requestId, format.MediaType, ManualProviderId, EgressPolicy.Normal, now);
        var candidate = job.AddCandidate(
            ManualProviderId,
            providerReference: staged.StoredFilename,
            title: null,
            author: null,
            format: extension,
            sizeBytes: staged.SizeBytes,
            duration: null,
            bitrateKbps: null,
            metadataJson: null,
            confidenceScore: null,
            now);
        job.MarkCandidateStatus(candidate.Id, AcquisitionCandidateStatus.Acquired, now);
        job.TransitionTo(AcquisitionJobStatus.CandidateAcquired, now);

        var asset = new MediaAsset(
            request.WorkId,
            editionId: null,
            format.MediaType,
            extension,
            originalFilename,
            staged.StoredFilename,
            staged.SizeBytes,
            staged.Sha256,
            staged.DetectedMimeType,
            requestFormatId,
            candidate.Id,
            now);

        acquisitions.AddJob(job);
        acquisitions.AddAsset(asset);
        await acquisitions.SaveChangesAsync(cancellationToken);

        await audit.WriteAsync(
            AuditActions.ManualImportStaged,
            AuditSubjectTypes.MediaAsset,
            asset.Id.ToString(),
            new
            {
                RequestId = requestId,
                RequestFormatId = requestFormatId,
                WorkId = request.WorkId,
                format.MediaType,
                asset.SizeBytes,
                asset.Sha256
            },
            cancellationToken);

        return ManualImportResult.Success(job.Id, asset.Id);
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
