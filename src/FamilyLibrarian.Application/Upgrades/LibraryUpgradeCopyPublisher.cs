using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Notifications;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Audit;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Application.Upgrades;

/// <summary>The one question the publishing coordinator asks about an approved copy: is it the better copy of an upgrade in progress?</summary>
public interface IUpgradeCopyPublisher
{
    /// <returns>True when the files were an upgrade copy and have been handled, whether or not delivery succeeded.</returns>
    Task<bool> TryPublishAlongsideAsync(IReadOnlyList<MediaAsset> files, CancellationToken cancellationToken);
}

/// <summary>
/// Delivers the better copy of an upgrade next to the old one. Kept apart from <see cref="LibraryUpgradeService"/> because publishing
/// happens inside approval, which that service itself triggers when it fetches the copy.
/// </summary>
public sealed class LibraryUpgradeCopyPublisher(
    ILibraryUpgradeStore store,
    IAssetStagingStore staging,
    IAudiobookshelfApiClient abs,
    ICwaSettingsStore cwaSettings,
    ICwaIngestTransportFactory transports,
    IWorkLookup workLookup,
    NotificationService notifications,
    IAuditWriter audit,
    IClock clock) : IUpgradeCopyPublisher
{
    /// <summary>The name an upgrade copy is delivered under until it replaces the old one, so the two never merge or overwrite.</summary>
    public static string TemporaryTitle(string title, Guid upgradeId) => $"{title} (upgrade {upgradeId.ToString("N")[..8]})";

    /// <summary>
    /// Called when an approved copy is about to be published. If it is the better copy of an upgrade in progress it is delivered next to
    /// the old one instead, and the normal path (which would recognise the old copy and discard this one) is skipped.
    /// </summary>
    /// <returns>True when this was an upgrade copy and has been handled, whether or not delivery succeeded.</returns>
    public async Task<bool> TryPublishAlongsideAsync(IReadOnlyList<MediaAsset> files, CancellationToken cancellationToken)
    {
        if (files.Count == 0) return false;
        var upgrade = await store.FindOpenForFormatAsync(files[0].AssociatedRequestFormatId, cancellationToken);
        if (upgrade is not { State: LibraryUpgradeState.Acquiring } || files.Any(file => file.Id == upgrade.DeliveredAssetId)) return false;

        var work = await workLookup.FindAsync(files[0].WorkId, cancellationToken);
        var title = work?.Title ?? "Unknown title";
        try
        {
            if (files[0].MediaType == RequestMediaType.Ebook)
            {
                var settings = await cwaSettings.FindAsync(cancellationToken);
                if (settings is null || !settings.IsEnabled) throw new InvalidOperationException("CWA is not configured.");
                await using var content = await staging.OpenAsync(MediaAssetStorageState.Trusted, files[0].StoredFilename, cancellationToken);
                // CWA keeps a second book with the same title; it does not replace or merge.
                await transports.Create(settings).WriteAsync(content, PublishingFilenames.BuildTargetFilename(title, files[0].Format), cancellationToken);
            }
            else
            {
                var ordered = files.OrderBy(file => file.BundleSequence).ToArray();
                var streams = new List<Stream>(ordered.Length);
                try
                {
                    var tracks = new List<(Stream Content, string Filename)>(ordered.Length);
                    foreach (var file in ordered)
                    {
                        var stream = await staging.OpenAsync(MediaAssetStorageState.Trusted, file.StoredFilename, cancellationToken);
                        streams.Add(stream);
                        tracks.Add((stream, ordered.Length == 1
                            ? PublishingFilenames.BuildTargetFilename(title, file.Format)
                            : PublishingFilenames.BuildBundleTrackFilename(title, file.Format, file.BundleSequence ?? 1)));
                    }

                    // Under its own title so it becomes its own item: the same title would merge with, or overwrite, the old copy.
                    var uploaded = await abs.UploadBundleAsync(tracks, TemporaryTitle(title, upgrade.Id), work?.PrimaryAuthor, cancellationToken);
                    if (!uploaded.Succeeded) throw new InvalidOperationException(uploaded.Error ?? "The upload failed.");
                }
                finally
                {
                    foreach (var stream in streams) await stream.DisposeAsync();
                }
            }

            upgrade.MarkPublishing(files[0].Id, clock.UtcNow);
            await store.SaveAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await FailAsync(upgrade, "Delivering the better copy failed: " + exception.Message, title, cancellationToken);
        }

        return true;
    }


    private async Task FailAsync(LibraryUpgrade upgrade, string reason, string title, CancellationToken cancellationToken)
    {
        upgrade.MarkFailed(reason, clock.UtcNow);
        await store.SaveAsync(cancellationToken);
        await audit.WriteSystemAsync(AuditActions.LibraryUpgradeFailed, AuditSubjectTypes.BookRequest, upgrade.RequestId.ToString(),
            new { UpgradeId = upgrade.Id, upgrade.RequestFormatId, Reason = reason }, cancellationToken);
        await notifications.RecordUpgradeStoppedAsync(upgrade.RequestId, title, reason, cancellationToken);
    }
}
