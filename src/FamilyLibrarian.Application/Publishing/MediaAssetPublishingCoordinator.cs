using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Audit;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Application.Publishing;

/// <summary>
/// The single new dependency <c>ApprovalService</c> takes on: dispatches a
/// newly Trusted asset to the right publishing destination by media type.
/// </summary>
/// <remarks>
/// A publish failure must never fail the approval that triggered it — the
/// asset is trusted either way, and publishing is a separate, retriable
/// concern. <see cref="CwaPublishingService"/>/<see cref="AudiobookshelfPublishingService"/>
/// already catch their own expected failure modes and record them on a
/// <c>LibraryImport</c>/<c>Delivery</c> row; the try/catch here is only the
/// final safety net for something unexpected happening before either could
/// even do that.
/// </remarks>
public sealed class MediaAssetPublishingCoordinator(
    CwaPublishingService cwaPublishing,
    AudiobookshelfPublishingService audiobookshelfPublishing,
    IAuditWriter audit)
{
    public async Task PublishAsync(MediaAsset asset, CancellationToken cancellationToken)
    {
        try
        {
            if (asset.MediaType == RequestMediaType.Ebook)
            {
                await cwaPublishing.PublishAsync(asset, cancellationToken);
            }
            else
            {
                await audiobookshelfPublishing.PublishAsync(asset, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await audit.WriteAsync(
                AuditActions.AssetPublishFailed,
                AuditSubjectTypes.MediaAsset,
                asset.Id.ToString(),
                new { asset.Id, Reason = exception.Message },
                cancellationToken);
        }
    }

    /// <summary>
    /// Publishes every track of one multi-file acquisition together, once
    /// all have reached Trusted. Bundles are audiobook-only — CWA/ebooks
    /// never span more than one file — so this always goes to Audiobookshelf.
    /// </summary>
    public async Task PublishBundleAsync(IReadOnlyList<MediaAsset> tracks, CancellationToken cancellationToken)
    {
        try
        {
            await audiobookshelfPublishing.PublishBundleAsync(tracks, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var bundleId = tracks.Count > 0 ? tracks[0].BundleId : null;
            await audit.WriteAsync(
                AuditActions.AssetPublishFailed,
                AuditSubjectTypes.MediaAsset,
                bundleId?.ToString() ?? "unknown",
                new { BundleId = bundleId, Reason = exception.Message },
                cancellationToken);
        }
    }
}
