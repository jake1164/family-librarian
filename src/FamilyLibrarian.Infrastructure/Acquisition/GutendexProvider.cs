using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FamilyLibrarian.Application.Acquisition;
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
/// <para>
/// Audiobook discovery still goes through Gutendex (title/author search,
/// <c>media_type == "Sound"</c>), but Gutendex's <c>formats</c> dictionary
/// collapses same-mimetype files — a chaptered audiobook's several
/// <c>audio/mpeg</c> chapters would collapse to one URL there — so the
/// actual track list comes from <see cref="IGutenbergAudiobookCatalog"/>,
/// which reads Gutenberg's own per-book RDF record instead.
/// </para>
/// </remarks>
public sealed class GutendexProvider(
    HttpClient httpClient,
    IProviderRegistry registry,
    IProviderSettingsStore settingsStore,
    IWorkLookup workLookup,
    IGutenbergAudiobookCatalog gutenbergCatalog,
    ManualImportPolicy importPolicy) : IAutomaticDirectAcquisitionProvider
{
    public string Id => ProviderRegistry.GutendexProviderId;

    public async Task<IReadOnlyList<FulfillmentOption>> FindDirectAcquisitionsAsync(
        Guid workId, RequestMediaType mediaType, CancellationToken cancellationToken)
    {
        if (mediaType != RequestMediaType.Ebook && mediaType != RequestMediaType.Audiobook)
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

        var baseQuery = string.IsNullOrWhiteSpace(work.PrimaryAuthor)
            ? work.Title
            : $"{work.Title} {work.PrimaryAuthor}";
        var searchUrl = mediaType == RequestMediaType.Audiobook
            ? $"books/?search={Uri.EscapeDataString(baseQuery)}&mime_type=audio%2F"
            : $"books/?search={Uri.EscapeDataString(baseQuery)}";

        JsonNode? root;
        try
        {
            using var response = await httpClient.GetAsync(searchUrl, cancellationToken);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            root = JsonNode.Parse(body);
        }
        catch (JsonException exception)
        {
            // A malformed catalog response is a provider health problem, not a
            // genuine no-match. Wrap it as an HTTP-style provider failure so the
            // automatic worker records an admin-visible, safe failure outcome.
            throw new HttpRequestException("Gutendex returned an invalid catalog response.", exception);
        }

        var results = root?["results"]?.AsArray();
        if (results is null)
        {
            return [];
        }

        foreach (var result in results)
        {
            var title = result?["title"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(title) || !TitleMatches(work.Title, title))
            {
                continue;
            }

            // Gutendex's search results can include adaptations, reading guides,
            // and similarly titled works. For unattended acquisition we require
            // the title to begin with our canonical title *and* an exact match of
            // normalized creator-name tokens. The downloaded file is then still
            // independently verified against the Work before it can be trusted.
            var sourceAuthors = result?["authors"]?.AsArray() ?? [];
            if (!string.IsNullOrWhiteSpace(work.PrimaryAuthor) &&
                !sourceAuthors.Any(author =>
                    AuthorMatches(work.PrimaryAuthor, author?["name"]?.GetValue<string>())))
            {
                continue;
            }

            var idNode = result?["id"];
            if (idNode is null)
            {
                continue;
            }

            var bookId = idNode.GetValue<int>();

            var option = mediaType == RequestMediaType.Audiobook
                ? await TryBuildAudiobookOptionAsync(result, workId, bookId, cancellationToken)
                : TryBuildEbookOption(result, workId, bookId);
            if (option is not null)
            {
                return [option];
            }
        }

        return [];
    }

    private static FulfillmentOption? TryBuildEbookOption(JsonNode? result, Guid workId, int bookId)
    {
        var epubUrl = result?["formats"]?["application/epub+zip"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(epubUrl))
        {
            return null;
        }

        return new FulfillmentOption(
            ProviderId: ProviderRegistry.GutendexProviderId,
            ProviderResultId: bookId.ToString(CultureInfo.InvariantCulture),
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
            ProviderData: epubUrl);
    }

    /// <summary>
    /// Gutendex can confirm a "Sound" record exists but not enumerate its
    /// chapter files (see the class remarks), so a confirmed match still
    /// requires a follow-up read of Gutenberg's own RDF record for the full
    /// track list before this becomes an automatic candidate.
    /// </summary>
    private async Task<FulfillmentOption?> TryBuildAudiobookOptionAsync(
        JsonNode? result, Guid workId, int bookId, CancellationToken cancellationToken)
    {
        var mediaTypeValue = result?["media_type"]?.GetValue<string>();
        if (!string.Equals(mediaTypeValue, "Sound", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var tracks = await gutenbergCatalog.FindTracksAsync(bookId, cancellationToken);
        if (tracks.Count == 0 || tracks.Count > importPolicy.MaxAudiobookBundleTracks)
        {
            return null;
        }

        var providerData = JsonSerializer.Serialize(
            tracks.Select(track => new AudioBundleTrackData(track.Url.ToString(), track.Extension)).ToArray());

        return new FulfillmentOption(
            ProviderId: ProviderRegistry.GutendexProviderId,
            ProviderResultId: bookId.ToString(CultureInfo.InvariantCulture),
            WorkId: workId,
            EditionId: null,
            MediaType: RequestMediaType.Audiobook,
            OptionKind: OptionKind.DirectAcquisition,
            AcquisitionMethod: AcquisitionMethod.DirectDownload,
            Format: AudioBundleFormat,
            Language: null,
            Quality: null,
            Availability: null,
            Cost: 0m,
            Currency: null,
            LicenseOrUsageStatus: "Public domain",
            DrmStatus: null,
            ExternalActionUri: null,
            ProviderData: providerData);
    }

    public async Task<IReadOnlyList<DirectAcquisitionFile>> FetchAsync(
        FulfillmentOption fulfillmentOption, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fulfillmentOption);

        if (fulfillmentOption.Format == AudioBundleFormat)
        {
            return await FetchAudioBundleAsync(fulfillmentOption, cancellationToken);
        }

        var url = fulfillmentOption.ProviderData
            ?? throw new InvalidOperationException("This Gutendex option has no download URL.");

        var response = await httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        return [new DirectAcquisitionFile(stream, $"gutenberg-{fulfillmentOption.ProviderResultId}.epub")];
    }

    private async Task<IReadOnlyList<DirectAcquisitionFile>> FetchAudioBundleAsync(
        FulfillmentOption fulfillmentOption, CancellationToken cancellationToken)
    {
        var json = fulfillmentOption.ProviderData
            ?? throw new InvalidOperationException("This Gutendex option has no track list.");
        var tracks = JsonSerializer.Deserialize<AudioBundleTrackData[]>(json)
            ?? throw new InvalidOperationException("This Gutendex option's track list could not be read.");

        var files = new List<DirectAcquisitionFile>(tracks.Length);
        for (var index = 0; index < tracks.Length; index++)
        {
            var response = await httpClient.GetAsync(
                tracks[index].Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            files.Add(new DirectAcquisitionFile(
                stream,
                $"gutenberg-{fulfillmentOption.ProviderResultId}-{index + 1:00}{tracks[index].Extension}"));
        }

        return files;
    }

    private const string AudioBundleFormat = "audio-bundle";

    private sealed record AudioBundleTrackData(string Url, string Extension);

    private static readonly string[] LeadingArticles = ["The ", "A ", "An "];
    private static readonly string[] TrailingArticles = [", The", ", A", ", An"];

    private static bool TitleMatches(string expected, string actual)
    {
        var normalizedExpected = Normalize(NormalizeTitleVariants(expected));
        var normalizedActual = Normalize(NormalizeTitleVariants(actual));
        return !string.IsNullOrEmpty(normalizedExpected) &&
            normalizedActual.StartsWith(normalizedExpected, StringComparison.Ordinal);
    }

    /// <summary>
    /// Catalog sources disagree on where a leading article goes — Gutendex
    /// keeps "The Green Mummy" as written, while OpenLibrary-derived titles
    /// often arrive as sort-form "Green Mummy, The" or with the article
    /// dropped entirely. They also disagree on "&amp;" versus "and" (Gutendex
    /// always spells it out, e.g. "Romeo and Juliet"). Normalizing both sides
    /// to the same bare form keeps <see cref="TitleMatches"/> from rejecting
    /// an otherwise exact match.
    /// </summary>
    private static string NormalizeTitleVariants(string value)
    {
        var trimmed = value.Replace("&", " and ", StringComparison.Ordinal).Trim();

        foreach (var article in TrailingArticles)
        {
            if (trimmed.EndsWith(article, StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed[..^article.Length].TrimEnd();
                break;
            }
        }

        foreach (var article in LeadingArticles)
        {
            if (trimmed.StartsWith(article, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[article.Length..];
            }
        }

        return trimmed;
    }

    // Gutendex author names sometimes expand initials into a full-name
    // parenthetical the catalog doesn't carry (e.g. catalog "J. M. Barrie"
    // vs Gutendex "Barrie, J. M. (James Matthew)"). Requiring every catalog
    // token to appear in Gutendex's token set — rather than an exact
    // token-set match — accepts that without accepting a materially
    // different name, since every catalog word still has to agree.
    private static bool AuthorMatches(string expected, string? actual) =>
        !string.IsNullOrWhiteSpace(actual) &&
        IsAuthorSubsetMatch(AuthorTokens(expected), AuthorTokens(actual));

    private static bool IsAuthorSubsetMatch(string[] expectedTokens, string[] actualTokens) =>
        expectedTokens.Length > 0 && expectedTokens.All(actualTokens.Contains);

    private static string[] AuthorTokens(string value) => value
        .Normalize(NormalizationForm.FormKC)
        .Split(default(char[]), StringSplitOptions.RemoveEmptyEntries)
        .SelectMany(part => part.Split([',', '.', ';', ':', '-', '_'], StringSplitOptions.RemoveEmptyEntries))
        .Select(part => new string(part.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray()))
        .Where(part => !string.IsNullOrEmpty(part))
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static string Normalize(string value) => new(value
        .Normalize(NormalizationForm.FormKC)
        .Where(char.IsLetterOrDigit)
        .Select(char.ToLowerInvariant)
        .ToArray());
}
