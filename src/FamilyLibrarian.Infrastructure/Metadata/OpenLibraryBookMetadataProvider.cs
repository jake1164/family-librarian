using System.Globalization;
using System.Net.Http.Json;
using System.Text;
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

        var response = await FetchSearchAsync(
            $"key:/works/{externalId}",
            SearchFields,
            1,
            1,
            cancellationToken);

        var document = response?.Documents?.SingleOrDefault(document =>
            string.Equals(GetWorkId(document.Key), externalId, StringComparison.Ordinal));
        var candidate = document is null ? null : ToCandidate(document, trustUntaggedEditionTitle: false);
        if (candidate is null)
        {
            return null;
        }

        // search.json never returns more than one nested edition document per
        // work no matter what is asked of it (verified live: OL45870364W
        // reports editions.numFound 2 and returns one), and for a key: lookup
        // the one it returns is arbitrary. The work's own edition list is the
        // only way the detail view can show every edition, so always read it.
        var entries = await FetchEditionsAsync(externalId, cancellationToken);
        if (entries.Count == 0)
        {
            return candidate;
        }

        var editions = ToEditionCandidates(entries, candidate.Title);
        if (editions.Length > 0)
        {
            candidate = candidate with { Editions = editions };
        }

        // The search document's own edition is the one Open Library judged
        // relevant; when it is already in the preferred language nothing in
        // the edition list beats it. editions.json is in modification order,
        // not relevance, so its first preferred-language entry can be an
        // abridgement or graded reader (observed live: "Killing Floor" ->
        // "Penguin Readers Level 4").
        if (IsPreferredLanguageEdition(PrimaryEdition(document!)))
        {
            return candidate;
        }

        var representative = SelectRepresentativeEdition(entries, candidate.Title);
        return representative is null
            ? candidate
            : ApplyEdition(candidate, representative.Value.Entry, representative.Value.LanguageConfirmed);
    }

    private async Task<IReadOnlyList<OpenLibraryEditionListEntry>> FetchEditionsAsync(
        string workId,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await httpClient.GetFromJsonAsync<OpenLibraryEditionsListResponse>(
                $"works/{workId}/editions.json?limit=50",
                cancellationToken);
            return response?.Entries ?? [];
        }
        catch (HttpRequestException)
        {
            return [];
        }
    }

    // Which edition's cover/publisher/page count should stand in for the work.
    // A confirmed preferred-language edition wins, and among several the one
    // whose own title is the work title (possibly with an edition subtitle)
    // beats a retitled abridgement. Open Library editions are very often
    // untagged - both entries of OL45870364W are - so an untagged edition that
    // still carries the work's title is the next best self-consistent record,
    // and far better than the work-level cover, which is aggregated across
    // every translation ever indexed under the work id.
    private static (OpenLibraryEditionListEntry Entry, bool LanguageConfirmed)? SelectRepresentativeEdition(
        IReadOnlyList<OpenLibraryEditionListEntry> entries,
        string workTitle)
    {
        var preferredLanguage = entries.Where(IsPreferredLanguageEntry).ToArray();
        if (preferredLanguage.Length > 0)
        {
            var titled = preferredLanguage.FirstOrDefault(entry =>
                TitleMatchesWorkTitle(entry.Title, workTitle));
            return (titled ?? preferredLanguage[0], true);
        }

        var untagged = entries.FirstOrDefault(entry =>
            entry.Languages is not { Count: > 0 } && TitleMatchesWorkTitle(entry.Title, workTitle));
        return untagged is null ? null : (untagged, false);
    }

    private static BookCandidate ApplyEdition(
        BookCandidate candidate,
        OpenLibraryEditionListEntry edition,
        bool languageConfirmed) =>
        candidate with
        {
            // An untagged edition only lends the fields that describe the
            // physical record. Its title and language are not confirmed to be
            // the preferred ones, so the work title stays.
            Title = languageConfirmed && !string.IsNullOrWhiteSpace(edition.Title)
                ? edition.Title.Trim()
                : candidate.Title,
            CoverUrl = GetCoverUrl(FirstCoverId(edition.Covers)) ?? candidate.CoverUrl,
            Publisher = edition.Publishers?
                .FirstOrDefault(publisher => !string.IsNullOrWhiteSpace(publisher))?.Trim()
                    ?? candidate.Publisher,
            PageCount = edition.NumberOfPages is > 0
                ? edition.NumberOfPages
                : candidate.PageCount,
            Language = languageConfirmed ? PreferredLanguage : candidate.Language,
            SourceUrl = languageConfirmed && !string.IsNullOrWhiteSpace(edition.Key)
                ? $"https://openlibrary.org{edition.Key}"
                : candidate.SourceUrl
        };

    private static bool IsPreferredLanguageEntry(OpenLibraryEditionListEntry entry) =>
        entry.Languages?.Any(language => string.Equals(
            LanguageCodeNormalizer.Normalize(GetLanguageCode(language.Key)),
            PreferredLanguage,
            StringComparison.OrdinalIgnoreCase)) == true;

    // An edition title counts as the work's own when it is the work title, or
    // the work title followed by edition wording ("Threshing Day (Wing and
    // Claw Collection)"), compared without case or punctuation.
    private static bool TitleMatchesWorkTitle(string? editionTitle, string workTitle)
    {
        var edition = NormalizeTitle(editionTitle);
        var work = NormalizeTitle(workTitle);

        return work.Length > 0 &&
            edition.StartsWith(work, StringComparison.Ordinal) &&
            (edition.Length == work.Length || edition[work.Length] == ' ');
    }

    private static string NormalizeTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
            }
            else if (builder.Length > 0 && builder[^1] != ' ')
            {
                builder.Append(' ');
            }
        }

        return builder.ToString().TrimEnd();
    }

    // Open Library records a removed cover as -1 instead of dropping it.
    private static int? FirstCoverId(IReadOnlyList<int>? covers)
    {
        var cover = covers?.FirstOrDefault(candidate => candidate > 0) ?? 0;
        return cover > 0 ? cover : null;
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
        var response = await FetchSearchAsync(query, fields, limit, page, cancellationToken);

        if (response?.Documents is not { Count: > 0 })
        {
            return new BookCandidateSearchPage([], false);
        }

        var candidates = response.Documents
            .Select(document => ToCandidate(document, trustUntaggedEditionTitle: true))
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

    private Task<OpenLibrarySearchResponse?> FetchSearchAsync(
        string query,
        string fields,
        int limit,
        int page,
        CancellationToken cancellationToken) =>
        httpClient.GetFromJsonAsync<OpenLibrarySearchResponse>(
            $"search.json?q={Uri.EscapeDataString(query)}" +
            $"&fields={Uri.EscapeDataString(fields)}&lang={PreferredLanguage}" +
            $"&limit={limit.ToString(CultureInfo.InvariantCulture)}" +
            $"&page={page.ToString(CultureInfo.InvariantCulture)}",
            cancellationToken);

    private static OpenLibraryEditionDocument? PrimaryEdition(OpenLibrarySearchDocument document) =>
        document.Editions?.Documents is { Count: > 0 } editions ? editions[0] : null;

    private static bool IsPreferredLanguageEdition(OpenLibraryEditionDocument? edition) =>
        edition is not null && string.Equals(
            LanguageCodeNormalizer.Normalize(FirstString(edition.Languages)),
            PreferredLanguage,
            StringComparison.OrdinalIgnoreCase);

    // trustUntaggedEditionTitle: in a text search the included edition is the one
    // that matched the query, so its title is relevant even without a language
    // tag. A key: lookup has nothing to match, so Open Library returns an arbitrary
    // edition (observed: a Portuguese edition for OL45870364W) and only a
    // confirmed preferred-language edition may override the work title.
    private BookCandidate? ToCandidate(OpenLibrarySearchDocument document, bool trustUntaggedEditionTitle)
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

        // The work-level title/cover/publisher/language fields are aggregated
        // across every edition OL has ever indexed for this work (every
        // translation, reprint, and format), so a work can be titled in one
        // language while its cover/publisher come from another. The single
        // edition doc included in the response is one real, self-consistent
        // edition - trust its title/cover/publisher/language as a set only
        // when that edition itself is in the preferred language, otherwise
        // mixing its fields with the (differently-languaged) work title would
        // just trade one kind of mismatch for another.
        var primaryEdition = PrimaryEdition(document);
        var primaryEditionLanguage = primaryEdition is null
            ? null
            : LanguageCodeNormalizer.Normalize(FirstString(primaryEdition.Languages));
        var useEditionFields = IsPreferredLanguageEdition(primaryEdition);

        // Title gets its own, slightly looser rule than cover/publisher/language
        // below: an edition whose language is merely *unknown* (not confirmed
        // to be some other language) still gets to keep its own title. Observed
        // live: an edition titled "Little Women - Complete Authorized Edition"
        // with no language tag lost its title to the work's own aggregate
        // title "Mujercitas" -- Open Library's work-level fields are mixed
        // across every translation/reprint ever indexed under that work id,
        // so treating "unknown" the same as "confirmed foreign" here can
        // silently substitute a completely different-language title instead
        // of just keeping the one real edition's own.
        var editionConfirmedOtherLanguage = primaryEditionLanguage is not null && !useEditionFields;
        var useEditionRecord = primaryEdition is not null && !editionConfirmedOtherLanguage &&
            (trustUntaggedEditionTitle || useEditionFields);
        var displayTitle = useEditionRecord && !string.IsNullOrWhiteSpace(primaryEdition!.Title)
            ? primaryEdition.Title.Trim()
            : title;

        // The cover is tied to the title, not to the looser language rule the
        // other fields use: whichever edition's title is being shown must be
        // the edition whose cover is shown, or the card illustrates a title it
        // isn't displaying. Observed live: a search for "threshing day"
        // returns the matched edition "Threshing Day (Wing and Claw
        // Collection)" with its own cover 15260611, while the work-level
        // cover_i is 15260919 - the cover of an unrelated Portuguese edition
        // of the same work. The edition's cover is already in the response, so
        // using it costs nothing.
        var coverId = useEditionRecord ? primaryEdition!.CoverId ?? document.CoverId : document.CoverId;
        var publisher = useEditionFields
            ? FirstString(primaryEdition!.Publishers) ?? FirstString(document.Publishers)
            : FirstString(document.Publishers);
        var language = useEditionFields
            ? PreferredLanguage
            : LanguageCodeNormalizer.Normalize(FirstString(document.Languages));

        return new BookCandidate(
            Id,
            DisplayName,
            externalId,
            displayTitle,
            authors,
            GetDescription(document.Description),
            GetCoverUrl(coverId),
            TryParseExactDate(FirstString(document.FirstPublishDates)),
            GetEditions(document, displayTitle),
            [],
            publisher,
            document.NumberOfPagesMedian is > 0 ? document.NumberOfPagesMedian : null,
            GetStrings(document.Subjects)
                .Where(subject => !string.IsNullOrWhiteSpace(subject))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaximumSubjects)
                .ToArray(),
            SourceUrl: $"https://openlibrary.org/works/{externalId}",
            Language: language,
            WorkTitle: title);
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
                : new BookEditionCandidate(title, isbn13, format, publicationDate,
                    LanguageCodeNormalizer.Normalize(FirstString(edition.Languages)), FirstString(edition.Publishers));
    }

    private static BookEditionCandidate[] ToEditionCandidates(
        IReadOnlyList<OpenLibraryEditionListEntry> entries,
        string workTitle) =>
        entries
            .Select(entry => ToEditionCandidate(entry, workTitle))
            .Where(edition => edition is not null)
            .Cast<BookEditionCandidate>()
            .DistinctBy(
                edition => edition.Isbn13 ??
                    $"{edition.Title}|{edition.Format}|{edition.PublicationDate}",
                StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static BookEditionCandidate? ToEditionCandidate(
        OpenLibraryEditionListEntry entry,
        string workTitle)
    {
        var title = string.IsNullOrWhiteSpace(entry.Title) ? workTitle : entry.Title.Trim();
        var isbn13 = FirstNormalizedIsbn13(entry.Isbn13s) ?? FirstNormalizedIsbn13(entry.Isbn10s);
        var format = string.IsNullOrWhiteSpace(entry.PhysicalFormat)
            ? "Unknown format"
            : entry.PhysicalFormat.Trim();

        return isbn13 is null && string.Equals(title, workTitle, StringComparison.Ordinal)
            && string.Equals(format, "Unknown format", StringComparison.Ordinal)
                ? null
                : new BookEditionCandidate(title, isbn13, format, TryParseExactDate(entry.PublishDate),
                    LanguageCodeNormalizer.Normalize(GetLanguageCode(entry.Languages is { Count: > 0 } languages ? languages[0].Key : null)),
                    entry.Publishers is { Count: > 0 } publishers ? publishers[0] : null);
    }

    private static string? FirstNormalizedIsbn13(IEnumerable<string>? values)
    {
        foreach (var value in values ?? [])
        {
            if (IsbnNormalizer.TryNormalizeToIsbn13(value, out var isbn13))
            {
                return isbn13;
            }
        }

        return null;
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

        [JsonPropertyName("title")]
        public string? Title { get; init; }

        [JsonPropertyName("languages")]
        public IReadOnlyList<OpenLibraryLanguageRef>? Languages { get; init; }

        [JsonPropertyName("publishers")]
        public IReadOnlyList<string>? Publishers { get; init; }

        [JsonPropertyName("number_of_pages")]
        public int? NumberOfPages { get; init; }

        [JsonPropertyName("covers")]
        public IReadOnlyList<int>? Covers { get; init; }

        [JsonPropertyName("isbn_13")]
        public IReadOnlyList<string>? Isbn13s { get; init; }

        [JsonPropertyName("isbn_10")]
        public IReadOnlyList<string>? Isbn10s { get; init; }

        [JsonPropertyName("physical_format")]
        public string? PhysicalFormat { get; init; }

        [JsonPropertyName("publish_date")]
        public string? PublishDate { get; init; }
    }

    private sealed class OpenLibraryLanguageRef
    {
        [JsonPropertyName("key")]
        public string? Key { get; init; }
    }
}
