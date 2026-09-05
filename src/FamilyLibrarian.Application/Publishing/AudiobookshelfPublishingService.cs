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
    IBookRequestFulfillmentStore requestFulfillment,
    IAuditWriter audit,
    IClock clock,
    NotificationService notifications)
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

    /// <summary>
    /// Rechecks every Audiobookshelf delivery still awaiting catalog
    /// confirmation. Mirrors <see cref="CwaPublishingService.RecheckAwaitingVerificationAsync"/>
    /// -- this only performs API reads and, for a bundle, never re-uploads
    /// content that already succeeded.
    /// </summary>
    public async Task<int> RecheckAwaitingVerificationAsync(CancellationToken cancellationToken)
    {
        var deliveryIds = await repository.ListAwaitingVerificationIdsAsync(cancellationToken);
        foreach (var deliveryId in deliveryIds)
        {
            await RecheckAsync(deliveryId, cancellationToken);
        }

        return deliveryIds.Count;
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

        if (delivery.BundleId is { } bundleId)
        {
            var tracks = await assets.FindAssetsByBundleIdAsync(bundleId, cancellationToken);
            if (tracks.Count == 0)
            {
                delivery.MarkFailed("The source files no longer exist.", clock.UtcNow);
                await repository.SaveChangesAsync(cancellationToken);
                return true;
            }

            if (delivery.Status == DeliveryStatus.Failed)
            {
                delivery.ResetForRetry();
                await ExecuteBundlePublishAsync(tracks, delivery, cancellationToken);
            }
            else if (delivery.Status == DeliveryStatus.Verifying)
            {
                var bundleWork = await workLookup.FindAsync(tracks[0].WorkId, cancellationToken);
                await TryVerifyAsync(
                    delivery, tracks,
                    bundleWork?.Title ?? "Unknown title", bundleWork?.PrimaryAuthor, cancellationToken);
            }

            return true;
        }

        var asset = await assets.FindAssetAsync(delivery.AssetId!.Value, cancellationToken);
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
            await TryVerifyAsync(
                delivery, [asset],
                work?.Title ?? "Unknown title", work?.PrimaryAuthor, cancellationToken);
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
            var existing = await apiClient.FindExistingItemIdAsync(title, author, cancellationToken);
            if (existing.Decision == BookMatchDecision.Match)
            {
                delivery.MarkDelivered(existing.MatchedId!, clock.UtcNow);
                await MarkRequestFormatAvailableAsync(asset.AssociatedRequestFormatId, title, cancellationToken);
                ArchiveTrusted([asset]);
                await repository.SaveChangesAsync(cancellationToken);
                await AuditPublishedAsync(asset.Id, cancellationToken);
                await DeleteTrustedBytesAsync([asset], cancellationToken);
                return;
            }

            if (existing.Decision == BookMatchDecision.Ambiguous)
            {
                // Multiple library items match -- guessing one as "already
                // delivered" risks attaching the wrong edition, so this falls
                // through to a normal upload instead, same as NoMatch.
                await AuditMatchAmbiguousAsync(asset.Id, existing.Candidates, cancellationToken);
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

            // Close the read handle before any possible cleanup delete below --
            // safe to call again when the `await using` above disposes it a
            // second time at scope exit.
            await content.DisposeAsync();

            if (result.ExternalItemId is not null)
            {
                delivery.MarkDelivered(result.ExternalItemId, clock.UtcNow);
                await MarkRequestFormatAvailableAsync(asset.AssociatedRequestFormatId, title, cancellationToken);
                ArchiveTrusted([asset]);
            }
            else
            {
                delivery.MarkVerifying();
            }

            await repository.SaveChangesAsync(cancellationToken);
            await AuditPublishedAsync(asset.Id, cancellationToken);

            if (delivery.Status == DeliveryStatus.Verifying)
            {
                await TryVerifyAsync(delivery, [asset], title, author, cancellationToken);
            }
            else
            {
                await DeleteTrustedBytesAsync([asset], cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var reason = DescribeFailure(exception);
            delivery.MarkFailed(reason, clock.UtcNow);
            await repository.SaveChangesAsync(cancellationToken);
            await AuditPublishFailedAsync(asset.Id, reason, cancellationToken);
        }
    }

    /// <summary>
    /// Publishes every track of one multi-file acquisition (e.g. a chaptered
    /// Gutenberg audiobook) as a single Audiobookshelf upload, once all have
    /// reached Trusted. Only <see cref="ApprovalService"/> calls this, and
    /// only after confirming every sibling is Trusted.
    /// </summary>
    public async Task PublishBundleAsync(IReadOnlyList<MediaAsset> tracks, CancellationToken cancellationToken)
    {
        if (tracks.Count == 0)
        {
            return;
        }

        var settings = await settingsStore.FindAsync(cancellationToken);
        if (settings is null || !settings.IsEnabled)
        {
            return;
        }

        var bundleId = tracks[0].BundleId!.Value;
        var delivery = await repository.FindByBundleIdAsync(bundleId, cancellationToken);
        if (delivery is null)
        {
            delivery = Delivery.ForBundle(bundleId, clock.UtcNow);
            repository.Add(delivery);
        }

        await ExecuteBundlePublishAsync(tracks, delivery, cancellationToken);
    }

    private async Task ExecuteBundlePublishAsync(
        IReadOnlyList<MediaAsset> tracks, Delivery delivery, CancellationToken cancellationToken)
    {
        var work = await workLookup.FindAsync(tracks[0].WorkId, cancellationToken);
        var title = work?.Title ?? "Unknown title";
        var author = work?.PrimaryAuthor;
        var bundleId = tracks[0].BundleId!.Value;

        try
        {
            var existing = await apiClient.FindExistingItemIdAsync(title, author, cancellationToken);
            if (existing.Decision == BookMatchDecision.Match)
            {
                delivery.MarkDelivered(existing.MatchedId!, clock.UtcNow);
                await MarkRequestFormatAvailableAsync(tracks[0].AssociatedRequestFormatId, title, cancellationToken);
                ArchiveTrusted(tracks);
                await repository.SaveChangesAsync(cancellationToken);
                await AuditBundlePublishedAsync(bundleId, cancellationToken);
                await DeleteTrustedBytesAsync(tracks, cancellationToken);
                return;
            }

            if (existing.Decision == BookMatchDecision.Ambiguous)
            {
                await AuditMatchAmbiguousAsync(bundleId, existing.Candidates, cancellationToken);
            }

            var orderedTracks = tracks.OrderBy(track => track.BundleSequence).ToArray();
            var openStreams = new List<Stream>(orderedTracks.Length);
            var shouldDeleteAfterUpload = false;
            try
            {
                var uploadTracks = new List<(Stream Content, string Filename)>(orderedTracks.Length);
                foreach (var track in orderedTracks)
                {
                    var content = await stagingStore.OpenAsync(
                        MediaAssetStorageState.Trusted, track.StoredFilename, cancellationToken);
                    openStreams.Add(content);
                    var filename = PublishingFilenames.BuildBundleTrackFilename(
                        title, track.Format, track.BundleSequence ?? 1);
                    uploadTracks.Add((content, filename));
                }

                var result = await apiClient.UploadBundleAsync(uploadTracks, title, author, cancellationToken);
                if (!result.Succeeded)
                {
                    delivery.MarkFailed(result.Error ?? "The upload failed.", clock.UtcNow);
                    await repository.SaveChangesAsync(cancellationToken);
                    await AuditBundlePublishFailedAsync(bundleId, result.Error, cancellationToken);
                    return;
                }

                if (result.ExternalItemId is not null)
                {
                    delivery.MarkDelivered(result.ExternalItemId, clock.UtcNow);
                    await MarkRequestFormatAvailableAsync(tracks[0].AssociatedRequestFormatId, title, cancellationToken);
                    ArchiveTrusted(orderedTracks);
                }
                else
                {
                    delivery.MarkVerifying();
                }

                await repository.SaveChangesAsync(cancellationToken);
                await AuditBundlePublishedAsync(bundleId, cancellationToken);

                if (delivery.Status == DeliveryStatus.Verifying)
                {
                    await TryVerifyAsync(delivery, orderedTracks, title, author, cancellationToken);
                }
                else
                {
                    // Deferred past the stream-closing `finally` below -- the
                    // upload streams opened from the Trusted zone above must
                    // be closed before their backing files can be deleted.
                    shouldDeleteAfterUpload = true;
                }
            }
            finally
            {
                foreach (var stream in openStreams)
                {
                    await stream.DisposeAsync();
                }
            }

            if (shouldDeleteAfterUpload)
            {
                await DeleteTrustedBytesAsync(orderedTracks, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var reason = DescribeFailure(exception);
            delivery.MarkFailed(reason, clock.UtcNow);
            await repository.SaveChangesAsync(cancellationToken);
            await AuditBundlePublishFailedAsync(bundleId, reason, cancellationToken);
        }
    }

    /// <summary>
    /// .NET's <see cref="HttpContent"/> wraps a mid-upload stream failure in a
    /// generic "Error while copying content to a stream." message and puts the
    /// actual cause (e.g. a connection reset, or a reverse proxy's body-size
    /// limit rejecting a large multi-file request) on <see cref="Exception.InnerException"/>.
    /// Surfacing it here is the difference between an admin being able to
    /// diagnose a failed delivery and a dead end.
    /// </summary>
    private static string DescribeFailure(Exception exception) =>
        exception.InnerException is { } inner
            ? $"{exception.Message} ({inner.Message})"
            : exception.Message;

    private async Task TryVerifyAsync(
        Delivery delivery, IReadOnlyList<MediaAsset> assets, string title, string? author, CancellationToken cancellationToken)
    {
        try
        {
            var result = await apiClient.FindExistingItemIdAsync(title, author, cancellationToken);
            if (result.Decision == BookMatchDecision.Match)
            {
                delivery.MarkDelivered(result.MatchedId!, clock.UtcNow);
                await MarkRequestFormatAvailableAsync(assets[0].AssociatedRequestFormatId, title, cancellationToken);
                ArchiveTrusted(assets);
                await repository.SaveChangesAsync(cancellationToken);

                // Cleanup runs only after the Delivered/Archived state is
                // durably saved -- see DeleteTrustedBytesAsync.
                await DeleteTrustedBytesAsync(assets, cancellationToken);
            }
            else if (result.Decision == BookMatchDecision.Ambiguous)
            {
                // Left Verifying -- genuine ambiguity, not a bug to guess past.
                await AuditMatchAmbiguousAsync(assets[0].Id, result.Candidates, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Best-effort: leave Verifying for a later manual recheck.
        }
    }

    /// <summary>Moves every given asset from Trusted to Archived, in memory only -- the caller saves.</summary>
    private void ArchiveTrusted(IReadOnlyList<MediaAsset> assets)
    {
        foreach (var asset in assets)
        {
            asset.TransitionStorageState(MediaAssetStorageState.Archived, clock.UtcNow);
        }
    }

    /// <summary>
    /// Permanently removes each asset's local trusted copy once Audiobookshelf
    /// has confirmed delivery — docs/01 §13's "remove local media copy." Every
    /// asset is already Archived and saved by the time this runs, so a
    /// cleanup failure here is a leftover-file nuisance for an operator to
    /// notice, never a reason to roll back or retry an otherwise-successful
    /// publish.
    /// </summary>
    private async Task DeleteTrustedBytesAsync(IReadOnlyList<MediaAsset> assets, CancellationToken cancellationToken)
    {
        foreach (var asset in assets)
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
                    new { asset.Id, Destination = "audiobookshelf", Reason = exception.Message },
                    cancellationToken);
            }
        }
    }

    private async Task MarkRequestFormatAvailableAsync(
        Guid requestFormatId, string title, CancellationToken cancellationToken)
    {
        var request = await requestFulfillment.FindByFormatIdAsync(requestFormatId, cancellationToken);
        if (request is null)
        {
            return;
        }

        var becameAvailable = request.MarkFormatAvailable(requestFormatId, clock.UtcNow);
        if (becameAvailable)
        {
            await notifications.RecordRequestStatusForUserAsync(
                request.UserId, request.Id, title, RequestStatus.Available, cancellationToken);
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

    private Task AuditBundlePublishedAsync(Guid bundleId, CancellationToken cancellationToken) =>
        audit.WriteAsync(
            AuditActions.AssetPublished,
            AuditSubjectTypes.MediaAsset,
            bundleId.ToString(),
            new { BundleId = bundleId, Destination = "audiobookshelf" },
            cancellationToken);

    private Task AuditBundlePublishFailedAsync(Guid bundleId, string? reason, CancellationToken cancellationToken) =>
        audit.WriteAsync(
            AuditActions.AssetPublishFailed,
            AuditSubjectTypes.MediaAsset,
            bundleId.ToString(),
            new { BundleId = bundleId, Destination = "audiobookshelf", Reason = reason },
            cancellationToken);

    private Task AuditMatchAmbiguousAsync(
        Guid subjectId, IReadOnlyList<CandidateBook> candidates, CancellationToken cancellationToken) =>
        audit.WriteAsync(
            AuditActions.AssetMatchAmbiguous,
            AuditSubjectTypes.MediaAsset,
            subjectId.ToString(),
            new { SubjectId = subjectId, Destination = "audiobookshelf", Candidates = candidates },
            cancellationToken);
}
