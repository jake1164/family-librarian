using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Delivery;
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
    NotificationService notifications,
    DeliveryAttemptService deliveryAttempts,
    Acquisition.FulfilledFormatDownloadCancellation? remainingDownloads = null)
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
            var lookup = await ResolveCatalogLookupAsync(asset, work, cancellationToken);
            await TryVerifyAsync(import, asset, title, lookup, cancellationToken);

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
        // What to look for in CWA. Usually the requested book; the book itself for everything shown to people.
        var lookup = await ResolveCatalogLookupAsync(asset, work, cancellationToken);

        // Duplicate guard: a book CWA already holds (an earlier copy for this request, or one added by hand) is not
        // sent again. The existing entry stands in for this copy exactly as a verified import would: the format
        // becomes available against it and this file's local bytes are removed. Two copies of one ebook from two
        // sources reached the lab library before this.
        if (await FindExistingBookIdAsync(asset, lookup, cancellationToken) is { } existingBookId)
        {
            await audit.WriteAsync(
                AuditActions.AssetPublishSkippedDuplicate,
                AuditSubjectTypes.MediaAsset,
                asset.Id.ToString(),
                new { asset.Id, Destination = "cwa", ExistingBookId = existingBookId },
                cancellationToken);
            await CompleteAsAvailableAsync(import, asset, existingBookId, title, cancellationToken);
            return;
        }

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
            await TryVerifyAsync(import, asset, title, lookup, cancellationToken);
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

    /// <summary>
    /// The CWA book already matching this asset's Work, or null when there is none or the lookup failed. A failed
    /// lookup never blocks publishing: at worst a duplicate is sent, which is what happened before this check.
    /// </summary>
    private async Task<string?> FindExistingBookIdAsync(
        MediaAsset asset, CatalogLookup lookup, CancellationToken cancellationToken)
    {
        try
        {
            var acceptedLanguage = await FindAcceptedLanguageAsync(asset.AssociatedRequestFormatId, cancellationToken);
            var result = await catalogClient.FindBookIdAsync(
                lookup.Title, lookup.Author, lookup.Isbn13s, cancellationToken, acceptedLanguage);
            return result.Decision == BookMatchDecision.Match ? result.MatchedId : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>The CWA entry is confirmed: the format is available against it and the local copy is archived.</summary>
    private async Task CompleteAsAvailableAsync(
        LibraryImport import, MediaAsset asset, string externalBookId, string title, CancellationToken cancellationToken)
    {
        import.MarkAvailable(externalBookId, clock.UtcNow);
        await MarkRequestFormatAvailableAsync(asset, import, title, cancellationToken);
        asset.TransitionStorageState(MediaAssetStorageState.Archived, clock.UtcNow);
        await repository.SaveChangesAsync(cancellationToken);
        await CancelRemainingDownloadsAsync(asset.AssociatedRequestFormatId, [asset.Id], cancellationToken);

        // Cleanup runs only after the Available/Archived state is
        // durably saved -- see DeleteTrustedBytesAsync.
        await DeleteTrustedBytesAsync(asset, cancellationToken);
    }

    private async Task TryVerifyAsync(
        LibraryImport import,
        MediaAsset asset,
        string title,
        CatalogLookup lookup,
        CancellationToken cancellationToken)
    {
        try
        {
            var acceptedLanguage = await FindAcceptedLanguageAsync(asset.AssociatedRequestFormatId, cancellationToken);
            var result = await catalogClient.FindBookIdAsync(
                lookup.Title, lookup.Author, lookup.Isbn13s, cancellationToken, acceptedLanguage);
            if (result.Decision == BookMatchDecision.NoMatch && asset.StorageState == MediaAssetStorageState.Trusted)
            {
                result = await catalogClient.FindImportedFileAsync(
                    lookup.Title, asset.Format, asset.SizeBytes, asset.Sha256, cancellationToken, acceptedLanguage);
            }
            if (result.Decision == BookMatchDecision.Match)
            {
                await CompleteAsAvailableAsync(import, asset, result.MatchedId!, title, cancellationToken);
            }
            else if (result.Decision is BookMatchDecision.Ambiguous or BookMatchDecision.LanguageExcluded)
            {
                // Left AwaitingVerification -- this is genuine ambiguity (e.g.
                // multiple catalog editions, or a same-title/author entry
                // excluded for language) not a bug to guess past. The
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

    /// <summary>
    /// The language the requester already explicitly accepted for this
    /// specific format, if any. Verification must match this language after
    /// alias normalization; null retains the English-or-unspecified default.
    /// </summary>
    private async Task<string?> FindAcceptedLanguageAsync(Guid requestFormatId, CancellationToken cancellationToken)
    {
        var request = await requestFulfillment.FindByFormatIdAsync(requestFormatId, cancellationToken);
        return request?.Formats.SingleOrDefault(format => format.Id == requestFormatId)?.AcceptedLanguage;
    }

    /// <summary>The title, author and ISBNs used to find a delivered book in CWA's catalog.</summary>
    private sealed record CatalogLookup(string Title, string? Author, IReadOnlyList<string> Isbn13s);

    /// <summary>
    /// What identifies this delivery in CWA. Normally the requested work. When the requester accepted a collection that
    /// contains the work ("Take it"), CWA files the book under the collection's own title and it carries none of the work's
    /// ISBNs, so looking for the work finds nothing and the delivery would wait for verification for ever. The format
    /// records the accepted collection's title as its accepted variant; look for that, still requiring the author to
    /// agree. Any other accepted variant (abridged, other language, parts) is the work itself and is looked up as before.
    /// </summary>
    private async Task<CatalogLookup> ResolveCatalogLookupAsync(
        MediaAsset asset, WorkSummary? work, CancellationToken cancellationToken)
    {
        var lookup = new CatalogLookup(work?.Title ?? "Unknown title", work?.PrimaryAuthor, work?.Isbn13s ?? []);
        var request = await requestFulfillment.FindByFormatIdAsync(asset.AssociatedRequestFormatId, cancellationToken);
        var format = request?.Formats.SingleOrDefault(candidate => candidate.Id == asset.AssociatedRequestFormatId);
        return format is { AcceptedVariantKind: AcquisitionQuestionKind.CollectionContaining, AcceptedVariantDetail: { Length: > 0 } collectionTitle }
            ? lookup with { Title = collectionTitle, Isbn13s = [] }
            : lookup;
    }

    private async Task MarkRequestFormatAvailableAsync(
        MediaAsset asset, LibraryImport import, string title, CancellationToken cancellationToken)
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

        var availableFormat = request.Formats.SingleOrDefault(candidate => candidate.Id == asset.AssociatedRequestFormatId);
        if (availableFormat?.MediaType == RequestMediaType.Ebook && import.ExternalBookId is { } externalBookId)
        {
            await TryReleaseDeliveryAsync(request, externalBookId, asset.Format, title, cancellationToken);
        }
    }

    /// <summary>The format is in the library: stop any other provider still downloading it. Never fails the publish.</summary>
    private async Task CancelRemainingDownloadsAsync(
        Guid requestFormatId, IReadOnlyCollection<Guid> deliveredAssetIds, CancellationToken cancellationToken)
    {
        if (remainingDownloads is null)
        {
            return;
        }

        try
        {
            await remainingDownloads.CancelRemainingAsync(requestFormatId, deliveredAssetIds, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Best effort: a copy that still arrives is caught by the duplicate check.
        }
    }

    /// <summary>
    /// Releases any pending Kindle deliveries for this request's ebook format --
    /// see <see cref="DeliveryAttemptService.ReleaseForRequestFormatAsync"/>.
    /// Best-effort, matching <see cref="TryVerifyAsync"/>: a delivery-release
    /// failure must never take down an otherwise successful publish/verify pass.
    /// </summary>
    private async Task TryReleaseDeliveryAsync(
        BookRequest request, string externalBookId, string bookFormat, string title, CancellationToken cancellationToken)
    {
        try
        {
            await deliveryAttempts.ReleaseForRequestFormatAsync(
                request, externalBookId, bookFormat, clock.UtcNow, cancellationToken, title);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Recovery reconciles the saved Available import with unreleased
            // recipient intent, including when no attempt was created here.
            await audit.WriteAsync(
                AuditActions.DeliveryAttemptFailed,
                AuditSubjectTypes.BookRequest,
                request.Id.ToString(),
                new { request.Id, Reason = exception.Message },
                cancellationToken);
        }
    }
}
