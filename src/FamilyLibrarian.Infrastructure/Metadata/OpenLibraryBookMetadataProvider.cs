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
        "editions.cover_i,editions.publisher,editions.language," +
        "publisher,subject,number_of_pages_median,description,language";

    // No per-user/global language preference exists yet, so this is a fixed
    // default rather than a setting.
    private const string PreferredLanguage = "en";

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

    public Task<BookCandidateSearchPage> SearchAsync(
        BookSearchQuery query,
        CancellationToken cancellationToken)
    {
        query.Validate();

        var providerQuery = IsbnNormalizer.TryNormalizeQuery(query.Text, out var isbn)
            ? $"isbn:{isbn}"
            : query.Text;

        return SearchCoreAsync(
            providerQuery,
            SearchFields,
            _options.MaxResults,
            query.Page,
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

        var result = await SearchCoreAsync(
            $"key:/works/{externalId}",
            SearchFields,
            1,
            1,
            cancellationToken);

        var candidate = result.Candidates.SingleOrDefault(candidate =>
            string.Equals(candidate.ExternalId, externalId, StringComparison.Ordinal));

        return candidate is null
            ? null
            : await ApplyPreferredLanguageEditionAsync(candidate, externalId, cancellationToken);
    }

    // The search endpoint only ever includes one (arbitrary) edition per work,
    // so it can't tell us whether a preferred-language edition exists. The
    // detail view can afford the extra round trip to look at every edition
    // and swap in one that actually matches, including linking out to that
    // specific edition instead of the ambiguous work page.
    private async Task<BookCandidate> ApplyPreferredLanguageEditionAsync(
        BookCandidate candidate,
        string workId,
        CancellationToken cancellationToken)
    {
        OpenLibraryEditionsListResponse? response;
        try
        {
            response = await httpClient.GetFromJsonAsync<OpenLibraryEditionsListResponse>(
                $"works/{workId}/editions.json?limit=50",
                cancellationToken);
        }
        catch (HttpRequestException)
        {
            return candidate;
        }

        var preferredEdition = response?.Entries?.FirstOrDefault(entry =>
            entry.Languages?.Any(language => string.Equals(
                LanguageCodeNormalizer.Normalize(GetLanguageCode(language.Key)),
                PreferredLanguage,
                StringComparison.OrdinalIgnoreCase)) == true);

        if (preferredEdition is null)
        {
            return candidate;
        }

        return candidate with
        {
            CoverUrl = GetCoverUrl(
                preferredEdition.Covers is { Count: > 0 } covers ? covers[0] : null) ??
                    candidate.CoverUrl,
            Publisher = preferredEdition.Publishers?
                .FirstOrDefault(publisher => !string.IsNullOrWhiteSpace(publisher))?.Trim()
                    ?? candidate.Publisher,
            PageCount = preferredEdition.NumberOfPages is > 0
                ? preferredEdition.NumberOfPages
                : candidate.PageCount,
            Language = PreferredLanguage,
            SourceUrl = string.IsNullOrWhiteSpace(preferredEdition.Key)
                ? candidate.SourceUrl
                : $"https://openlibrary.org{preferredEdition.Key}"
        };
    }

    private static string? GetLanguageCode(string? languageKey)
    {
        const string prefix = "/languages/";
        return languageKey?.StartsWith(prefix, StringComparison.Ordinal) == true
            ? languageKey[prefix.Length..]
            : languageKey;
    }

    private async Task<BookCandidateSearchPage> SearchCoreAsync(
        string query,
        string fields,
        int limit,
        int page,
        CancellationToken cancellationToken)
    {
        var requestUri =
            $"search.json?q={Uri.EscapeDataString(query)}" +
            $"&fields={Uri.EscapeDataString(fields)}" +
            $"&limit={limit.ToString(CultureInfo.InvariantCulture)}" +
            $"&page={page.ToString(CultureInfo.InvariantCulture)}";

        var response = await httpClient.GetFromJsonAsync<OpenLibrarySearchResponse>(
            requestUri,
            cancellationToken);

        if (response?.Documents is not { Count: > 0 })
        {
            return new BookCandidateSearchPage([], false);
        }

        var candidates = response.Documents
            .Select(ToCandidate)
            .Where(candidate => candidate is not null)
            .Cast<BookCandidate>()
            .ToArray();

        var returnedCount = response.Documents.Count;
        var offset = (page - 1) * limit;
        var hasMore = response.NumberFound is { } numberFound
            ? numberFound > offset + returnedCount
            : returnedCount == limit;

        return new BookCandidateSearchPage(candidates, hasMore);
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

        // The work-level cover/publisher/language fields are aggregated across
        // every edition OL has ever indexed for this work (every translation,
        // reprint, and format), so they can each come from a different edition
        // and disagree with one another (e.g. an English title with a French
        // publisher). The single edition doc included in the response is one
        // real, self-consistent edition, so prefer its values where present.
        var editionDocuments = document.Editions?.Documents;
        var primaryEdition = editionDocuments is { Count: > 0 } ? editionDocuments[0] : null;

        return new BookCandidate(
            Id,
            DisplayName,
            externalId,
            title,
            authors,
            GetDescription(document.Description),
            GetCoverUrl(primaryEdition?.CoverId ?? document.CoverId),
            TryParseExactDate(FirstString(document.FirstPublishDates)),
            GetEditions(document, title),
            [],
            (primaryEdition is null ? null : FirstString(primaryEdition.Publishers)) ??
                FirstString(document.Publishers),
            document.NumberOfPagesMedian is > 0 ? document.NumberOfPagesMedian : null,
            GetStrings(document.Subjects)
                .Where(subject => !string.IsNullOrWhiteSpace(subject))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaximumSubjects)
                .ToArray(),
            SourceUrl: $"https://openlibrary.org/works/{externalId}",
            Language: LanguageCodeNormalizer.Normalize(
                (primaryEdition is null ? null : FirstString(primaryEdition.Languages)) ??
                    FirstString(document.Languages)));
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
        [JsonPropertyName("num_found")]
        public int? NumberFound { get; init; }

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

        [JsonPropertyName("language")]
        public JsonElement Languages { get; init; }
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

        [JsonPropertyName("cover_i")]
        public int? CoverId { get; init; }

        [JsonPropertyName("publisher")]
        public JsonElement Publishers { get; init; }

        [JsonPropertyName("language")]
        public JsonElement Languages { get; init; }
    }

    private sealed class OpenLibraryEditionsListResponse
    {
        [JsonPropertyName("entries")]
        public IReadOnlyList<OpenLibraryEditionListEntry>? Entries { get; init; }
    }

    private sealed class OpenLibraryEditionListEntry
    {
        [JsonPropertyName("key")]
        public string? Key { get; init; }

        [JsonPropertyName("languages")]
        public IReadOnlyList<OpenLibraryLanguageRef>? Languages { get; init; }

        [JsonPropertyName("publishers")]
        public IReadOnlyList<string>? Publishers { get; init; }

        [JsonPropertyName("number_of_pages")]
        public int? NumberOfPages { get; init; }

        [JsonPropertyName("covers")]
        public IReadOnlyList<int>? Covers { get; init; }
    }

    private sealed class OpenLibraryLanguageRef
    {
        [JsonPropertyName("key")]
        public string? Key { get; init; }
    }
}
