using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FamilyLibrarian.Application.Catalog;
using Microsoft.Extensions.Options;

namespace FamilyLibrarian.Infrastructure.Metadata;

public sealed class OpenLibraryBookMetadataProvider(
    HttpClient httpClient,
    IOptions<OpenLibraryMetadataOptions> options) : IBookMetadataProvider
{
    private const int MaximumDescriptionLength = 8_000;
    private const int MaximumSubjects = 8;
    private const string SearchFields =
        "key,title,author_name,first_publish_date,cover_i,editions," +
        "editions.key,editions.title,editions.isbn,editions.publish_date,editions.format," +
        "publisher,subject,number_of_pages_median,description";

    private static readonly string[] ExactDateFormats =
    [
        "yyyy-MM-dd",
        "MMMM d, yyyy",
        "MMM d, yyyy",
        "d MMMM yyyy",
        "d MMM yyyy"
    ];

    private readonly OpenLibraryMetadataOptions _options = options.Value;

    public string Id => "openlibrary";

    public string DisplayName => "Open Library";

    public Task<IReadOnlyList<BookCandidate>> SearchAsync(
        BookSearchQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query.Text);

        var providerQuery = IsbnNormalizer.TryNormalizeQuery(query.Text, out var isbn)
            ? $"isbn:{isbn}"
            : query.Text;

        return SearchCoreAsync(
            providerQuery,
            SearchFields,
            _options.MaxResults,
            cancellationToken);
    }

    public async Task<BookCandidate?> GetDetailsAsync(
        string externalId,
        CancellationToken cancellationToken)
    {
        if (!IsValidWorkId(externalId))
        {
            return null;
        }

        var candidates = await SearchCoreAsync(
            $"key:/works/{externalId}",
            SearchFields,
            1,
            cancellationToken);

        return candidates.SingleOrDefault(candidate =>
            string.Equals(candidate.ExternalId, externalId, StringComparison.Ordinal));
    }

    private async Task<IReadOnlyList<BookCandidate>> SearchCoreAsync(
        string query,
        string fields,
        int limit,
        CancellationToken cancellationToken)
    {
        var requestUri =
            $"search.json?q={Uri.EscapeDataString(query)}" +
            $"&fields={Uri.EscapeDataString(fields)}" +
            $"&limit={limit.ToString(CultureInfo.InvariantCulture)}";

        var response = await httpClient.GetFromJsonAsync<OpenLibrarySearchResponse>(
            requestUri,
            cancellationToken);

        if (response?.Documents is not { Count: > 0 })
        {
            return [];
        }

        return response.Documents
            .Select(ToCandidate)
            .Where(candidate => candidate is not null)
            .Cast<BookCandidate>()
            .ToArray();
    }

    private BookCandidate? ToCandidate(OpenLibrarySearchDocument document)
    {
        var externalId = GetWorkId(document.Key);
        var title = document.Title?.Trim();
        if (externalId is null || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var authors = document.AuthorNames?
            .Where(author => !string.IsNullOrWhiteSpace(author))
            .Select(author => author.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];

        return new BookCandidate(
            Id,
            DisplayName,
            externalId,
            title,
            authors,
            GetDescription(document.Description),
            GetCoverUrl(document.CoverId),
            TryParseExactDate(FirstString(document.FirstPublishDates)),
            GetEditions(document, title),
            [],
            FirstString(document.Publishers),
            document.NumberOfPagesMedian is > 0 ? document.NumberOfPagesMedian : null,
            GetStrings(document.Subjects)
                .Where(subject => !string.IsNullOrWhiteSpace(subject))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaximumSubjects)
                .ToArray(),
            SourceUrl: $"https://openlibrary.org/works/{externalId}");
    }

    private static BookEditionCandidate[] GetEditions(
        OpenLibrarySearchDocument document,
        string workTitle)
    {
        var editionDocuments = document.Editions?.Documents ?? [];
        var editions = editionDocuments
            .Select(edition => ToEditionCandidate(edition, workTitle))
            .Where(edition => edition is not null)
            .Cast<BookEditionCandidate>()
            .DistinctBy(
                edition => edition.Isbn13 ??
                    $"{edition.Title}|{edition.Format}|{edition.PublicationDate}",
                StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (editions.Length > 0)
        {
            return editions;
        }

        var isbn13 = FirstNormalizedIsbn13(document.Isbns);
        return isbn13 is null
            ? []
            : [new BookEditionCandidate(workTitle, isbn13, "Unknown format", null)];
    }

    private static BookEditionCandidate? ToEditionCandidate(
        OpenLibraryEditionDocument edition,
        string workTitle)
    {
        var title = string.IsNullOrWhiteSpace(edition.Title)
            ? workTitle
            : edition.Title.Trim();
        var isbn13 = FirstNormalizedIsbn13(edition.Isbns);
        var format = FirstString(edition.Formats) ?? "Unknown format";
        var publicationDate = TryParseExactDate(FirstString(edition.PublishDates));

        return isbn13 is null && string.Equals(title, workTitle, StringComparison.Ordinal)
            && publicationDate is null && string.Equals(format, "Unknown format", StringComparison.Ordinal)
                ? null
                : new BookEditionCandidate(title, isbn13, format, publicationDate);
    }

    private static string? FirstNormalizedIsbn13(JsonElement values)
    {
        foreach (var value in GetStrings(values))
        {
            if (IsbnNormalizer.TryNormalizeToIsbn13(value, out var isbn13))
            {
                return isbn13;
            }
        }

        return null;
    }

    private static string? FirstString(JsonElement values) =>
        GetStrings(values).FirstOrDefault();

    private static IEnumerable<string> GetStrings(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String && value.GetString() is { } text)
        {
            yield return text;
            yield break;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } itemText)
            {
                yield return itemText;
            }
        }
    }

    private static string? GetDescription(JsonElement description)
    {
        string? value = description.ValueKind switch
        {
            JsonValueKind.String => description.GetString(),
            JsonValueKind.Object when description.TryGetProperty("value", out var nested) &&
                nested.ValueKind == JsonValueKind.String => nested.GetString(),
            _ => null
        };

        value = value?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Length <= MaximumDescriptionLength
            ? value
            : string.Concat(value.AsSpan(0, MaximumDescriptionLength).TrimEnd(), "…");
    }

    private static DateOnly? TryParseExactDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateOnly.TryParseExact(
            value.Trim(),
            ExactDateFormats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var date)
                ? date
                : null;
    }

    private static string? GetWorkId(string? key)
    {
        const string prefix = "/works/";
        return key?.StartsWith(prefix, StringComparison.Ordinal) == true &&
            IsValidWorkId(key[prefix.Length..])
                ? key[prefix.Length..]
                : null;
    }

    private static bool IsValidWorkId(string externalId) =>
        externalId.Length > 3 &&
        externalId.StartsWith("OL", StringComparison.Ordinal) &&
        externalId.EndsWith('W') &&
        externalId.AsSpan(2, externalId.Length - 3).IndexOfAnyExceptInRange('0', '9') < 0;

    private static string? GetCoverUrl(int? coverId) => coverId is > 0
        ? $"https://covers.openlibrary.org/b/id/{coverId.Value.ToString(CultureInfo.InvariantCulture)}-L.jpg?default=false"
        : null;

    private sealed class OpenLibrarySearchResponse
    {
        [JsonPropertyName("docs")]
        public IReadOnlyList<OpenLibrarySearchDocument>? Documents { get; init; }
    }

    private sealed class OpenLibrarySearchDocument
    {
        [JsonPropertyName("key")]
        public string? Key { get; init; }

        [JsonPropertyName("title")]
        public string? Title { get; init; }

        [JsonPropertyName("author_name")]
        public IReadOnlyList<string>? AuthorNames { get; init; }

        [JsonPropertyName("description")]
        public JsonElement Description { get; init; }

        [JsonPropertyName("first_publish_date")]
        public JsonElement FirstPublishDates { get; init; }

        [JsonPropertyName("isbn")]
        public JsonElement Isbns { get; init; }

        [JsonPropertyName("cover_i")]
        public int? CoverId { get; init; }

        [JsonPropertyName("publisher")]
        public JsonElement Publishers { get; init; }

        [JsonPropertyName("subject")]
        public JsonElement Subjects { get; init; }

        [JsonPropertyName("number_of_pages_median")]
        public int? NumberOfPagesMedian { get; init; }

        [JsonPropertyName("editions")]
        public OpenLibraryEditions? Editions { get; init; }
    }

    private sealed class OpenLibraryEditions
    {
        [JsonPropertyName("docs")]
        public IReadOnlyList<OpenLibraryEditionDocument>? Documents { get; init; }
    }

    private sealed class OpenLibraryEditionDocument
    {
        [JsonPropertyName("title")]
        public string? Title { get; init; }

        [JsonPropertyName("isbn")]
        public JsonElement Isbns { get; init; }

        [JsonPropertyName("publish_date")]
        public JsonElement PublishDates { get; init; }

        [JsonPropertyName("format")]
        public JsonElement Formats { get; init; }
    }
}
