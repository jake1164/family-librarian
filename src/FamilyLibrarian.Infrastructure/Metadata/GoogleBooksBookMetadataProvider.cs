using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using FamilyLibrarian.Application.Catalog;
using Microsoft.Extensions.Options;

namespace FamilyLibrarian.Infrastructure.Metadata;

public sealed class GoogleBooksBookMetadataProvider(
    HttpClient httpClient,
    IOptions<GoogleBooksMetadataOptions> options) : IBookMetadataProvider
{
    private const int MaximumDescriptionLength = 8_000;

    private readonly GoogleBooksMetadataOptions _options = options.Value;

    public string Id => "googlebooks";

    public string DisplayName => "Google Books";

    public async Task<BookCandidateSearchPage> SearchAsync(
        BookSearchQuery query,
        CancellationToken cancellationToken)
    {
        query.Validate();

        var providerQuery = IsbnNormalizer.TryNormalizeQuery(query.Text, out var isbn)
            ? $"isbn:{isbn}"
            : query.Text;
        var requestUri =
            $"volumes?q={Uri.EscapeDataString(providerQuery)}" +
            $"&maxResults={_options.MaxResults.ToString(CultureInfo.InvariantCulture)}" +
            $"&startIndex={((query.Page - 1) * _options.MaxResults).ToString(CultureInfo.InvariantCulture)}" +
            "&printType=books&projection=full";

        var response = await httpClient.GetFromJsonAsync<GoogleBooksVolumesResponse>(
            requestUri,
            cancellationToken);

        var items = response?.Items ?? [];
        var candidates = items
            .Select(ToCandidate)
            .Where(candidate => candidate is not null)
            .Cast<BookCandidate>()
            .ToArray();
        var offset = (query.Page - 1) * _options.MaxResults;
        var hasMore = response?.TotalItems is { } totalItems
            ? totalItems > offset + items.Count
            : items.Count == _options.MaxResults;

        return new BookCandidateSearchPage(candidates, hasMore);
    }

    public async Task<BookCandidate?> GetDetailsAsync(
        string externalId,
        CancellationToken cancellationToken)
    {
        if (!IsValidExternalId(externalId))
        {
            return null;
        }

        var response = await httpClient.GetFromJsonAsync<GoogleBooksVolume>(
            $"volumes/{Uri.EscapeDataString(externalId)}?projection=full",
            cancellationToken);

        return response is null ? null : ToCandidate(response);
    }

    private BookCandidate? ToCandidate(GoogleBooksVolume volume)
    {
        var externalId = volume.Id?.Trim();
        var title = GetTitle(volume.VolumeInfo);
        if (!IsValidExternalId(externalId) || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var authors = volume.VolumeInfo?.Authors?
            .Where(author => !string.IsNullOrWhiteSpace(author))
            .Select(author => author.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
        var publicationDate = TryParseExactDate(volume.VolumeInfo?.PublishedDate);
        var isbn13 = GetIsbn13(volume.VolumeInfo?.IndustryIdentifiers);
        var subjects = volume.VolumeInfo?.Categories?
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .Select(category => category.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];

        return new BookCandidate(
            Id,
            DisplayName,
            externalId!,
            title!,
            authors,
            GetDescription(volume.VolumeInfo?.Description),
            GetCoverUrl(volume.VolumeInfo?.ImageLinks),
            publicationDate,
            [new BookEditionCandidate(title, isbn13, "Unknown format", publicationDate)],
            [],
            volume.VolumeInfo?.Publisher?.Trim() is { Length: > 0 } publisher ? publisher : null,
            volume.VolumeInfo?.PageCount is > 0 ? volume.VolumeInfo.PageCount : null,
            subjects,
            SourceUrl: $"https://books.google.com/books?id={Uri.EscapeDataString(externalId!)}");
    }

    private static string? GetTitle(GoogleBooksVolumeInfo? volumeInfo)
    {
        var title = volumeInfo?.Title?.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var subtitle = volumeInfo?.Subtitle?.Trim();
        return string.IsNullOrWhiteSpace(subtitle) ? title : $"{title}: {subtitle}";
    }

    private static string? GetIsbn13(IReadOnlyList<GoogleBooksIndustryIdentifier>? identifiers)
    {
        if (identifiers is null)
        {
            return null;
        }

        foreach (var identifier in identifiers.Where(identifier =>
                     string.Equals(identifier.Type, "ISBN_13", StringComparison.OrdinalIgnoreCase)))
        {
            if (identifier.Identifier is { } value &&
                IsbnNormalizer.TryNormalizeToIsbn13(value, out var isbn13))
            {
                return isbn13;
            }
        }

        foreach (var identifier in identifiers)
        {
            if (identifier.Identifier is { } value &&
                IsbnNormalizer.TryNormalizeToIsbn13(value, out var isbn13))
            {
                return isbn13;
            }
        }

        return null;
    }

    private static string? GetDescription(string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Length <= MaximumDescriptionLength
            ? value
            : string.Concat(value.AsSpan(0, MaximumDescriptionLength).TrimEnd(), "…");
    }

    private static string? GetCoverUrl(GoogleBooksImageLinks? imageLinks)
    {
        var value = imageLinks?.Thumbnail ?? imageLinks?.SmallThumbnail;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        var builder = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 };
        return builder.Uri.AbsoluteUri;
    }

    private static DateOnly? TryParseExactDate(string? value) =>
        DateOnly.TryParseExact(
            value,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var date)
                ? date
                : null;

    private static bool IsValidExternalId(string? externalId) =>
        !string.IsNullOrWhiteSpace(externalId) &&
        externalId.Length <= 200 &&
        externalId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private sealed class GoogleBooksVolumesResponse
    {
        [JsonPropertyName("totalItems")]
        public int? TotalItems { get; init; }

        [JsonPropertyName("items")]
        public IReadOnlyList<GoogleBooksVolume>? Items { get; init; }
    }

    private sealed class GoogleBooksVolume
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("volumeInfo")]
        public GoogleBooksVolumeInfo? VolumeInfo { get; init; }
    }

    private sealed class GoogleBooksVolumeInfo
    {
        [JsonPropertyName("title")]
        public string? Title { get; init; }

        [JsonPropertyName("subtitle")]
        public string? Subtitle { get; init; }

        [JsonPropertyName("authors")]
        public IReadOnlyList<string>? Authors { get; init; }

        [JsonPropertyName("description")]
        public string? Description { get; init; }

        [JsonPropertyName("publishedDate")]
        public string? PublishedDate { get; init; }

        [JsonPropertyName("publisher")]
        public string? Publisher { get; init; }

        [JsonPropertyName("pageCount")]
        public int? PageCount { get; init; }

        [JsonPropertyName("categories")]
        public IReadOnlyList<string>? Categories { get; init; }

        [JsonPropertyName("industryIdentifiers")]
        public IReadOnlyList<GoogleBooksIndustryIdentifier>? IndustryIdentifiers { get; init; }

        [JsonPropertyName("imageLinks")]
        public GoogleBooksImageLinks? ImageLinks { get; init; }
    }

    private sealed class GoogleBooksIndustryIdentifier
    {
        [JsonPropertyName("type")]
        public string? Type { get; init; }

        [JsonPropertyName("identifier")]
        public string? Identifier { get; init; }
    }

    private sealed class GoogleBooksImageLinks
    {
        [JsonPropertyName("smallThumbnail")]
        public string? SmallThumbnail { get; init; }

        [JsonPropertyName("thumbnail")]
        public string? Thumbnail { get; init; }
    }
}
