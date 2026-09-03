using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Application.Catalog;

/// <summary>
/// Reports whether a Work's ebook is already present in the configured CWA
/// library, reusing the same OPDS lookup <c>CwaPublishingService</c> uses to
/// verify a just-published file landed.
/// </summary>
/// <remarks>
/// Read-only: this never triggers a publish, never mutates anything. "Not
/// found" (wrong media type, CWA not configured/enabled, or no catalog
/// match) is always an empty list, never an error — matching the rest of
/// this pipeline's "not found is a normal outcome" posture.
/// </remarks>
public sealed class CwaOwnedLibraryProvider(
    ICwaSettingsStore settingsStore,
    ICwaCatalogClient catalogClient,
    IWorkLookup workLookup) : IOwnedLibraryProvider
{
    public string Id => "cwa";

    public async Task<IReadOnlyList<FulfillmentOption>> FindOwnedMatchesAsync(
        Guid workId, RequestMediaType mediaType, CancellationToken cancellationToken)
    {
        if (mediaType != RequestMediaType.Ebook)
        {
            return [];
        }

        var settings = await settingsStore.FindAsync(cancellationToken);
        if (settings is null || !settings.IsEnabled || string.IsNullOrWhiteSpace(settings.OpdsBaseUrl))
        {
            return [];
        }

        var work = await workLookup.FindAsync(workId, cancellationToken);
        if (work is null)
        {
            return [];
        }

        var result = await catalogClient.FindBookIdAsync(work.Title, work.PrimaryAuthor, work.Isbn13s, cancellationToken);
        if (result.Decision != BookMatchDecision.Match)
        {
            return [];
        }

        var bookId = result.MatchedId!;

        return
        [
            new FulfillmentOption(
                ProviderId: Id,
                ProviderResultId: bookId,
                WorkId: workId,
                EditionId: null,
                MediaType: RequestMediaType.Ebook,
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
                ExternalActionUri: BuildDeepLink(settings.OpdsBaseUrl, bookId),
                ProviderData: null)
        ];
    }

    private static Uri? BuildDeepLink(string opdsBaseUrl, string bookId) =>
        Uri.TryCreate($"{opdsBaseUrl.TrimEnd('/')}/book/{bookId}", UriKind.Absolute, out var uri) ? uri : null;
}
