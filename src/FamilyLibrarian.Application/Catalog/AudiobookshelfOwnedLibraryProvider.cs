using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Application.Catalog;

/// <summary>
/// Reports whether a Work's audiobook is already present in the configured
/// Audiobookshelf library, reusing the same item-search lookup
/// <c>AudiobookshelfPublishingService</c> uses to avoid a duplicate upload.
/// </summary>
/// <remarks>Read-only — see <see cref="CwaOwnedLibraryProvider"/> for the shared rationale.</remarks>
public sealed class AudiobookshelfOwnedLibraryProvider(
    IAudiobookshelfSettingsStore settingsStore,
    IAudiobookshelfApiClient apiClient,
    IWorkLookup workLookup) : IOwnedLibraryProvider
{
    public string Id => "audiobookshelf";

    public async Task<IReadOnlyList<FulfillmentOption>> FindOwnedMatchesAsync(
        Guid workId, RequestMediaType mediaType, CancellationToken cancellationToken)
    {
        if (mediaType != RequestMediaType.Audiobook)
        {
            return [];
        }

        var settings = await settingsStore.FindAsync(cancellationToken);
        if (settings is null || !settings.IsEnabled || string.IsNullOrWhiteSpace(settings.BaseUrl))
        {
            return [];
        }

        var work = await workLookup.FindAsync(workId, cancellationToken);
        if (work is null)
        {
            return [];
        }

        var itemId = await apiClient.FindExistingItemIdAsync(work.Title, work.PrimaryAuthor, cancellationToken);
        if (itemId is null)
        {
            return [];
        }

        return
        [
            new FulfillmentOption(
                ProviderId: Id,
                ProviderResultId: itemId,
                WorkId: workId,
                EditionId: null,
                MediaType: RequestMediaType.Audiobook,
                OptionKind: OptionKind.Owned,
                AcquisitionMethod: AcquisitionMethod.OwnedImport,
                Format: null,
                Language: null,
                Quality: null,
                Availability: null,
                Cost: null,
                Currency: null,
                LicenseOrUsageStatus: null,
                DrmStatus: null,
                ExternalActionUri: BuildDeepLink(settings.BaseUrl, itemId),
                ProviderData: null)
        ];
    }

    private static Uri? BuildDeepLink(string baseUrl, string itemId) =>
        Uri.TryCreate($"{baseUrl.TrimEnd('/')}/item/{itemId}", UriKind.Absolute, out var uri) ? uri : null;
}
