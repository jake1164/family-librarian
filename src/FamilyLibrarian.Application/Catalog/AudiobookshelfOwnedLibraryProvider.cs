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

        var identity = new BookIdentity(work.Title, work.PrimaryAuthor, work.Isbn13s);
        var options = await MatchAsync(identity, settings, cancellationToken);
        return options.Select(option => option with { WorkId = workId }).ToArray();
    }

    public async Task<IReadOnlyList<FulfillmentOption>> FindOwnedMatchesAsync(
        BookIdentity identity, RequestMediaType mediaType, CancellationToken cancellationToken)
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

        return await MatchAsync(identity, settings, cancellationToken);
    }

    private async Task<IReadOnlyList<FulfillmentOption>> MatchAsync(
        BookIdentity identity, Domain.Publishing.AudiobookshelfSettings settings, CancellationToken cancellationToken)
    {
        var result = await apiClient.FindExistingItemIdAsync(identity.Title, identity.Author, cancellationToken);
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
                WorkId: Guid.Empty,
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
                ExternalActionUri: ExternalLibraryLinks.BuildAudiobookshelfItemLink(settings, itemId),
                ProviderData: null)
        ];
    }
}
