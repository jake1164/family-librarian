using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Providers;

namespace FamilyLibrarian.Infrastructure.Acquisition;

/// <summary>
/// Free, unauthenticated direct-acquisition provider over Gutendex
/// (<c>https://gutendex.com</c>), a documented JSON API over Project
/// Gutenberg's public-domain catalog.
/// </summary>
/// <remarks>
/// Needs no admin-configured settings — no credential, no configurable base
/// address — so it fits the existing <see cref="IProviderRegistry"/>/
/// <see cref="IProviderSettingsStore"/> model exactly like keyless Open
/// Library, rather than needing its own settings entity the way CWA/
/// Audiobookshelf did. Gated on <see cref="ProviderState.IsUsable"/> so an
    /// administrator's enable/disable toggle on the Sources page actually
/// controls whether this ever makes an outbound request.
/// </remarks>
public sealed class GutendexProvider(
    HttpClient httpClient,
    IProviderRegistry registry,
    IProviderSettingsStore settingsStore,
    IWorkLookup workLookup) : IDirectAcquisitionProvider
{
    public string Id => ProviderRegistry.GutendexProviderId;

    public async Task<IReadOnlyList<FulfillmentOption>> FindDirectAcquisitionsAsync(
        Guid workId, RequestMediaType mediaType, CancellationToken cancellationToken)
    {
        if (mediaType != RequestMediaType.Ebook)
        {
            return [];
        }

        var descriptor = registry.Find(Id);
        if (descriptor is null)
        {
            return [];
        }

        var setting = await settingsStore.FindAsync(Id, cancellationToken);
        if (!ProviderState.IsUsable(descriptor, setting))
        {
            return [];
        }

        var work = await workLookup.FindAsync(workId, cancellationToken);
        if (work is null || string.IsNullOrWhiteSpace(work.Title))
        {
            return [];
        }

        var query = string.IsNullOrWhiteSpace(work.PrimaryAuthor)
            ? work.Title
            : $"{work.Title} {work.PrimaryAuthor}";

        JsonNode? root;
        try
        {
            using var response = await httpClient.GetAsync(
                $"books?search={Uri.EscapeDataString(query)}", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            root = JsonNode.Parse(body);
        }
        catch (HttpRequestException)
        {
            return [];
        }
        catch (JsonException)
        {
            return [];
        }

        var results = root?["results"]?.AsArray();
        if (results is null)
        {
            return [];
        }

        foreach (var result in results)
        {
            var title = result?["title"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(title) ||
                !title.Contains(work.Title, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var epubUrl = result?["formats"]?["application/epub+zip"]?.GetValue<string>();
            var idNode = result?["id"];
            if (string.IsNullOrWhiteSpace(epubUrl) || idNode is null)
            {
                continue;
            }

            var bookId = idNode.GetValue<int>().ToString(CultureInfo.InvariantCulture);

            return
            [
                new FulfillmentOption(
                    ProviderId: Id,
                    ProviderResultId: bookId,
                    WorkId: workId,
                    EditionId: null,
                    MediaType: RequestMediaType.Ebook,
                    OptionKind: OptionKind.DirectAcquisition,
                    AcquisitionMethod: AcquisitionMethod.DirectDownload,
                    Format: "epub",
                    Language: null,
                    Quality: null,
                    Availability: null,
                    Cost: 0m,
                    Currency: null,
                    LicenseOrUsageStatus: "Public domain",
                    DrmStatus: null,
                    ExternalActionUri: null,
                    ProviderData: epubUrl)
            ];
        }

        return [];
    }

    public async Task<DirectAcquisitionFile> FetchAsync(
        FulfillmentOption fulfillmentOption, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fulfillmentOption);

        var url = fulfillmentOption.ProviderData
            ?? throw new InvalidOperationException("This Gutendex option has no download URL.");

        var response = await httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        return new DirectAcquisitionFile(stream, $"gutenberg-{fulfillmentOption.ProviderResultId}.epub");
    }
}
