using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Notifications;
using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Audit;
using FamilyLibrarian.Domain.Publishing;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Application.Publishing;

/// <summary>
/// Publishes an approved ebook <c>MediaAsset</c> to CWA's watched ingest
/// folder and, best-effort, verifies it landed via the OPDS catalog.
/// </summary>
/// <remarks>
/// Synchronous handoff plus one immediate verification attempt. Anything not
/// confirmed immediately stays <see cref="LibraryImportStatus.AwaitingVerification"/>
/// for the host's read-only background verifier, while an administrator can
/// also call <see cref="RecheckAsync"/> from the Library Publishing page. A
/// publish failure here is always caught and recorded on the
/// <see cref="LibraryImport"/> row; it must never propagate out to the caller
/// (see <c>MediaAssetPublishingCoordinator</c>, which is the actual caller
/// from the approval flow).
/// </remarks>
public sealed class CwaPublishingService(
    ICwaSettingsStore settingsStore,
    ILibraryImportRepository repository,
    ISecurityEvaluationRepository assets,
    IAssetStagingStore stagingStore,
    ICwaIngestTransportFactory transportFactory,
    ICwaCatalogClient catalogClient,
    IBookRequestFulfillmentStore requestFulfillment,
    IWorkLookup workLookup,
    IAuditWriter audit,
    IClock clock,
    NotificationService notifications)
{
    public async Task PublishAsync(MediaAsset asset, CancellationToken cancellationToken)
    {
        var settings = await settingsStore.FindAsync(cancellationToken);
        if (settings is null || !settings.IsEnabled)
        {
            // Not configured: nothing to publish to, nothing to show either.
            return;
        }

        var import = await repository.FindByAssetIdAsync(asset.Id, cancellationToken);
        if (import is null)
        {
            import = new LibraryImport(asset.Id, clock.UtcNow);
            repository.Add(import);
        }

        await ExecutePublishAsync(asset, settings, import, cancellationToken);
    }

    /// <returns><c>false</c> when no matching import exists.</returns>
    public async Task<bool> RecheckAsync(Guid libraryImportId, CancellationToken cancellationToken)
    {
        var import = await repository.FindAsync(libraryImportId, cancellationToken);
        if (import is null)
        {
            return false;
        }

        var settings = await settingsStore.FindAsync(cancellationToken);
        if (settings is null || !settings.IsEnabled)
        {
            import.MarkFailed("CWA is no longer configured.", clock.UtcNow);
            await repository.SaveChangesAsync(cancellationToken);
            return true;
        }

        var asset = await assets.FindAssetAsync(import.AssetId, cancellationToken);
        if (asset is null)
        {
            import.MarkFailed("The source file no longer exists.", clock.UtcNow);
            await repository.SaveChangesAsync(cancellationToken);
            return true;
        }

        if (import.Status == LibraryImportStatus.Failed)
        {
            import.ResetForRetry();
            await ExecutePublishAsync(asset, settings, import, cancellationToken);
        }
        else if (import.Status == LibraryImportStatus.AwaitingVerification)
        {
            var work = await workLookup.FindAsync(asset.WorkId, cancellationToken);
            var title = work?.Title ?? "Unknown title";
            await TryVerifyAsync(import, asset, title, work?.PrimaryAuthor, work?.Isbn13s ?? [], cancellationToken);

            if (import.Status == LibraryImportStatus.AwaitingVerification)
            {
                // Not found yet -- give CWA's watcher another chance at the file
                // it may have missed entirely (stopped/restarting at handoff
                // time; inotify has no startup backfill scan). Best-effort, and
                // never re-transports content -- see TryTouchAsync.
                await TryTouchAsync(import, settings, cancellationToken);
            }
        }

        return true;
    }

    /// <summary>
    /// Rechecks every CWA handoff still awaiting catalog confirmation. This
    /// only performs OPDS reads and, when still unconfirmed, a same-content
    /// re-signal via <see cref="ICwaIngestTransport.TouchAsync"/> -- it never
    /// transports the source file's content again.
    /// </summary>
    public async Task<int> RecheckAwaitingVerificationAsync(CancellationToken cancellationToken)
    {
        var importIds = await repository.ListAwaitingVerificationIdsAsync(cancellationToken);
        foreach (var importId in importIds)
        {
            await RecheckAsync(importId, cancellationToken);
        }

        return importIds.Count;
    }

    private async Task ExecutePublishAsync(
        MediaAsset asset,
        CwaSettings settings,
        LibraryImport import,
        CancellationToken cancellationToken)
    {
        var work = await workLookup.FindAsync(asset.WorkId, cancellationToken);
        var title = work?.Title ?? "Unknown title";
        var author = work?.PrimaryAuthor;
        var isbn13Candidates = work?.Isbn13s ?? [];

        try
        {
            var transport = transportFactory.Create(settings);
            await using var content = await stagingStore.OpenAsync(
                MediaAssetStorageState.Trusted, asset.StoredFilename, cancellationToken);

            var targetFilename = PublishingFilenames.BuildTargetFilename(title, asset.Format);
            await transport.WriteAsync(content, targetFilename, cancellationToken);

            import.MarkAwaitingVerification(targetFilename);
            await repository.SaveChangesAsync(cancellationToken);

            await audit.WriteAsync(
                AuditActions.AssetPublished,
                AuditSubjectTypes.MediaAsset,
                asset.Id.ToString(),
                new { asset.Id, Destination = "cwa" },
                cancellationToken);

            // One best-effort immediate check; CWA's ingest is asynchronous, so
            // "not found yet" is expected and left for a later manual recheck.
            await TryVerifyAsync(import, asset, title, author, isbn13Candidates, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            import.MarkFailed(exception.Message, clock.UtcNow);
            await repository.SaveChangesAsync(cancellationToken);

            await audit.WriteAsync(
                AuditActions.AssetPublishFailed,
                AuditSubjectTypes.MediaAsset,
                asset.Id.ToString(),
                new { asset.Id, Destination = "cwa", Reason = exception.Message },
                cancellationToken);
        }
    }

    private async Task TryVerifyAsync(
        LibraryImport import,
        MediaAsset asset,
        string title,
        string? author,
        IReadOnlyList<string> isbn13Candidates,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await catalogClient.FindBookIdAsync(title, author, isbn13Candidates, cancellationToken);
            if (result.Decision == BookMatchDecision.Match)
            {
                import.MarkAvailable(result.MatchedId!, clock.UtcNow);
                await MarkRequestFormatAvailableAsync(asset, title, cancellationToken);
                asset.TransitionStorageState(MediaAssetStorageState.Archived, clock.UtcNow);
                await repository.SaveChangesAsync(cancellationToken);

                // Cleanup runs only after the Available/Archived state is
                // durably saved -- see DeleteTrustedBytesAsync.
                await DeleteTrustedBytesAsync(asset, cancellationToken);
            }
            else if (result.Decision == BookMatchDecision.Ambiguous)
            {
                // Left AwaitingVerification -- this is genuine ambiguity (e.g.
                // multiple catalog editions), not a bug to guess past. The
                // audit entry makes it diagnosable instead of silently stuck.
                await audit.WriteAsync(
                    AuditActions.AssetMatchAmbiguous,
                    AuditSubjectTypes.MediaAsset,
                    asset.Id.ToString(),
                    new { asset.Id, Destination = "cwa", Candidates = result.Candidates },
                    cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Verification is best-effort: a catalog-lookup hiccup leaves the
            // import AwaitingVerification for a later manual recheck rather
            // than failing an otherwise-successful handoff.
        }
    }

    /// <summary>
    /// Re-signals the already-delivered file to CWA's watcher, without
    /// re-transporting its content. See <see cref="ICwaIngestTransport.TouchAsync"/>
    /// for why: CWA's ingest watcher has no startup backfill scan, so a file
    /// delivered while CWA was stopped or mid-restart is otherwise invisible
    /// to it forever. Best-effort, matching <see cref="TryVerifyAsync"/> -- a
    /// touch failure must not fail an otherwise-pending recheck; the next
    /// cycle tries again.
    /// </summary>
    /// <remarks>
    /// Targets <see cref="LibraryImport.TargetFilename"/>, the name actually
    /// recorded at write time -- never <c>PublishingFilenames.BuildTargetFilename</c>
    /// called again here, which would mint a fresh random-suffixed name that
    /// does not exist at the destination and make every touch a silent no-op.
    /// </remarks>
    private async Task TryTouchAsync(LibraryImport import, CwaSettings settings, CancellationToken cancellationToken)
    {
        if (import.TargetFilename is null)
        {
            return;
        }

        try
        {
            var transport = transportFactory.Create(settings);
            await transport.TouchAsync(import.TargetFilename, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Best-effort: see the doc comment above.
        }
    }

    /// <summary>
    /// Permanently removes the local trusted copy once CWA has confirmed the
    /// import — docs/01 §13's "remove local media copy." The asset is already
    /// Archived and saved by the time this runs, so a cleanup failure here is
    /// a leftover-file nuisance for an operator to notice, never a reason to
    /// roll back or retry an otherwise-successful publish.
    /// </summary>
    private async Task DeleteTrustedBytesAsync(MediaAsset asset, CancellationToken cancellationToken)
    {
        try
        {
            await stagingStore.DeleteAsync(MediaAssetStorageState.Trusted, asset.StoredFilename, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await audit.WriteAsync(
                AuditActions.AssetArchiveCleanupFailed,
                AuditSubjectTypes.MediaAsset,
                asset.Id.ToString(),
                new { asset.Id, Destination = "cwa", Reason = exception.Message },
                cancellationToken);
        }
    }

    private async Task MarkRequestFormatAvailableAsync(
        MediaAsset asset, string title, CancellationToken cancellationToken)
    {
        var request = await requestFulfillment.FindByFormatIdAsync(
            asset.AssociatedRequestFormatId,
            cancellationToken);
        if (request is null)
        {
            return;
        }

        var previouslySatisfied = request.SatisfiedRequesterIds.ToHashSet();
        request.MarkFormatAvailable(asset.AssociatedRequestFormatId, clock.UtcNow);
        foreach (var requesterId in request.SatisfiedRequesterIds.Except(previouslySatisfied))
            await notifications.RecordRequestStatusForUserAsync(
                requesterId, request.Id, title, RequestStatus.Available, cancellationToken);
    }
}
