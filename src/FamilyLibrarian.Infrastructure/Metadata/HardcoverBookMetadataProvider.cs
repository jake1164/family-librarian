using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using FamilyLibrarian.Application.Catalog;

namespace FamilyLibrarian.Infrastructure.Metadata;

/// <summary>
/// Hardcover as a second metadata provider, powered by the admin's own
/// personal access token (METADATA-1).
/// </summary>
/// <remarks>
/// Hardcover's search endpoint (<c>search</c>) only ever returns matching book
/// ids — full typed fields need a second query, and the two cannot be combined
/// into one HTTP request (Hardcover documents that a request containing a
/// <c>search</c> field may contain no other top-level field). So
/// <see cref="SearchAsync"/> always issues two sequential requests: one to
/// rank/find ids, one to fetch <c>books</c> by those ids. <see cref="GetDetailsAsync"/>
/// needs only one, via <c>books_by_pk</c>.
/// <para>
/// Field mapping is grounded in Hardcover's published <c>schema.graphql</c>
/// (github.com/hardcoverapp/hardcover-docs), not assumed — see the Alpha 3
/// plan's §H. One field is a documented best-effort guess rather than a
/// verified shape: <c>books.cached_image</c> is an untyped <c>jsonb</c> column:
/// <see cref="GetCoverUrl"/> assumes it carries a <c>url</c> string property,
/// matching how other public Hardcover API integrations read it, but this has
/// not been confirmed against a live response in this codebase.
/// </para>
/// </remarks>
public sealed class HardcoverBookMetadataProvider(
    HttpClient httpClient,
    HardcoverLanguageLookupCache languageLookup) : IBookMetadataProvider
{
    private const int MaximumDescriptionLength = 8_000;

    private const string BookFields =
        """
        id
        title
        description
        release_date
        slug
        compilation
        is_partial_book
        cached_image
        contributions {
          author {
            name
          }
        }
        book_series {
          position
          featured
          series {
            name
            is_completed
          }
        }
        default_physical_edition {
          language_id
          pages
        }
        default_ebook_edition {
          language_id
          pages
        }
        editions(limit: 10, order_by: { id: asc }) {
          isbn_13
          isbn_10
          edition_format
          release_date
        }
        """;

    private const string SearchQuery =
        """
        query Search($query: String!, $page: Int!, $perPage: Int!) {
          search(query: $query, query_type: "Book", page: $page, per_page: $perPage) {
            ids
          }
        }
        """;

    private static readonly string BooksByIdsQuery =
        $$"""
        query BooksByIds($ids: [Int!]) {
          books(where: { id: { _in: $ids } }) {
            {{BookFields}}
          }
        }
        """;

    private static readonly string BookByIdQuery =
        $$"""
        query BookById($id: Int!) {
          books_by_pk(id: $id) {
            {{BookFields}}
          }
        }
        """;

    public string Id => "hardcover";

    public string DisplayName => "Hardcover";

    public async Task<BookCandidateSearchPage> SearchAsync(
        BookSearchQuery query,
        CancellationToken cancellationToken)
    {
        query.Validate();

        var searchResult = await HardcoverGraphQlClient.ExecuteAsync<SearchData>(
            httpClient,
            SearchQuery,
            new { query = query.Text, page = query.Page, perPage = MaxResultsPerPage },
            cancellationToken);

        var ids = searchResult?.Search?.Ids?
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .ToArray() ?? [];

        if (ids.Length == 0)
        {
            return new BookCandidateSearchPage([], false);
        }

        var booksResult = await HardcoverGraphQlClient.ExecuteAsync<BooksData>(
            httpClient,
            BooksByIdsQuery,
            new { ids },
            cancellationToken);

        var booksById = (booksResult?.Books ?? []).ToDictionary(book => book.Id);

        var candidates = new List<BookCandidate>(ids.Length);
        foreach (var id in ids)
        {
            if (booksById.TryGetValue(id, out var book))
            {
                var candidate = await ToCandidateAsync(book, cancellationToken);
                if (candidate is not null)
                {
                    candidates.Add(candidate);
                }
            }
        }

        // Hardcover's search does not report a total match count, only the
        // page of ids it returned, so a full page is the only available
        // signal that another page might exist.
        return new BookCandidateSearchPage(candidates, ids.Length == MaxResultsPerPage);
    }

    public async Task<BookCandidate?> GetDetailsAsync(
        string externalId,
        CancellationToken cancellationToken)
    {
        if (!int.TryParse(externalId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ||
            id <= 0)
        {
            return null;
        }

        var result = await HardcoverGraphQlClient.ExecuteAsync<BookByIdData>(
            httpClient,
            BookByIdQuery,
            new { id },
            cancellationToken);

        return result?.Book is { } book ? await ToCandidateAsync(book, cancellationToken) : null;
    }

    private async Task<BookCandidate?> ToCandidateAsync(
        HardcoverBook book,
        CancellationToken cancellationToken)
    {
        var title = book.Title?.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        // Observed live: a real, well-populated Hardcover row (cover,
        // description, correctly-linked contributors) can still be a
        // multi-book omnibus titled only after its author, e.g. a "Tom
        // Clancy" 3-in-1 audiobook compilation — not a stub, but not a
        // single describable work either, and FL's acquisition model has no
        // notion of a bundle. This was named in this provider's own plan
        // (§H) from the start; wiring it in was simply missed until this was
        // found live.
        if (book.Compilation || book.IsPartialBook == true)
        {
            return null;
        }

        var authors = (book.Contributions ?? [])
            .Select(contribution => contribution.Author?.Name?.Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var editions = BuildEditions(book, title);
        var series = BuildSeries(book);
        var description = GetDescription(book.Description);
        var coverUrl = GetCoverUrl(book.CachedImage);

        // Hardcover's catalog is community-editable and has stub "book" rows
        // with essentially nothing filled in — observed live: an author-search
        // for "Tom Clancy" surfaced a bare book titled "Tom Clancy" itself,
        // with no cover, no description, no linked contributor, and no
        // editions (Hardcover's own auto-generated slug for it,
        // "tom-clancy-<uuid>", is that same fallback pattern: a title-plus-
        // random-id used only when there is nothing more specific to build a
        // slug from). A row with a title and *zero* other signal of being a
        // real, describable book is not a usable search result regardless of
        // provider, so it is dropped here rather than reaching the catalog.
        if (authors.Length == 0 && editions.Length == 0 &&
            description is null && coverUrl is null)
        {
            return null;
        }

        var representativeEdition = book.DefaultPhysicalEdition ?? book.DefaultEbookEdition;
        var language = representativeEdition?.LanguageId is { } languageId
            ? await languageLookup.GetIsoCodeAsync(languageId, cancellationToken)
            : null;

        return new BookCandidate(
            Id,
            DisplayName,
            book.Id.ToString(CultureInfo.InvariantCulture),
            title,
            authors,
            description,
            coverUrl,
            TryParseExactDate(book.ReleaseDate),
            editions,
            series,
            Publisher: null,
            PageCount: representativeEdition?.Pages,
            Subjects: [],
            SourceUrl: string.IsNullOrWhiteSpace(book.Slug)
                ? $"https://hardcover.app/books/{book.Id.ToString(CultureInfo.InvariantCulture)}"
                : $"https://hardcover.app/books/{Uri.EscapeDataString(book.Slug)}",
            Language: language);
    }

    private static BookEditionCandidate[] BuildEditions(HardcoverBook book, string workTitle) =>
        (book.Editions ?? [])
            .Select(edition => ToEditionCandidate(edition, workTitle))
            .Where(edition => edition is not null)
            .Cast<BookEditionCandidate>()
            .ToArray();

    private static BookEditionCandidate? ToEditionCandidate(
        HardcoverEdition edition,
        string workTitle)
    {
        var isbn13 = FirstNormalizedIsbn13(edition.Isbn13, edition.Isbn10);
        var format = string.IsNullOrWhiteSpace(edition.EditionFormat)
            ? "Unknown format"
            : edition.EditionFormat.Trim();
        var publicationDate = TryParseExactDate(edition.ReleaseDate);

        return isbn13 is null && publicationDate is null &&
            string.Equals(format, "Unknown format", StringComparison.Ordinal)
                ? null
                : new BookEditionCandidate(workTitle, isbn13, format, publicationDate);
    }

    private static string? FirstNormalizedIsbn13(string? isbn13, string? isbn10)
    {
        if (!string.IsNullOrWhiteSpace(isbn13) &&
            IsbnNormalizer.TryNormalizeToIsbn13(isbn13, out var normalized13))
        {
            return normalized13;
        }

        if (!string.IsNullOrWhiteSpace(isbn10) &&
            IsbnNormalizer.TryNormalizeToIsbn13(isbn10, out var normalizedFrom10))
        {
            return normalizedFrom10;
        }

        return null;
    }

    private static BookSeriesCandidate[] BuildSeries(HardcoverBook book) =>
        (book.BookSeries ?? [])
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Series?.Name))
            .Select(entry => new BookSeriesCandidate(
                entry.Series!.Name!.Trim(),
                FormatPositionLabel(entry.Position),
                entry.Featured,
                (decimal?)entry.Position,
                entry.Series.IsCompleted == true))
            .ToArray();

    private static string? FormatPositionLabel(double? position) =>
        position?.ToString("0.###", CultureInfo.InvariantCulture);

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

    // Undocumented shape: Hardcover's cached_image column is untyped jsonb.
    // Other public integrations read a "url" string property off it; this is
    // a best-effort match against that convention, not a verified contract.
    private static string? GetCoverUrl(JsonElement? cachedImage)
    {
        if (cachedImage is not { ValueKind: JsonValueKind.Object } image ||
            !image.TryGetProperty("url", out var urlProperty) ||
            urlProperty.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = urlProperty.GetString();
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                ? uri.AbsoluteUri
                : null;
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

    private const int MaxResultsPerPage = 10;

    private sealed class SearchData
    {
        [JsonPropertyName("search")]
        public SearchOutput? Search { get; init; }
    }

    private sealed class SearchOutput
    {
        [JsonPropertyName("ids")]
        public IReadOnlyList<int?>? Ids { get; init; }
    }

    private sealed class BooksData
    {
        [JsonPropertyName("books")]
        public IReadOnlyList<HardcoverBook>? Books { get; init; }
    }

    private sealed class BookByIdData
    {
        [JsonPropertyName("books_by_pk")]
        public HardcoverBook? Book { get; init; }
    }

    private sealed class HardcoverBook
    {
        [JsonPropertyName("id")]
        public int Id { get; init; }

        [JsonPropertyName("title")]
        public string? Title { get; init; }

        [JsonPropertyName("description")]
        public string? Description { get; init; }

        [JsonPropertyName("release_date")]
        public string? ReleaseDate { get; init; }

        [JsonPropertyName("slug")]
        public string? Slug { get; init; }

        [JsonPropertyName("compilation")]
        public bool Compilation { get; init; }

        [JsonPropertyName("is_partial_book")]
        public bool? IsPartialBook { get; init; }

        [JsonPropertyName("cached_image")]
        public JsonElement? CachedImage { get; init; }

        [JsonPropertyName("contributions")]
        public IReadOnlyList<HardcoverContribution>? Contributions { get; init; }

        [JsonPropertyName("book_series")]
        public IReadOnlyList<HardcoverBookSeries>? BookSeries { get; init; }

        [JsonPropertyName("default_physical_edition")]
        public HardcoverDefaultEdition? DefaultPhysicalEdition { get; init; }

        [JsonPropertyName("default_ebook_edition")]
        public HardcoverDefaultEdition? DefaultEbookEdition { get; init; }

        [JsonPropertyName("editions")]
        public IReadOnlyList<HardcoverEdition>? Editions { get; init; }
    }

    private sealed class HardcoverContribution
    {
        [JsonPropertyName("author")]
        public HardcoverAuthor? Author { get; init; }
    }

    private sealed class HardcoverAuthor
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }
    }

    private sealed class HardcoverBookSeries
    {
        [JsonPropertyName("position")]
        public double? Position { get; init; }

        [JsonPropertyName("featured")]
        public bool Featured { get; init; }

        [JsonPropertyName("series")]
        public HardcoverSeriesRef? Series { get; init; }
    }

    private sealed class HardcoverSeriesRef
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("is_completed")]
        public bool? IsCompleted { get; init; }
    }

    private sealed class HardcoverDefaultEdition
    {
        [JsonPropertyName("language_id")]
        public int? LanguageId { get; init; }

        [JsonPropertyName("pages")]
        public int? Pages { get; init; }
    }

    private sealed class HardcoverEdition
    {
        [JsonPropertyName("isbn_13")]
        public string? Isbn13 { get; init; }

        [JsonPropertyName("isbn_10")]
        public string? Isbn10 { get; init; }

        [JsonPropertyName("edition_format")]
        public string? EditionFormat { get; init; }

        [JsonPropertyName("release_date")]
        public string? ReleaseDate { get; init; }
    }
}
