using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Audit;
using FamilyLibrarian.Domain.Publishing;

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
    IClock clock)
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
            await TryVerifyAsync(import, asset, work?.Title ?? "Unknown title", work?.PrimaryAuthor, cancellationToken);
        }

        return true;
    }

    /// <summary>
    /// Rechecks every CWA handoff still awaiting catalog confirmation. This
    /// only performs OPDS reads; it never transports the source file again.
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

        try
        {
            var transport = transportFactory.Create(settings);
            await using var content = await stagingStore.OpenAsync(
                MediaAssetStorageState.Trusted, asset.StoredFilename, cancellationToken);

            var targetFilename = PublishingFilenames.BuildTargetFilename(title, asset.Format);
            await transport.WriteAsync(content, targetFilename, cancellationToken);

            import.MarkAwaitingVerification();
            await repository.SaveChangesAsync(cancellationToken);

            await audit.WriteAsync(
                AuditActions.AssetPublished,
                AuditSubjectTypes.MediaAsset,
                asset.Id.ToString(),
                new { asset.Id, Destination = "cwa" },
                cancellationToken);

            // One best-effort immediate check; CWA's ingest is asynchronous, so
            // "not found yet" is expected and left for a later manual recheck.
            await TryVerifyAsync(import, asset, title, author, cancellationToken);
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
        CancellationToken cancellationToken)
    {
        try
        {
            var bookId = await catalogClient.FindBookIdAsync(title, author, cancellationToken);
            if (bookId is not null)
            {
                import.MarkAvailable(bookId, clock.UtcNow);
                await MarkRequestFormatAvailableAsync(asset, cancellationToken);
                await repository.SaveChangesAsync(cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Verification is best-effort: a catalog-lookup hiccup leaves the
            // import AwaitingVerification for a later manual recheck rather
            // than failing an otherwise-successful handoff.
        }
    }

    private async Task MarkRequestFormatAvailableAsync(MediaAsset asset, CancellationToken cancellationToken)
    {
        var request = await requestFulfillment.FindByFormatIdAsync(
            asset.AssociatedRequestFormatId,
            cancellationToken);
        request?.MarkFormatAvailable(asset.AssociatedRequestFormatId, clock.UtcNow);
    }
}
