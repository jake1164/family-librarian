using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Audit;
using FamilyLibrarian.Domain.Publishing;

namespace FamilyLibrarian.Application.Publishing;

/// <summary>
/// Delivers an approved audiobook <c>MediaAsset</c> to Audiobookshelf via its
/// upload API.
/// </summary>
/// <remarks>
/// Searches for an existing matching item before uploading, so a Recheck
/// after a partial failure (e.g. the upload succeeded server-side but the
/// response was lost) never creates a duplicate — mirrors how third-party
/// Audiobookshelf integrations behave. Same synchronous/no-polling posture as
/// <see cref="CwaPublishingService"/>.
/// </remarks>
public sealed class AudiobookshelfPublishingService(
    IAudiobookshelfSettingsStore settingsStore,
    IDeliveryRepository repository,
    ISecurityEvaluationRepository assets,
    IAssetStagingStore stagingStore,
    IAudiobookshelfApiClient apiClient,
    IWorkLookup workLookup,
    IAuditWriter audit,
    IClock clock)
{
    public async Task PublishAsync(MediaAsset asset, CancellationToken cancellationToken)
    {
        var settings = await settingsStore.FindAsync(cancellationToken);
        if (settings is null || !settings.IsEnabled)
        {
            return;
        }

        var delivery = await repository.FindByAssetIdAsync(asset.Id, cancellationToken);
        if (delivery is null)
        {
            delivery = new Delivery(asset.Id, clock.UtcNow);
            repository.Add(delivery);
        }

        await ExecutePublishAsync(asset, delivery, cancellationToken);
    }

    /// <returns><c>false</c> when no matching delivery exists.</returns>
    public async Task<bool> RecheckAsync(Guid deliveryId, CancellationToken cancellationToken)
    {
        var delivery = await repository.FindAsync(deliveryId, cancellationToken);
        if (delivery is null)
        {
            return false;
        }

        var settings = await settingsStore.FindAsync(cancellationToken);
        if (settings is null || !settings.IsEnabled)
        {
            delivery.MarkFailed("Audiobookshelf is no longer configured.", clock.UtcNow);
            await repository.SaveChangesAsync(cancellationToken);
            return true;
        }

        var asset = await assets.FindAssetAsync(delivery.AssetId, cancellationToken);
        if (asset is null)
        {
            delivery.MarkFailed("The source file no longer exists.", clock.UtcNow);
            await repository.SaveChangesAsync(cancellationToken);
            return true;
        }

        if (delivery.Status == DeliveryStatus.Failed)
        {
            delivery.ResetForRetry();
            await ExecutePublishAsync(asset, delivery, cancellationToken);
        }
        else if (delivery.Status == DeliveryStatus.Verifying)
        {
            var work = await workLookup.FindAsync(asset.WorkId, cancellationToken);
            await TryVerifyAsync(delivery, work?.Title ?? "Unknown title", work?.PrimaryAuthor, cancellationToken);
        }

        return true;
    }

    private async Task ExecutePublishAsync(MediaAsset asset, Delivery delivery, CancellationToken cancellationToken)
    {
        var work = await workLookup.FindAsync(asset.WorkId, cancellationToken);
        var title = work?.Title ?? "Unknown title";
        var author = work?.PrimaryAuthor;

        try
        {
            var existingItemId = await apiClient.FindExistingItemIdAsync(title, author, cancellationToken);
            if (existingItemId is not null)
            {
                delivery.MarkDelivered(existingItemId, clock.UtcNow);
                await repository.SaveChangesAsync(cancellationToken);
                await AuditPublishedAsync(asset.Id, cancellationToken);
                return;
            }

            await using var content = await stagingStore.OpenAsync(
                MediaAssetStorageState.Trusted, asset.StoredFilename, cancellationToken);
            var filename = PublishingFilenames.BuildTargetFilename(title, asset.Format);

            var result = await apiClient.UploadAsync(content, filename, title, author, cancellationToken);
            if (!result.Succeeded)
            {
                delivery.MarkFailed(result.Error ?? "The upload failed.", clock.UtcNow);
                await repository.SaveChangesAsync(cancellationToken);
                await AuditPublishFailedAsync(asset.Id, result.Error, cancellationToken);
                return;
            }

            if (result.ExternalItemId is not null)
            {
                delivery.MarkDelivered(result.ExternalItemId, clock.UtcNow);
            }
            else
            {
                delivery.MarkVerifying();
            }

            await repository.SaveChangesAsync(cancellationToken);
            await AuditPublishedAsync(asset.Id, cancellationToken);

            if (delivery.Status == DeliveryStatus.Verifying)
            {
                await TryVerifyAsync(delivery, title, author, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            delivery.MarkFailed(exception.Message, clock.UtcNow);
            await repository.SaveChangesAsync(cancellationToken);
            await AuditPublishFailedAsync(asset.Id, exception.Message, cancellationToken);
        }
    }

    private async Task TryVerifyAsync(Delivery delivery, string title, string? author, CancellationToken cancellationToken)
    {
        try
        {
            var itemId = await apiClient.FindExistingItemIdAsync(title, author, cancellationToken);
            if (itemId is not null)
            {
                delivery.MarkDelivered(itemId, clock.UtcNow);
                await repository.SaveChangesAsync(cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Best-effort: leave Verifying for a later manual recheck.
        }
    }

    private Task AuditPublishedAsync(Guid assetId, CancellationToken cancellationToken) =>
        audit.WriteAsync(
            AuditActions.AssetPublished,
            AuditSubjectTypes.MediaAsset,
            assetId.ToString(),
            new { AssetId = assetId, Destination = "audiobookshelf" },
            cancellationToken);

    private Task AuditPublishFailedAsync(Guid assetId, string? reason, CancellationToken cancellationToken) =>
        audit.WriteAsync(
            AuditActions.AssetPublishFailed,
            AuditSubjectTypes.MediaAsset,
            assetId.ToString(),
            new { AssetId = assetId, Destination = "audiobookshelf", Reason = reason },
            cancellationToken);
}
