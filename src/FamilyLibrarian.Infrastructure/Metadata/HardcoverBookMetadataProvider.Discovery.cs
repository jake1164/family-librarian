using System.Globalization;
using System.Text.Json.Serialization;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Catalog.Discovery;
using CatalogIdentityPolicy = FamilyLibrarian.Application.Catalog.Identity.CatalogIdentityPolicy;
using FamilyLibrarian.Domain.Catalog;

namespace FamilyLibrarian.Infrastructure.Metadata;

/// <summary>
/// Read-only author/series discovery on the same Hardcover provider instance, credential and handler pipeline as search/detail.
/// </summary>
/// <remarks>
/// <para>
/// Every document below uses one top-level GraphQL field, because Hardcover charges one request per top-level field and
/// rejects an over-wide document with 403 <c>request_exceeds_capacity</c>. Selection is always by native id; a name is never
/// used to pick a series. Field names come from Hardcover's published schema pages (book_series, series, contributions
/// filters); the id filters, the <c>contributor_role</c> relation on contributions and the <c>series.author_id</c> filter have
/// <b>not</b> been exercised against the live API by this codebase, which is why their capabilities are declared Unverified.
/// </para>
/// <para>
/// Pages are 99 rows read with one look-ahead row, because the provider caps a request at 100 rows: that makes "another
/// page exists" known rather than guessed from a full page. Offsets are the continuation; the host binds them to an owner and
/// operation. A page can shift if Hardcover's data changes between requests; that is stated, never repaired by guessing.
/// </para>
/// </remarks>
public sealed partial class HardcoverBookMetadataProvider
{
    private const int DiscoveryPageSize = 99;
    private const int DiscoveryRowsRequested = DiscoveryPageSize + 1;
    private const int MaximumSeriesPerBook = 50;
    private const string ProviderKey = "hardcover";

    private const string DiscoverySeriesFields =
        "id name is_completed books_count primary_books_count canonical_id";

    private const string DiscoveryBookFields =
        """
        id
        title
        release_date
        compilation
        is_partial_book
        contributions {
          contributor_role {
            slug
          }
          author {
            id
            name
          }
        }
        """;

    private static readonly string BookSeriesDiscoveryQuery =
        $$$$"""
        query DiscoveryBookSeries($id: Int!) {
          books_by_pk(id: $id) {
            id
            book_series(order_by: {series_id: asc}, limit: {{{{MaximumSeriesPerBook}}}}) {
              position
              details
              compilation
              featured
              series {
                {{{{DiscoverySeriesFields}}}}
              }
            }
          }
        }
        """;

    private static readonly string CanonicalSeriesQuery =
        $$$$"""
        query DiscoveryCanonicalSeries($ids: [Int!]) {
          series(where: {id: {_in: $ids}}) {
            {{{{DiscoverySeriesFields}}}}
          }
        }
        """;

    private static readonly string SeriesMembersQuery =
        $$$$"""
        query DiscoverySeriesMembers($id: Int!, $limit: Int!, $offset: Int!) {
          series(where: {id: {_eq: $id}}, limit: 1) {
            {{{{DiscoverySeriesFields}}}}
            book_series(
              where: {book: {canonical_id: {_is_null: true}}}
              order_by: [{position: asc}, {book_id: asc}]
              limit: $limit
              offset: $offset
            ) {
              position
              details
              compilation
              featured
              book {
                {{{{DiscoveryBookFields}}}}
              }
            }
          }
        }
        """;

    private const string AuthorSearchQuery =
        """
        query DiscoveryAuthorSearch($query: String!) {
          search(query: $query, query_type: "Author", page: 1, per_page: 10) {
            ids
          }
        }
        """;

    private const string AuthorsByIdQuery =
        """
        query DiscoveryAuthors($ids: [Int!], $bookIds: [Int!], $isbns: [String!]) {
          authors(where: {id: {_in: $ids}}, limit: 10) {
            id
            name
            books_count
            contributions(
              where: {book: {_or: [{id: {_in: $bookIds}}, {editions: {isbn_13: {_in: $isbns}}}]}}
              limit: 1
            ) {
              book {
                id
              }
            }
          }
        }
        """;

    private static readonly string AuthorWorksQuery =
        $$$$"""
        query DiscoveryAuthorWorks($id: Int!, $limit: Int!, $offset: Int!) {
          authors(where: {id: {_eq: $id}}, limit: 1) {
            id
            name
            contributions(
              where: {
                contributable_type: {_eq: "Book"}
                contributor_role: {slug: {_eq: "author"}}
                book: {canonical_id: {_is_null: true}}
              }
              order_by: {book: {id: asc}}
              limit: $limit
              offset: $offset
            ) {
              book {
                {{{{DiscoveryBookFields}}}}
                book_series(order_by: {series_id: asc}, limit: {{{{MaximumSeriesPerBook}}}}) {
                  position
                  details
                  compilation
                  series {
                    {{{{DiscoverySeriesFields}}}}
                  }
                }
              }
            }
          }
        }
        """;

    private static readonly string AuthorSeriesQuery =
        $$$$"""
        query DiscoveryAuthorSeries($id: Int!, $limit: Int!, $offset: Int!) {
          series(
            where: {author_id: {_eq: $id}, canonical_id: {_is_null: true}}
            order_by: {id: asc}
            limit: $limit
            offset: $offset
          ) {
            {{{{DiscoverySeriesFields}}}}
          }
        }
        """;

    private static readonly DiscoveryCapabilities DiscoveryCapabilityTable = new(
    [
        new(DiscoveryOperation.FindSeriesForBook, DiscoveryCapability.Supported),
        new(DiscoveryOperation.ResolveAuthor, DiscoveryCapability.Supported),
        new(DiscoveryOperation.GetAuthorWorks, DiscoveryCapability.Supported),
        new(DiscoveryOperation.GetSeriesMembers, DiscoveryCapability.Unverified("series-id-filter-unverified-live")),
        new(DiscoveryOperation.GetAuthorSeries, DiscoveryCapability.Unverified("author-series-filter-unverified-live"))
    ]);

    private readonly TimeProvider time = clock ?? TimeProvider.System;

    string IBookMetadataDiscoveryProvider.ProviderId => Id;

    DiscoveryCapabilities IBookMetadataDiscoveryProvider.Capabilities => DiscoveryCapabilityTable;

    // ------------------------------------------------------------------------------------------------ book -> series

    /// <summary>The series-only view of <see cref="FindSeriesClaimsForBookAsync"/>: the same single read, projected, never fetched twice.</summary>
    public async Task<DiscoveryResult<SeriesRef>> FindSeriesForBookAsync(
        SourceRef book, IDiscoveryBudget budget, CancellationToken cancellationToken) =>
        DiscoveryResultProjection.SeriesOnly(await FindSeriesClaimsForBookAsync(book, budget, cancellationToken));

    /// <summary>One <c>books_by_pk</c> read: each series claim with this book's position and details text in it.</summary>
    public async Task<DiscoveryResult<BookSeriesClaim>> FindSeriesClaimsForBookAsync(
        SourceRef book, IDiscoveryBudget budget, CancellationToken cancellationToken)
    {
        var scope = new DiscoveryScope(DiscoveryOperation.FindSeriesForBook, book, "all-series-claims-of-this-book");
        if (!TryNativeId(book, ExternalReferenceEntityType.Work, out var bookId))
        {
            return Refused<BookSeriesClaim>(scope, budget, DiscoveryCodes.SubjectInvalid);
        }

        using var work = budget.BeginWork();
        var first = await HardcoverGraphQlClient.ExecuteDiscoveryAsync<BookSeriesDiscoveryData>(
            httpClient, BookSeriesDiscoveryQuery, new { id = bookId }, budget, cancellationToken);
        if (!first.HasData)
        {
            return Stopped<BookSeriesClaim>(scope, budget, first.Transport, first.Code);
        }

        var record = first.Data!.Book;
        var hasFieldErrors = first.Errors.Count > 0;
        if (record is null)
        {
            // A null record with no error is a successful look-up that found nothing for this id; with an error it is unknown.
            return hasFieldErrors
                ? NotAnswered<BookSeriesClaim>(scope, budget, DiscoveryOutcome.Failed, DiscoveryCodes.PartialFieldError)
                : Empty<BookSeriesClaim>(scope, budget);
        }

        if (record.BookSeries is null)
        {
            return NotAnswered<BookSeriesClaim>(scope, budget, DiscoveryOutcome.Failed, DiscoveryCodes.ProviderError);
        }

        var notes = new List<string>();
        var rows = new List<DiscSeries>();
        var entries = new List<(DiscSeries Series, DiscBookSeries Entry)>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var rowErrors = HardcoverRowErrors.Of(first.Errors, "books_by_pk.book_series");
        for (var index = 0; index < record.BookSeries.Count; index++)
        {
            var entry = record.BookSeries[index];
            if (rowErrors.Failed(index))
            {
                // A claim with a failed field is unknown, not a complete claim; it is withheld and the result says so.
                continue;
            }

            if (entry?.Series is null || string.IsNullOrWhiteSpace(entry.Series.Name))
            {
                continue;
            }

            var key = entry.Series.Id is > 0 ? "id:" + entry.Series.Id : "name:" + entry.Series.Name.Trim();
            if (seenKeys.Add(key))
            {
                rows.Add(entry.Series);
            }

            // Every membership row is a claim: the same series listed twice with different positions keeps both.
            entries.Add((entry.Series, entry));
        }

        var canonicalLookup = await LookUpCanonicalAsync(rows, budget, cancellationToken);
        var refs = rows.ToDictionary(
            row => row.Id is > 0 ? "id:" + row.Id : "name:" + row.Name!.Trim(),
            row => ToSeriesRef(row, canonicalLookup, notes),
            StringComparer.Ordinal);
        var items = new List<BookSeriesClaim>();
        var seenClaims = new HashSet<(string Series, double? Position, string? Details)>();
        foreach (var (row, entry) in entries)
        {
            var seriesRef = refs[row.Id is > 0 ? "id:" + row.Id : "name:" + row.Name!.Trim()];
            if (!seenClaims.Add((seriesRef.Id?.ExternalId ?? "name:" + seriesRef.Label, entry.Position, entry.Details)))
            {
                notes.Add(DiscoveryCodes.DuplicateDropped);
                continue;
            }

            items.Add(new BookSeriesClaim(seriesRef, PositionOf(entry.Position, entry.Details, seriesRef.Id)));
        }

        if (items.GroupBy(claim => claim.Series.Id?.ExternalId ?? "name:" + claim.Series.Label).Any(group => group.Count() > 1))
        {
            notes.Add("series-position-conflict");
        }

        budget.RecordItems(items.Count);

        if (hasFieldErrors)
        {
            notes.Add(DiscoveryCodes.PartialFieldError);
            return items.Count == 0
                ? NotAnswered<BookSeriesClaim>(scope, budget, DiscoveryOutcome.Failed, DiscoveryCodes.PartialFieldError)
                : DiscoveryResult.PartialSuccess(items, scope, time.GetUtcNow(), budget.Usage, Distinct(notes));
        }

        if (items.Count == 0)
        {
            return Empty<BookSeriesClaim>(scope, budget, Distinct(notes));
        }

        var completion = record.BookSeries.Count >= MaximumSeriesPerBook ? DiscoveryCompletion.ItemTruncated : DiscoveryCompletion.ExhaustedInCheckedScope;
        return DiscoveryResult.Found(items, scope, completion, time.GetUtcNow(), budget.Usage, notes: Distinct(notes));
    }

    // ---------------------------------------------------------------------------------------------- series -> members

    public async Task<DiscoveryResult<SeriesMember>> GetSeriesMembersAsync(
        SeriesRef series, DiscoveryCursor? cursor, IDiscoveryBudget budget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(series);
        var scope = new DiscoveryScope(DiscoveryOperation.GetSeriesMembers, series.Id, "non-merged-books", "position-then-book-id");
        if (series.Id is null || !TryNativeId(series.Id, ExternalReferenceEntityType.Series, out var seriesId))
        {
            // A label or another provider's id is never enough to pick a series here.
            return Refused<SeriesMember>(scope, budget, DiscoveryCodes.SubjectInvalid);
        }

        if (!TryOffset(cursor, out var offset))
        {
            return Refused<SeriesMember>(scope, budget, DiscoveryCodes.CursorInvalid);
        }

        if (!budget.TryAdmitPage())
        {
            return NotAnswered<SeriesMember>(scope, budget, DiscoveryOutcome.Failed, DiscoveryCodes.PageCeiling);
        }

        using var work = budget.BeginWork();
        var response = await HardcoverGraphQlClient.ExecuteDiscoveryAsync<SeriesDiscoveryData>(
            httpClient, SeriesMembersQuery, new { id = seriesId, limit = DiscoveryRowsRequested, offset }, budget, cancellationToken);
        if (!response.HasData)
        {
            return Stopped<SeriesMember>(scope, budget, response.Transport, response.Code);
        }

        var record = response.Data!.Series is { Count: > 0 } found ? found[0] : null;
        var hasFieldErrors = response.Errors.Count > 0;
        if (record is null)
        {
            return hasFieldErrors
                ? NotAnswered<SeriesMember>(scope, budget, DiscoveryOutcome.Failed, DiscoveryCodes.PartialFieldError)
                : Empty<SeriesMember>(scope, budget);
        }

        if (record.BookSeries is null)
        {
            return NotAnswered<SeriesMember>(scope, budget, DiscoveryOutcome.Failed, DiscoveryCodes.ProviderError);
        }

        var notes = new List<string>();
        if (record.CanonicalId is > 0)
        {
            // The statement is kept; this call does not follow it (the series lookup does, within its own budget).
            notes.Add(DiscoveryCodes.SeriesCanonicalStated);
        }

        var now = time.GetUtcNow();
        var seriesRef = series.Id;
        var more = record.BookSeries.Count > DiscoveryPageSize;
        var seenBooks = new HashSet<int>();
        var members = new List<SeriesMember>();
        var rowsRead = Math.Min(record.BookSeries.Count, DiscoveryPageSize);
        var rowErrors = HardcoverRowErrors.Of(response.Errors, "series.0.book_series");
        for (var index = 0; index < rowsRead; index++)
        {
            var row = record.BookSeries[index];
            if (row is null || rowErrors.Failed(index))
            {
                // Credits, role, date or the book itself failed: the row is withheld, never kept with the failed field read as empty.
                continue;
            }

            var book = ToDiscoveredBook(row.Book, now);
            if (book is null)
            {
                notes.Add(DiscoveryCodes.ItemMissingId);
                continue;
            }

            if (!seenBooks.Add(row.Book!.Id!.Value))
            {
                notes.Add(DiscoveryCodes.DuplicateDropped);
                continue;
            }

            members.Add(new SeriesMember(book, seriesRef, [PositionOf(row.Position, row.Details, seriesRef)]));
        }

        budget.RecordItems(members.Count);
        return Page(members, scope, budget, more, offset + DiscoveryPageSize, hasFieldErrors, notes, rowsRead);
    }

    // ----------------------------------------------------------------------------------------------------- authors

    public async Task<DiscoveryResult<AuthorRef>> ResolveAuthorAsync(
        AuthorAnchor anchor, IDiscoveryBudget budget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        var scope = new DiscoveryScope(DiscoveryOperation.ResolveAuthor, anchor.Id, anchor.Kind.ToString().ToLowerInvariant());
        if (anchor.Kind == AuthorAnchorKind.SourceId && !TryNativeId(anchor.Id!, ExternalReferenceEntityType.Author, out _))
        {
            return Refused<AuthorRef>(scope, budget, DiscoveryCodes.SubjectInvalid);
        }

        using var work = budget.BeginWork();
        var ids = new List<int>();

        // A failed field is unknown, never absent. Anything that makes the answer uncertain is collected here and keeps the
        // result from selecting an author or certifying an empty scope.
        var searchUncertain = false;
        if (anchor.Kind == AuthorAnchorKind.SourceId)
        {
            TryNativeId(anchor.Id!, ExternalReferenceEntityType.Author, out var nativeId);
            ids.Add(nativeId);
        }
        else
        {
            var search = await HardcoverGraphQlClient.ExecuteDiscoveryAsync<SearchData>(
                httpClient, AuthorSearchQuery, new { query = anchor.Name }, budget, cancellationToken);
            if (!search.HasData)
            {
                return Stopped<AuthorRef>(scope, budget, search.Transport, search.Code);
            }

            if (search.Data!.Search?.Ids is not { } found)
            {
                // A missing search field is a failed or malformed answer, not "no matching author".
                return NotAnswered<AuthorRef>(
                    scope, budget, DiscoveryOutcome.Failed, search.Errors.Count > 0 ? DiscoveryCodes.PartialFieldError : DiscoveryCodes.ProviderError);
            }

            ids.AddRange(found.Where(id => id is > 0).Select(id => id!.Value).Distinct());
            searchUncertain = search.Errors.Count > 0;
            if (ids.Count == 0)
            {
                return searchUncertain
                    ? NotAnswered<AuthorRef>(scope, budget, DiscoveryOutcome.Failed, DiscoveryCodes.PartialFieldError)
                    : Empty<AuthorRef>(scope, budget);
            }
        }

        var anchorBooks = anchor.Books;
        var bookIds = anchorBooks
            .Where(book => book.Source is { } source && TryNativeId(source, ExternalReferenceEntityType.Work, out _))
            .Select(book =>
            {
                TryNativeId(book.Source!, ExternalReferenceEntityType.Work, out var id);
                return id;
            })
            .ToArray();
        var isbns = anchorBooks.Where(book => book.Isbn13 is not null).Select(book => book.Isbn13!).ToArray();

        var authors = await HardcoverGraphQlClient.ExecuteDiscoveryAsync<AuthorsDiscoveryData>(
            httpClient, AuthorsByIdQuery, new { ids, bookIds, isbns }, budget, cancellationToken);
        if (!authors.HasData)
        {
            return Stopped<AuthorRef>(scope, budget, authors.Transport, authors.Code);
        }

        if (authors.Data!.Authors is not { } records)
        {
            // data.authors = null means the field failed (or the answer is malformed); it is never an empty author list.
            return NotAnswered<AuthorRef>(
                scope, budget, DiscoveryOutcome.Failed, authors.Errors.Count > 0 ? DiscoveryCodes.PartialFieldError : DiscoveryCodes.ProviderError);
        }

        var rowErrors = HardcoverRowErrors.Of(authors.Errors, "authors");
        var wantedKey = anchor.Name is null ? null : CatalogIdentityPolicy.AuthorKey(anchor.Name);
        var needsCorroboration = anchor.Kind == AuthorAnchorKind.NameWithBooks;
        var identityUnknown = false;
        var corroborationUnknown = false;
        var candidates = new List<AuthorCandidate>();
        for (var index = 0; index < records.Count; index++)
        {
            var author = records[index];
            if (author is null || rowErrors.FieldFailed(index, "id") || rowErrors.FieldFailed(index, "name"))
            {
                // Who this row is could not be read: it might have been the author, so it is unknown rather than dropped.
                identityUnknown = true;
                continue;
            }

            if (author.Id is not > 0 || string.IsNullOrWhiteSpace(author.Name))
            {
                continue;
            }

            var reference = new AuthorRef(
                author.Name.Trim(),
                new SourceRef(ProviderKey, author.Id.Value.ToString(CultureInfo.InvariantCulture), ExternalReferenceEntityType.Author),
                WorkCount: rowErrors.FieldFailed(index, "books_count") ? null : author.BooksCount);
            var nameMatches = wantedKey is null || CatalogIdentityPolicy.AuthorKey(author.Name) == wantedKey;
            var corroborationFailed = rowErrors.FieldFailed(index, "contributions");
            corroborationUnknown |= corroborationFailed && needsCorroboration;
            candidates.Add(new AuthorCandidate(reference, nameMatches, !corroborationFailed && author.Contributions is { Count: > 0 }));
        }

        budget.RecordItems(candidates.Count);
        var observed = time.GetUtcNow();
        var hadErrors = authors.Errors.Count > 0 || searchUncertain;
        IReadOnlyList<string> notes = hadErrors ? [DiscoveryCodes.PartialFieldError] : [];

        if (anchor.Kind == AuthorAnchorKind.SourceId)
        {
            // The native id selects the record, so other fields and other rows cannot change who it is. If the record itself
            // could not be read, or errors may have hidden it, there is no answer rather than an empty one.
            var exact = candidates.Any(candidate => candidate.Author.Id == anchor.Id);
            return exact || (!identityUnknown && !rowErrors.Unattributed && !hadErrors)
                ? AuthorResolutionPolicy.Decide(anchor, candidates, scope, observed, budget.Usage, notes)
                : NotAnswered<AuthorRef>(scope, budget, DiscoveryOutcome.Failed, DiscoveryCodes.PartialFieldError);
        }

        var uncertain = identityUnknown || corroborationUnknown || searchUncertain || rowErrors.Unattributed;
        if (!uncertain)
        {
            return AuthorResolutionPolicy.Decide(anchor, candidates, scope, observed, budget.Usage, notes);
        }

        // Evidence for some candidate failed: keep the valid name matches as partial choices and never select one, because the
        // unreadable candidate might be the one that matches.
        var named = candidates.Where(candidate => candidate.NameMatchesExactly).Select(candidate => candidate.Author).ToArray();
        return named.Length == 0
            ? NotAnswered<AuthorRef>(scope, budget, DiscoveryOutcome.Failed, DiscoveryCodes.PartialFieldError)
            : DiscoveryResult.PartialSuccess(named, scope, observed, budget.Usage, [DiscoveryCodes.PartialFieldError, DiscoveryCodes.AnchorInsufficient]);
    }

    public async Task<DiscoveryResult<AuthorWork>> GetAuthorWorksAsync(
        AuthorRef author, DiscoveryCursor? cursor, IDiscoveryBudget budget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(author);
        var scope = new DiscoveryScope(DiscoveryOperation.GetAuthorWorks, author.Id, "author-role-non-merged-books", "book-id");
        if (!TryNativeId(author.Id, ExternalReferenceEntityType.Author, out var authorId))
        {
            return Refused<AuthorWork>(scope, budget, DiscoveryCodes.SubjectInvalid);
        }

        if (!TryOffset(cursor, out var offset))
        {
            return Refused<AuthorWork>(scope, budget, DiscoveryCodes.CursorInvalid);
        }

        if (!budget.TryAdmitPage())
        {
            return NotAnswered<AuthorWork>(scope, budget, DiscoveryOutcome.Failed, DiscoveryCodes.PageCeiling);
        }

        using var work = budget.BeginWork();
        var response = await HardcoverGraphQlClient.ExecuteDiscoveryAsync<AuthorsDiscoveryData>(
            httpClient, AuthorWorksQuery, new { id = authorId, limit = DiscoveryRowsRequested, offset }, budget, cancellationToken);
        if (!response.HasData)
        {
            return Stopped<AuthorWork>(scope, budget, response.Transport, response.Code);
        }

        var record = response.Data!.Authors is { Count: > 0 } found ? found[0] : null;
        var hasFieldErrors = response.Errors.Count > 0;
        if (record is null)
        {
            return hasFieldErrors
                ? NotAnswered<AuthorWork>(scope, budget, DiscoveryOutcome.Failed, DiscoveryCodes.PartialFieldError)
                : Empty<AuthorWork>(scope, budget);
        }

        if (record.Contributions is null)
        {
            return NotAnswered<AuthorWork>(scope, budget, DiscoveryOutcome.Failed, DiscoveryCodes.ProviderError);
        }

        var notes = new List<string>();
        var now = time.GetUtcNow();
        var more = record.Contributions.Count > DiscoveryPageSize;
        var seenBooks = new HashSet<int>();
        var works = new List<AuthorWork>();
        var rowsRead = Math.Min(record.Contributions.Count, DiscoveryPageSize);
        var rowErrors = HardcoverRowErrors.Of(response.Errors, "authors.0.contributions");
        for (var index = 0; index < rowsRead; index++)
        {
            var contribution = record.Contributions[index];
            if (contribution is null || rowErrors.Failed(index))
            {
                // A failed series, credit or date field is unknown; the work is withheld rather than kept as having none.
                continue;
            }

            var book = ToDiscoveredBook(contribution.Book, now);
            if (book is null)
            {
                notes.Add(DiscoveryCodes.ItemMissingId);
                continue;
            }

            if (!seenBooks.Add(contribution.Book!.Id!.Value))
            {
                notes.Add(DiscoveryCodes.DuplicateDropped);
                continue;
            }

            var claims = new List<BookSeriesClaim>();
            foreach (var entry in contribution.Book.BookSeries ?? [])
            {
                if (entry.Series is null || string.IsNullOrWhiteSpace(entry.Series.Name))
                {
                    continue;
                }

                var seriesRef = ToSeriesRef(entry.Series, new Dictionary<int, CanonicalProbe>(), notes);
                claims.Add(new BookSeriesClaim(seriesRef, PositionOf(entry.Position, entry.Details, seriesRef.Id)));
            }

            works.Add(new AuthorWork(book, claims));
        }

        budget.RecordItems(works.Count);
        return Page(works, scope, budget, more, offset + DiscoveryPageSize, hasFieldErrors, notes, rowsRead);
    }

    public async Task<DiscoveryResult<AuthorSeriesEntry>> GetAuthorSeriesAsync(
        AuthorRef author, DiscoveryCursor? cursor, IDiscoveryBudget budget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(author);
        // Hardcover's series.author_id is the series' primary author only, so this list is partial by construction.
        var scope = new DiscoveryScope(DiscoveryOperation.GetAuthorSeries, author.Id, "series-with-this-primary-author", "series-id");
        if (!TryNativeId(author.Id, ExternalReferenceEntityType.Author, out var authorId))
        {
            return Refused<AuthorSeriesEntry>(scope, budget, DiscoveryCodes.SubjectInvalid);
        }

        if (!TryOffset(cursor, out var offset))
        {
            return Refused<AuthorSeriesEntry>(scope, budget, DiscoveryCodes.CursorInvalid);
        }

        if (!budget.TryAdmitPage())
        {
            return NotAnswered<AuthorSeriesEntry>(scope, budget, DiscoveryOutcome.Failed, DiscoveryCodes.PageCeiling);
        }

        using var work = budget.BeginWork();
        var response = await HardcoverGraphQlClient.ExecuteDiscoveryAsync<SeriesDiscoveryData>(
            httpClient, AuthorSeriesQuery, new { id = authorId, limit = DiscoveryRowsRequested, offset }, budget, cancellationToken);
        if (!response.HasData)
        {
            return Stopped<AuthorSeriesEntry>(scope, budget, response.Transport, response.Code);
        }

        var rows = response.Data!.Series;
        var hasFieldErrors = response.Errors.Count > 0;
        if (rows is null)
        {
            return NotAnswered<AuthorSeriesEntry>(scope, budget, DiscoveryOutcome.Failed, DiscoveryCodes.ProviderError);
        }

        var notes = new List<string> { DiscoveryCodes.PrimaryAuthorOnly };
        var more = rows.Count > DiscoveryPageSize;
        var entries = new List<AuthorSeriesEntry>();
        var rowsRead = Math.Min(rows.Count, DiscoveryPageSize);
        var rowErrors = HardcoverRowErrors.Of(response.Errors, "series");
        for (var index = 0; index < rowsRead; index++)
        {
            var row = rows[index];
            if (row is not null && rowErrors.Failed(index))
            {
                continue;
            }

            if (row is null || string.IsNullOrWhiteSpace(row.Name))
            {
                notes.Add(DiscoveryCodes.ItemMissingId);
                continue;
            }

            entries.Add(new AuthorSeriesEntry(ToSeriesRef(row, new Dictionary<int, CanonicalProbe>(), notes), RelationshipEvidence.Native));
        }

        budget.RecordItems(entries.Count);
        return Page(entries, scope, budget, more, offset + DiscoveryPageSize, hasFieldErrors, notes, rowsRead);
    }

    // -------------------------------------------------------------------------------------------------- canonical

    private async Task<Dictionary<int, CanonicalProbe>> LookUpCanonicalAsync(
        IReadOnlyList<DiscSeries> rows, IDiscoveryBudget budget, CancellationToken cancellationToken)
    {
        var targets = rows
            .Where(row => row.CanonicalId is > 0)
            .Select(row => row.CanonicalId!.Value)
            .Distinct()
            .ToArray();
        var probes = new Dictionary<int, CanonicalProbe>();
        if (targets.Length == 0)
        {
            return probes;
        }

        // At most one extra request, charged to the same budget. Any failure leaves the statement unverified.
        var response = await HardcoverGraphQlClient.ExecuteDiscoveryAsync<SeriesDiscoveryData>(
            httpClient, CanonicalSeriesQuery, new { ids = targets }, budget, cancellationToken);
        if (!response.HasData || response.Errors.Count > 0)
        {
            return probes;
        }

        var found = (response.Data!.Series ?? []).Where(row => row.Id is > 0).ToDictionary(row => row.Id!.Value);
        foreach (var target in targets)
        {
            probes[target] = found.TryGetValue(target, out var row)
                ? new CanonicalProbe(CanonicalTargetLookup.Found, row.CanonicalId, row.Name?.Trim())
                : new CanonicalProbe(CanonicalTargetLookup.Missing, null, null);
        }

        return probes;
    }

    private readonly record struct CanonicalProbe(CanonicalTargetLookup Lookup, int? TargetsOwnCanonicalId, string? TargetLabel);

    private static SeriesRef ToSeriesRef(DiscSeries row, IReadOnlyDictionary<int, CanonicalProbe> probes, List<string> notes)
    {
        var name = row.Name!.Trim();
        SourceRef? id = row.Id is > 0
            ? new SourceRef(ProviderKey, row.Id.Value.ToString(CultureInfo.InvariantCulture), ExternalReferenceEntityType.Series)
            : null;
        if (id is null)
        {
            notes.Add(DiscoveryCodes.NoNativeSeriesId);
        }

        CanonicalSeriesLink? canonical = null;
        if (id is not null && row.CanonicalId is > 0)
        {
            var target = new SourceRef(ProviderKey, row.CanonicalId.Value.ToString(CultureInfo.InvariantCulture), ExternalReferenceEntityType.Series);
            var probe = probes.TryGetValue(row.CanonicalId.Value, out var found) ? found : new CanonicalProbe(CanonicalTargetLookup.NotAttempted, null, null);
            SourceRef? ownCanonical = probe.TargetsOwnCanonicalId is > 0
                ? new SourceRef(ProviderKey, probe.TargetsOwnCanonicalId.Value.ToString(CultureInfo.InvariantCulture), ExternalReferenceEntityType.Series)
                : null;
            // Hardcover's published series schema defines canonical_id as "Canonical series if duplicate".
            canonical = CanonicalSeriesLink.Evaluate(id, target, semanticsVerified: true, probe.Lookup, ownCanonical, probe.TargetLabel);
            if (canonical.State != CanonicalLinkState.Verified)
            {
                notes.Add(canonical.State == CanonicalLinkState.Invalid
                    ? canonical.ReasonCode!
                    : DiscoveryCodes.CanonicalTargetNotChecked);
            }
        }

        return new SeriesRef(name, id, row.IsCompleted, row.PrimaryBooksCount, row.BooksCount, canonical);
    }

    // ----------------------------------------------------------------------------------------------- shared mapping

    private static DiscoveredBook? ToDiscoveredBook(DiscBook? book, DateTimeOffset observedAtUtc)
    {
        if (book?.Id is not > 0 || string.IsNullOrWhiteSpace(book.Title))
        {
            return null;
        }

        var credits = new List<CreditRef>();
        var contributions = book.Contributions ?? [];
        for (var ordinal = 0; ordinal < contributions.Count; ordinal++)
        {
            var author = contributions[ordinal].Author;
            if (string.IsNullOrWhiteSpace(author?.Name))
            {
                continue;
            }

            var role = string.IsNullOrWhiteSpace(contributions[ordinal].Role?.Slug) ? null : contributions[ordinal].Role!.Slug!.Trim();
            credits.Add(new CreditRef(ordinal, author.Name.Trim(), role, AuthorRefOf(author.Id)));
        }

        var released = PublicationDateParser.Parse(book.ReleaseDate, DateFormats);
        // What Hardcover's release_date means (original or one edition) is unverified, so the basis stays Unknown.
        var publication = released is { } date
            ? PublicationEvidence.Dated(date.Date, date.Precision, DateBasis.Unknown)
            : PublicationEvidence.NotStated;

        BookCandidateVersion? version = null;
        if (book.Compilation == true)
        {
            version = new BookCandidateVersion("Collection", "Collection or combined volume", "The source marks this record as a compilation.");
        }
        else if (book.IsPartialBook == true)
        {
            version = new BookCandidateVersion("Partial", "Partial record", "The source marks this record as a partial book.");
        }

        return new DiscoveredBook(
            new SourceRef(ProviderKey, book.Id.Value.ToString(CultureInfo.InvariantCulture), ExternalReferenceEntityType.Work),
            new TitleForm(book.Title.Trim()),
            credits,
            publication,
            observedAtUtc,
            version: version);
    }

    private static PositionClaim PositionOf(double? position, string? details, SourceRef? series)
    {
        var label = !string.IsNullOrWhiteSpace(details) ? details.Trim() : FormatPositionLabel(position);
        return new PositionClaim(label, position is null ? null : (decimal)position.Value, null, series);
    }

    private static bool TryNativeId(SourceRef reference, ExternalReferenceEntityType kind, out int id)
    {
        id = 0;
        return string.Equals(reference.ProviderId, ProviderKey, StringComparison.Ordinal) &&
            reference.Kind == kind &&
            int.TryParse(reference.ExternalId, NumberStyles.None, CultureInfo.InvariantCulture, out id) &&
            id > 0;
    }

    private static bool TryOffset(DiscoveryCursor? cursor, out int offset)
    {
        offset = 0;
        return cursor is null ||
            (cursor.Value.StartsWith("o:", StringComparison.Ordinal) &&
                int.TryParse(cursor.Value.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out offset));
    }

    private static string[] Distinct(IEnumerable<string> notes) => notes.Distinct(StringComparer.Ordinal).ToArray();

    // ------------------------------------------------------------------------------------------- result building

    private DiscoveryResult<T> Page<T>(
        List<T> items,
        DiscoveryScope scope,
        IDiscoveryBudget budget,
        bool more,
        int nextOffset,
        bool hasFieldErrors,
        List<string> notes,
        int rowsRead)
    {
        var observed = time.GetUtcNow();
        if (hasFieldErrors)
        {
            notes.Add(DiscoveryCodes.PartialFieldError);
        }

        var codes = Distinct(notes);
        if (hasFieldErrors)
        {
            // Valid items are kept; the failed fields are never read as absent, and no continuation is offered past a gap.
            return items.Count == 0
                ? NotAnswered<T>(scope, budget, DiscoveryOutcome.Failed, DiscoveryCodes.PartialFieldError)
                : DiscoveryResult.PartialSuccess(items, scope, observed, budget.Usage, codes);
        }

        if (items.Count == 0)
        {
            // Rows the provider returned but none usable are not an empty scope, and a full page of them still has a next page.
            // Only a page with no source rows at all, and nothing after it, is verified empty.
            if (rowsRead == 0 && !more)
            {
                return DiscoveryResult.Empty<T>(scope, observed, budget.Usage, codes);
            }

            var reasons = codes.Length == 0 ? [DiscoveryCodes.ItemMissingId] : codes;
            return more
                ? DiscoveryResult.NoUsableItems<T>(scope, DiscoveryCompletion.MoreAvailable, new DiscoveryCursor("o:" + nextOffset.ToString(CultureInfo.InvariantCulture)), observed, budget.Usage, reasons)
                : DiscoveryResult.NoUsableItems<T>(scope, DiscoveryCompletion.ExhaustedInCheckedScope, null, observed, budget.Usage, reasons);
        }

        return more
            ? DiscoveryResult.Found(items, scope, DiscoveryCompletion.MoreAvailable, observed, budget.Usage, new DiscoveryCursor("o:" + nextOffset.ToString(CultureInfo.InvariantCulture)), codes)
            : DiscoveryResult.Found(items, scope, DiscoveryCompletion.ExhaustedInCheckedScope, observed, budget.Usage, notes: codes);
    }

    private DiscoveryResult<T> Empty<T>(DiscoveryScope scope, IDiscoveryBudget budget, IReadOnlyList<string>? notes = null) =>
        DiscoveryResult.Empty<T>(scope, time.GetUtcNow(), budget.Usage, notes);

    // A request refused before any provider call: nothing was dispatched, so usage is whatever the budget already holds.
    private DiscoveryResult<T> Refused<T>(DiscoveryScope scope, IDiscoveryBudget budget, string code) =>
        NotAnswered<T>(scope, budget, DiscoveryOutcome.Failed, code);

    private DiscoveryResult<T> NotAnswered<T>(DiscoveryScope scope, IDiscoveryBudget budget, DiscoveryOutcome outcome, string code) =>
        DiscoveryResult.NotAnswered<T>(outcome, scope, time.GetUtcNow(), budget.Usage, [code]);

    private DiscoveryResult<T> Stopped<T>(DiscoveryScope scope, IDiscoveryBudget budget, HardcoverTransport transport, string? reason)
    {
        var (outcome, code) = transport switch
        {
            HardcoverTransport.Throttled => (DiscoveryOutcome.Throttled, reason ?? DiscoveryCodes.Throttled),
            HardcoverTransport.TimedOut => (DiscoveryOutcome.TimedOut, DiscoveryCodes.DeadlineReached),
            HardcoverTransport.AuthorizationDenied or HardcoverTransport.SetupMissing => (DiscoveryOutcome.Unavailable, reason ?? DiscoveryCodes.AuthorizationDenied),
            _ => (DiscoveryOutcome.Failed, reason ?? DiscoveryCodes.ProviderError)
        };
        return NotAnswered<T>(scope, budget, outcome, code);
    }

    // ------------------------------------------------------------------------------------------------------ models

    private sealed class SeriesDiscoveryData
    {
        [JsonPropertyName("series")]
        public IReadOnlyList<DiscSeries>? Series { get; init; }
    }

    private sealed class BookSeriesDiscoveryData
    {
        [JsonPropertyName("books_by_pk")]
        public DiscBook? Book { get; init; }
    }

    private sealed class AuthorsDiscoveryData
    {
        [JsonPropertyName("authors")]
        public IReadOnlyList<DiscAuthor>? Authors { get; init; }
    }

    private sealed class DiscSeries
    {
        [JsonPropertyName("id")]
        public int? Id { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("is_completed")]
        public bool? IsCompleted { get; init; }

        [JsonPropertyName("books_count")]
        public int? BooksCount { get; init; }

        [JsonPropertyName("primary_books_count")]
        public int? PrimaryBooksCount { get; init; }

        [JsonPropertyName("canonical_id")]
        public int? CanonicalId { get; init; }

        [JsonPropertyName("book_series")]
        public IReadOnlyList<DiscBookSeries>? BookSeries { get; init; }
    }

    private sealed class DiscBookSeries
    {
        [JsonPropertyName("position")]
        public double? Position { get; init; }

        [JsonPropertyName("details")]
        public string? Details { get; init; }

        [JsonPropertyName("series")]
        public DiscSeries? Series { get; init; }

        [JsonPropertyName("book")]
        public DiscBook? Book { get; init; }
    }

    private sealed class DiscBook
    {
        [JsonPropertyName("id")]
        public int? Id { get; init; }

        [JsonPropertyName("title")]
        public string? Title { get; init; }

        [JsonPropertyName("release_date")]
        public string? ReleaseDate { get; init; }

        [JsonPropertyName("compilation")]
        public bool? Compilation { get; init; }

        [JsonPropertyName("is_partial_book")]
        public bool? IsPartialBook { get; init; }

        [JsonPropertyName("contributions")]
        public IReadOnlyList<DiscContribution>? Contributions { get; init; }

        [JsonPropertyName("book_series")]
        public IReadOnlyList<DiscBookSeries>? BookSeries { get; init; }
    }

    private sealed class DiscContribution
    {
        [JsonPropertyName("contributor_role")]
        public DiscRole? Role { get; init; }

        [JsonPropertyName("author")]
        public HardcoverAuthor? Author { get; init; }

        [JsonPropertyName("book")]
        public DiscBook? Book { get; init; }
    }

    private sealed class DiscRole
    {
        [JsonPropertyName("slug")]
        public string? Slug { get; init; }
    }

    private sealed class DiscAuthor
    {
        [JsonPropertyName("id")]
        public int? Id { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("books_count")]
        public int? BooksCount { get; init; }

        [JsonPropertyName("contributions")]
        public IReadOnlyList<DiscContribution>? Contributions { get; init; }
    }
}
