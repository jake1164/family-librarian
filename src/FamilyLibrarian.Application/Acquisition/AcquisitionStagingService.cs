using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Audit;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Application.Acquisition;

/// <summary>
/// The one trusted path by which any artifact — manually uploaded or fetched
/// by a bundled provider — enters Family Librarian-controlled staging.
/// </summary>
/// <remarks>
/// Extracted from what was originally <c>ManualImportService.ImportAsync</c>'s
/// full body; that type's own doc comment anticipated this before M11 shipped
/// a second caller: "every artifact ... enters staging through code shaped
/// like this." Nothing here ever hands the caller a browser-servable path,
/// and the resulting <see cref="MediaAsset"/> starts in
/// <see cref="MediaAssetStorageState.Quarantine"/> with no path to
/// <see cref="MediaAssetStorageState.Trusted"/> until an M10
/// <c>ApprovalService</c> decision moves it there. <see cref="IAcquisitionBoundaryGuard"/>
/// is checked before a single byte is staged: required-scanner unavailability
/// blocks the whole acquisition boundary, not merely later delivery/approval —
/// this applies identically to a legally-free automated fetch as to a manual
/// upload; there is no "it's free" exception to the security gate.
/// </remarks>
public sealed class AcquisitionStagingService(
    IAcquisitionRepository acquisitions,
    IAssetStagingStore stagingStore,
    IAcquisitionBoundaryGuard boundaryGuard,
    ManualImportPolicy policy,
    IAuditWriter audit,
    IClock clock)
{
    public async Task<ManualImportResult> StageAsync(
        BookRequest request,
        RequestFormat format,
        Stream content,
        string originalFilename,
        string providerId,
        string auditAction,
        string? candidateTitle,
        string? candidateAuthor,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(originalFilename);
        if (string.IsNullOrEmpty(extension) || !policy.IsExtensionAllowed(format.MediaType, extension))
        {
            return ManualImportResult.Invalid(DisallowedFileTypeMessage(extension, format.MediaType));
        }

        if (!await boundaryGuard.CanAcceptNewArtifactAsync(cancellationToken))
        {
            await audit.WriteAsync(
                AuditActions.ManualImportRejectedNoScanner,
                AuditSubjectTypes.BookRequest,
                request.Id.ToString(),
                new { RequestId = request.Id, RequestFormatId = format.Id },
                cancellationToken);
            return ManualImportResult.WaitingForSecurityScanner();
        }

        var (staged, error) = await WriteAndValidateContentAsync(
            format, content, originalFilename, extension, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var now = clock.UtcNow;
        var job = new AcquisitionJob(request.Id, format.MediaType, providerId, now);
        var candidate = AddAcquiredCandidate(job, providerId, staged!, candidateTitle, candidateAuthor, extension, now);
        job.TransitionTo(AcquisitionJobStatus.CandidateAcquired, now);

        var asset = new MediaAsset(
            request.WorkId,
            editionId: null,
            format.MediaType,
            extension,
            originalFilename,
            staged!.StoredFilename,
            staged.SizeBytes,
            staged.Sha256,
            staged.DetectedMimeType,
            format.Id,
            candidate.Id,
            now);

        acquisitions.AddJob(job);
        acquisitions.AddAsset(asset);
        await acquisitions.SaveChangesAsync(cancellationToken);

        await audit.WriteAsync(
            auditAction,
            AuditSubjectTypes.MediaAsset,
            asset.Id.ToString(),
            new
            {
                RequestId = request.Id,
                RequestFormatId = format.Id,
                WorkId = request.WorkId,
                format.MediaType,
                asset.SizeBytes,
                asset.Sha256,
                ProviderId = providerId
            },
            cancellationToken);

        return ManualImportResult.Success(job.Id, asset.Id);
    }

    /// <summary>
    /// Stages every track of a multi-file acquisition (e.g. a chaptered
    /// Gutenberg audiobook) as one <see cref="AcquisitionJob"/> and one
    /// shared bundle: sibling <see cref="MediaAsset"/> rows that a later
    /// approval only publishes once every one of them is trusted.
    /// </summary>
    /// <remarks>
    /// Fail-closed: if any single track fails validation, every
    /// already-quarantined sibling from this attempt is deleted and the
    /// whole bundle is rejected — a partial audiobook is never staged.
    /// </remarks>
    public async Task<ManualImportResult> StageBundleAsync(
        BookRequest request,
        RequestFormat format,
        IReadOnlyList<DirectAcquisitionFile> files,
        string providerId,
        string auditAction,
        string? candidateTitle,
        string? candidateAuthor,
        CancellationToken cancellationToken)
    {
        return await StageBundleCoreAsync(
            request, format, ToAsyncEnumerable(files), providerId, auditAction,
            candidateTitle, candidateAuthor, providerJob: null, cancellationToken);
    }

    public Task<ManualImportResult> StageExternalBundleAsync(
        BookRequest request,
        RequestFormat format,
        IAsyncEnumerable<DirectAcquisitionFile> files,
        string providerId,
        string auditAction,
        ProviderAcquisitionJob providerJob,
        CancellationToken cancellationToken) =>
        StageBundleCoreAsync(
            request, format, files, providerId, auditAction,
            candidateTitle: null, candidateAuthor: null, providerJob, cancellationToken);

    private async Task<ManualImportResult> StageBundleCoreAsync(
        BookRequest request,
        RequestFormat format,
        IAsyncEnumerable<DirectAcquisitionFile> files,
        string providerId,
        string auditAction,
        string? candidateTitle,
        string? candidateAuthor,
        ProviderAcquisitionJob? providerJob,
        CancellationToken cancellationToken)
    {
        if (!await boundaryGuard.CanAcceptNewArtifactAsync(cancellationToken))
        {
            await audit.WriteAsync(
                AuditActions.ManualImportRejectedNoScanner,
                AuditSubjectTypes.BookRequest,
                request.Id.ToString(),
                new { RequestId = request.Id, RequestFormatId = format.Id },
                cancellationToken);
            return ManualImportResult.WaitingForSecurityScanner();
        }

        var quarantined = new List<StagedFile>();
        var staged = new List<StagedFile>();
        var acceptedFiles = new List<DirectAcquisitionFile>();
        try
        {
            await foreach (var file in files.WithCancellation(cancellationToken))
            {
                await using (file.Content.ConfigureAwait(false))
                {
                    var extension = Path.GetExtension(file.Filename);
                    if (string.IsNullOrEmpty(extension) || !policy.IsExtensionAllowed(format.MediaType, extension))
                    {
                        await CleanUpAsync(quarantined, CancellationToken.None);
                        return ManualImportResult.Invalid(DisallowedFileTypeMessage(extension, format.MediaType));
                    }

                    var (stagedFile, error) = await WriteAndValidateContentAsync(
                        format, file.Content, file.Filename, extension, cancellationToken, file.MaxSizeBytes);
                    if (error is not null)
                    {
                        await CleanUpAsync(quarantined, CancellationToken.None);
                        return error;
                    }

                    quarantined.Add(stagedFile!);
                    if (file.ValidateAsync is not null)
                        await file.ValidateAsync(cancellationToken);
                    staged.Add(stagedFile!);
                    acceptedFiles.Add(file);
                }
            }
        }
        catch
        {
            await CleanUpAsync(quarantined, CancellationToken.None);
            throw;
        }

        if (staged.Count == 0)
        {
            await CleanUpAsync(quarantined, CancellationToken.None);
            return ManualImportResult.Invalid("The provider returned no audiobook tracks.");
        }

        var now = clock.UtcNow;
        var bundleId = staged.Count > 1 ? Guid.NewGuid() : (Guid?)null;
        var job = new AcquisitionJob(request.Id, format.MediaType, providerId, now);
        var assetIds = new List<Guid>(staged.Count);

        for (var index = 0; index < staged.Count; index++)
        {
            var extension = Path.GetExtension(acceptedFiles[index].Filename);
            var candidate = AddAcquiredCandidate(
                job, providerId, staged[index], candidateTitle, candidateAuthor, extension, now);

            var asset = new MediaAsset(
                request.WorkId,
                editionId: null,
                format.MediaType,
                extension,
                acceptedFiles[index].Filename,
                staged[index].StoredFilename,
                staged[index].SizeBytes,
                staged[index].Sha256,
                staged[index].DetectedMimeType,
                format.Id,
                candidate.Id,
                now,
                bundleId: bundleId,
                bundleSequence: bundleId is null ? null : index + 1,
                bundleTrackCount: bundleId is null ? null : staged.Count,
                // Default true (manual import, and StageBundleAsync's other
                // caller -- a built-in provider, which verifies title/author
                // itself before ever offering a candidate) is correct as-is;
                // only an external provider's job carries a real answer.
                identityPreConfirmed: providerJob?.IdentityPreConfirmed ?? true);

            acquisitions.AddAsset(asset);
            if (providerJob is not null && acceptedFiles[index].ExternalOutputId is { } outputId)
                providerJob.Outputs.Single(output => output.OutputId == outputId).LinkAsset(asset.Id);
            assetIds.Add(asset.Id);
        }

        job.TransitionTo(AcquisitionJobStatus.CandidateAcquired, now);
        acquisitions.AddJob(job);
        if (providerJob is not null)
            providerJob.SetLocalAcquisition(job.Id, now);
        await acquisitions.SaveChangesAsync(cancellationToken);

        await audit.WriteAsync(
            auditAction,
            AuditSubjectTypes.MediaAsset,
            (bundleId ?? job.Id).ToString(),
            new
            {
                RequestId = request.Id,
                RequestFormatId = format.Id,
                WorkId = request.WorkId,
                format.MediaType,
                BundleId = bundleId,
                AssetIds = assetIds,
                ProviderId = providerId
            },
            cancellationToken);

        return assetIds.Count == 1
            ? ManualImportResult.Success(job.Id, assetIds[0])
            : ManualImportResult.SuccessBundle(job.Id, assetIds);
    }

    private static async IAsyncEnumerable<DirectAcquisitionFile> ToAsyncEnumerable(
        IReadOnlyList<DirectAcquisitionFile> files)
    {
        foreach (var file in files)
            yield return file;
        await Task.CompletedTask;
    }

    /// <summary>
    /// Writes a file into quarantine and validates its declared type against
    /// its actual contents and against any prior upload for the same format,
    /// deleting the quarantined copy on either failure. Shared by
    /// <see cref="StageAsync"/> and <see cref="StageBundleAsync"/> — the
    /// extension allowlist check happens in each caller first, since a
    /// bundle attempt needs to fail before writing anything for an
    /// early-rejected track.
    /// </summary>
    private async Task<(StagedFile? Staged, ManualImportResult? Error)> WriteAndValidateContentAsync(
        RequestFormat format,
        Stream content,
        string originalFilename,
        string extension,
        CancellationToken cancellationToken,
        long? maximumSizeBytes = null)
    {
        StagedFile staged;
        try
        {
            staged = await stagingStore.WriteToQuarantineAsync(
                content,
                originalFilename,
                maximumSizeBytes ?? policy.MaxUploadSizeBytes,
                cancellationToken);
        }
        catch (AssetTooLargeException)
        {
            return (null, ManualImportResult.Invalid(
                $"The file exceeds the {(maximumSizeBytes ?? policy.MaxUploadSizeBytes)}-byte upload limit."));
        }

        // The file is already in quarantine at this point, which is safe: it is
        // untrusted storage with no browser access and no path to Trusted. If
        // it cannot become an asset (wrong type or duplicate), remove this
        // temporary file immediately rather than leaving an orphan behind.
        if (!KnownFormatContentTypes.ByExtension.TryGetValue(extension, out var expectedContentType) ||
            !string.Equals(staged.DetectedMimeType, expectedContentType, StringComparison.OrdinalIgnoreCase))
        {
            await stagingStore.DeleteAsync(
                MediaAssetStorageState.Quarantine, staged.StoredFilename, cancellationToken);
            return (null, ManualImportResult.Invalid(
                "The file's contents do not match its file extension."));
        }

        if (await acquisitions.ExistsAssetWithChecksumForFormatAsync(
                format.Id, staged.Sha256, cancellationToken))
        {
            await stagingStore.DeleteAsync(
                MediaAssetStorageState.Quarantine, staged.StoredFilename, cancellationToken);
            return (null, ManualImportResult.DuplicateDetected());
        }

        return (staged, null);
    }

    private static AcquisitionCandidate AddAcquiredCandidate(
        AcquisitionJob job,
        string providerId,
        StagedFile staged,
        string? candidateTitle,
        string? candidateAuthor,
        string extension,
        DateTimeOffset now)
    {
        var candidate = job.AddCandidate(
            providerId,
            providerReference: staged.StoredFilename,
            title: candidateTitle,
            author: candidateAuthor,
            format: extension,
            sizeBytes: staged.SizeBytes,
            duration: null,
            bitrateKbps: null,
            metadataJson: null,
            confidenceScore: null,
            now);
        job.MarkCandidateStatus(candidate.Id, AcquisitionCandidateStatus.Acquired, now);
        return candidate;
    }

    // An empty extension rendered as "'' is not an allowed file type", which
    // reads like a stray quote rather than "the name has no extension".
    private static string DisallowedFileTypeMessage(string extension, RequestMediaType mediaType) =>
        string.IsNullOrEmpty(extension)
            ? $"The file name has no extension, so it can't be accepted as {mediaType}."
            : $"'{extension}' is not an allowed file type for {mediaType}.";

    private async Task CleanUpAsync(IReadOnlyList<StagedFile> quarantined, CancellationToken cancellationToken)
    {
        foreach (var file in quarantined)
        {
            await stagingStore.DeleteAsync(MediaAssetStorageState.Quarantine, file.StoredFilename, cancellationToken);
        }
    }
}
