using FamilyLibrarian.Application.Matching;
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

        var result = await apiClient.FindExistingItemIdAsync(work.Title, work.PrimaryAuthor, cancellationToken);
        if (result.Decision != BookMatchDecision.Match)
        {
            return [];
        }

        var itemId = result.MatchedId!;

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
                ExternalActionUri: BuildDeepLink(settings.PublicUrl ?? settings.BaseUrl, itemId),
                ProviderData: null)
        ];
    }

    // settings.PublicUrl, when set, is what a family member's browser can
    // actually reach -- settings.BaseUrl is only guaranteed reachable by
    // Family Librarian's own backend (in a containerized deployment it is
    // routinely a Docker-internal hostname like http://abs:80).
    private static Uri? BuildDeepLink(string baseUrl, string itemId) =>
        Uri.TryCreate($"{baseUrl.TrimEnd('/')}/item/{itemId}", UriKind.Absolute, out var uri) ? uri : null;
}
