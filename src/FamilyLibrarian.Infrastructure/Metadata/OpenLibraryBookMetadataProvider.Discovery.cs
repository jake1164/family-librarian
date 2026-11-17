using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using FamilyLibrarian.Application.Catalog.Discovery;
using FamilyLibrarian.Domain.Catalog;
using CatalogIdentityPolicy = FamilyLibrarian.Application.Catalog.Identity.CatalogIdentityPolicy;

namespace FamilyLibrarian.Infrastructure.Metadata;

/// <summary>
/// Read-only author/series discovery on the same Open Library provider instance and handler pipeline (so the same request gate
/// paces it) as search/detail.
/// </summary>
/// <remarks>
/// <para>
/// Every selection is by an Open Library key validated as <c>OL…A</c> / <c>OL…L</c> / <c>OL…W</c> before it reaches a URL: an
/// <c>author=</c> value that looks like an author key becomes an exact <c>author_key</c> filter on Open Library's side (first-party
/// <c>works.py</c>), and anything else would fall back to a name search, so a malformed id is refused rather than sent. Each
/// returned row is checked to carry the requested key again, so a changed filter cannot silently widen a scope.
/// </para>
/// <para>
/// Evidence behind the shapes used here (a bounded set of unauthenticated public GETs on 2026-11-17, plus one captured lab probe):
/// <c>search.json?q=series_key:OL…L</c> and <c>search.json?author=OL…A</c> filter exactly and return parallel
/// <c>author_key/author_name</c> and <c>series_key/series_name/series_position</c> arrays; <c>search/authors.json</c> returns
/// <c>key, name, work_count, top_work, birth_date, death_date</c>; <c>/authors/OL…A.json</c> returns a record with <c>name</c>.
/// <c>first_publish_date</c> was <b>not</b> returned for the sampled rows even when requested; <c>first_publish_year</c> was.
/// The <c>/series/OL…L/seeds.json</c> route is <b>not used</b>: its response keys are unverified and the first-party model caps
/// it at 100 works with no pagination.
/// </para>
/// <para>
/// Open Library has no retry in this pipeline, so a logical request is exactly one physical attempt; the attempt is admitted by
/// the journey budget before dispatch and the request gate's pacing time counts as provider work. A 429 is reported as throttled
/// and never retried here.
/// </para>
/// </remarks>
public sealed partial class OpenLibraryBookMetadataProvider
{
    private const int DiscoveryPageSize = 50;
    private const int MaximumPage = 10_000;
    private const int MaximumAuthorCandidates = 10;
    private const int MaximumAnchorParts = 5;
    private const int AnchorRowLimit = MaximumAnchorParts * 2;
    private const int MaximumBodyBytes = 2 * 1024 * 1024;
    private const string ProviderKey = "openlibrary";

    // Local codes of the shared stable-code shape; the shared list is not extended for one adapter.
    private const string SeriesArraysMismatch = "series-arrays-mismatch";
    private const string SeriesKeyInvalid = "series-key-invalid";
    private const string SeriesLabelMissing = "series-label-missing";
    private const string CreditArraysMismatch = "credit-arrays-mismatch";
    private const string SeriesAnchorMismatch = "series-anchor-mismatch";
    private const string AuthorAnchorMismatch = "author-anchor-mismatch";
    private const string BookAnchorMismatch = "book-anchor-mismatch";
    private const string SeriesLabelConflict = "series-label-conflict";
    private const string AuthorRedirectUnfollowed = "author-redirect-unfollowed";
    private const string AnchorLookupFailed = "anchor-lookup-failed";
    private const string AnchorEvidenceTruncated = "anchor-evidence-truncated";
    private const string AnchorEvidenceUnreadable = "anchor-evidence-unreadable";
    private const string AnchorRowUnmatched = "anchor-row-unmatched";
    private const string ResponseTooLarge = "response-too-large";
    private const string DerivedFromAuthorWorks = "derived-from-author-works";
    private const string NoSeriesClaimsOnPage = "no-series-claims-on-page";
    private const string DerivedScope = "author-works-checked-pages";

    private const string DiscoveryWorkFields =
        "key,title,author_key,author_name,first_publish_year,series_key,series_name,series_position";

    private const string SeriesOnlyFields = "key,author_key,series_key,series_name,series_position";

    private static readonly DiscoveryCapabilities DiscoveryCapabilityTable = new(
    [
        new(DiscoveryOperation.FindSeriesForBook, DiscoveryCapability.Supported),
        new(DiscoveryOperation.GetSeriesMembers, DiscoveryCapability.Supported),
        new(DiscoveryOperation.ResolveAuthor, DiscoveryCapability.Supported),
        new(DiscoveryOperation.GetAuthorWorks, DiscoveryCapability.Supported),
        new(DiscoveryOperation.GetAuthorSeries, DiscoveryCapability.Supported)
    ]);

    private readonly TimeProvider time = clock ?? TimeProvider.System;

    string IBookMetadataDiscoveryProvider.ProviderId => Id;

    DiscoveryCapabilities IBookMetadataDiscoveryProvider.Capabilities => DiscoveryCapabilityTable;

    // ------------------------------------------------------------------------------------------------ book -> series

    /// <summary>The series-only view of <see cref="FindSeriesClaimsForBookAsync"/>: the same single read, projected, never fetched twice.</summary>
    public async Task<DiscoveryResult<SeriesRef>> FindSeriesForBookAsync(
        SourceRef book, IDiscoveryBudget budget, CancellationToken cancellationToken) =>
        DiscoveryResultProjection.SeriesOnly(await FindSeriesClaimsForBookAsync(book, budget, cancellationToken));

    /// <summary>One <c>search.json</c> row for the work: each series claim with the work's own position in it.</summary>
    public async Task<DiscoveryResult<BookSeriesClaim>> FindSeriesClaimsForBookAsync(
        SourceRef book, IDiscoveryBudget budget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(book);
        var scope = new DiscoveryScope(DiscoveryOperation.FindSeriesForBook, book, "series-claims-in-work-search-index");
        if (!TryNativeId(book, ExternalReferenceEntityType.Work, 'W', out var workId))
        {
            return Refused<BookSeriesClaim>(scope, budget, DiscoveryCodes.SubjectInvalid);
        }

        using var work = budget.BeginWork();
        var reply = await GetAsync<OlDiscoverySearch>(
            SearchUri($"key:/works/{workId}", DiscoveryWorkFields, 1, 1), budget, cancellationToken);
        if (!reply.HasData)
        {
            return Stopped<BookSeriesClaim>(scope, budget, reply.Transport, reply.Code);
        }

        if (reply.Data!.Documents is not { } documents)
        {
            return NotAnswered<BookSeriesClaim>(scope, budget, DiscoveryOutcome.Failed, DiscoveryCodes.ProviderError);
        }

        if (documents.Count == 0)
        {
            return Empty<BookSeriesClaim>(scope, budget);
        }

        var document = documents.FirstOrDefault(candidate => string.Equals(candidate?.Key, "/works/" + workId, StringComparison.Ordinal));
        if (document is null)
        {
            // The filter is documented as a key lookup; rows for a different work are never read as this work's claims.
            return NoUsable<BookSeriesClaim>(scope, budget, DiscoveryCompletion.ExhaustedInCheckedScope, null, [BookAnchorMismatch]);
        }

        var series = ReadSeries(document);
        if (series.Problem is not null)
        {
            return NoUsable<BookSeriesClaim>(scope, budget, DiscoveryCompletion.ExhaustedInCheckedScope, null, [series.Problem]);
        }

        if (series.Claims.Count == 0)
        {
            return Empty<BookSeriesClaim>(scope, budget);
        }

        var notes = new List<string>();
        // Each claim keeps its own position text and number; a series listed twice with different numbers keeps both.
        var items = series.Claims.Select(claim => new BookSeriesClaim(ToSeriesRef(claim, notes), PositionOf(claim))).ToArray();
        budget.RecordItems(items.Length);
        return DiscoveryResult.Found(items, scope, DiscoveryCompletion.ExhaustedInCheckedScope, time.GetUtcNow(), budget.Usage, notes: Distinct(notes));
    }

    // ---------------------------------------------------------------------------------------------- series -> members

    public async Task<DiscoveryResult<SeriesMember>> GetSeriesMembersAsync(
        SeriesRef series, DiscoveryCursor? cursor, IDiscoveryBudget budget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(series);
        var scope = new DiscoveryScope(DiscoveryOperation.GetSeriesMembers, series.Id, "works-indexed-with-this-series-key", "provider-index-order");
        if (series.Id is null || !TryNativeId(series.Id, ExternalReferenceEntityType.Series, 'L', out var seriesKey))
        {
            // A label, a derived group or another provider's id is never enough to pick a series here.
            return Refused<SeriesMember>(scope, budget, DiscoveryCodes.SubjectInvalid);
        }

        if (!TryPage(cursor, out var page))
        {
            return Refused<SeriesMember>(scope, budget, DiscoveryCodes.CursorInvalid);
        }

        if (!budget.TryAdmitPage())
        {
            return NotAnswered<SeriesMember>(scope, budget, DiscoveryOutcome.Failed, DiscoveryCodes.PageCeiling);
        }

        using var work = budget.BeginWork();
        var reply = await GetAsync<OlDiscoverySearch>(
            SearchUri($"series_key:{seriesKey}", DiscoveryWorkFields, DiscoveryPageSize, page), budget, cancellationToken);
        if (!reply.HasData)
        {
            return Stopped<SeriesMember>(scope, budget, reply.Transport, reply.Code);
        }

        if (reply.Data!.Documents is not { } documents)
        {
            return NotAnswered<SeriesMember>(scope, budget, DiscoveryOutcome.Failed, DiscoveryCodes.ProviderError);
        }

        var notes = new List<string>();
        var now = time.GetUtcNow();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var members = new List<SeriesMember>();
        foreach (var document in documents)
        {
            if (document is null)
            {
                // A slot the provider sent that cannot be read still consumed a source row; it is never silently dropped.
                notes.Add(DiscoveryCodes.ItemMissingId);
                continue;
            }

            var workId = GetWorkId(document.Key);
            if (workId is null || string.IsNullOrWhiteSpace(document.Title))
            {
                notes.Add(DiscoveryCodes.ItemMissingId);
                continue;
            }

            var claims = ReadSeries(document);
            if (claims.Problem is not null)
            {
                notes.Add(claims.Problem);
                continue;
            }

            var own = claims.Claims.Where(claim => string.Equals(claim.Key, seriesKey, StringComparison.Ordinal)).ToArray();
            if (own.Length == 0)
            {
                notes.Add(SeriesAnchorMismatch);
                continue;
            }

            if (!seen.Add(workId))
            {
                notes.Add(DiscoveryCodes.DuplicateDropped);
                continue;
            }

            // Every claim this work makes for the requested series stays: a work listed twice with different positions is a
            // conflict to show, not one to settle.
            var positions = own.Select(claim => new PositionClaim(claim.Position, claim.Sort, null, series.Id)).ToArray();
            members.Add(new SeriesMember(ToDiscoveredBook(document, workId, claims.Claims, now, notes), series.Id, positions));
        }

        budget.RecordItems(members.Count);
        return Page(members, scope, budget, documents.Count, reply.Data, page, notes);
    }

    // ----------------------------------------------------------------------------------------------------- authors

    public async Task<DiscoveryResult<AuthorRef>> ResolveAuthorAsync(
        AuthorAnchor anchor, IDiscoveryBudget budget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        var scope = new DiscoveryScope(DiscoveryOperation.ResolveAuthor, anchor.Id, anchor.Kind.ToString().ToLowerInvariant());
        if (anchor.Kind == AuthorAnchorKind.SourceId)
        {
            if (!TryNativeId(anchor.Id!, ExternalReferenceEntityType.Author, 'A', out var nativeId))
            {
                return Refused<AuthorRef>(scope, budget, DiscoveryCodes.SubjectInvalid);
            }

            return await ResolveByRecordAsync(anchor, nativeId, scope, budget, cancellationToken);
        }

        using var work = budget.BeginWork();
        var search = await GetAsync<OlAuthorSearch>(
            $"search/authors.json?q={Uri.EscapeDataString(anchor.Name!)}&limit={MaximumAuthorCandidates.ToString(CultureInfo.InvariantCulture)}",
            budget, cancellationToken);
        if (!search.HasData)
        {
            return Stopped<AuthorRef>(scope, budget, search.Transport, search.Code);
        }

        if (search.Data!.Documents is not { } documents)
        {
            return NotAnswered<AuthorRef>(scope, budget, DiscoveryOutcome.Failed, DiscoveryCodes.ProviderError);
        }

        // Only the first page of hits is read, so "no exact name" and "these are all the namesakes" are claims about that page.
        var truncated = search.Data.NumberFound is { } found && found > documents.Count;
        IReadOnlyList<string> carried = truncated ? [DiscoveryCodes.ListTruncated] : [];
        var wanted = CatalogIdentityPolicy.AuthorKey(anchor.Name!);
        var named = new List<(AuthorRef Author, string Key)>();
        var identityUnknown = false;
        foreach (var document in documents)
        {
            // A row that cannot be read, or whose name matches but whose key is unusable, might have been the author: it is
            // unknown, not absent, so it may neither certify an empty scope nor let another namesake be selected.
            if (document is null || string.IsNullOrWhiteSpace(document.Name))
            {
                identityUnknown = true;
                continue;
            }

            // Only the primary name counts as the same name: an alias is evidence for a person to read, not equality.
            if (CatalogIdentityPolicy.AuthorKey(document.Name) != wanted)
            {
                continue;
            }

            if (!IsOlId(document.Key, 'A'))
            {
                identityUnknown = true;
                continue;
            }

            named.Add((new AuthorRef(
                document.Name.Trim(),
                new SourceRef(ProviderKey, document.Key!, ExternalReferenceEntityType.Author),
                LifeSpan(document.BirthDate, document.DeathDate),
                document.WorkCount), document.Key!));
        }

        budget.RecordItems(named.Count);
        var observed = time.GetUtcNow();
        if (identityUnknown)
        {
            carried = [.. carried, DiscoveryCodes.ItemMissingId];
        }

        if (named.Count == 0)
        {
            return identityUnknown
                ? NoUsable<AuthorRef>(scope, budget, DiscoveryCompletion.ExhaustedInCheckedScope, null, carried)
                : Empty<AuthorRef>(scope, budget, carried);
        }

        var choices = named.Select(candidate => candidate.Author).ToArray();
        if (identityUnknown)
        {
            // The unreadable row might be the person the caller meant, so none of the readable ones may be selected, and the
            // anchor request that could only pick between them is not spent.
            return DiscoveryResult.PartialSuccess(
                choices, scope, observed, budget.Usage, [.. carried, DiscoveryCodes.AnchorInsufficient]);
        }

        var corroborated = new HashSet<string>(StringComparer.Ordinal);
        if (anchor.Kind == AuthorAnchorKind.NameWithBooks && AnchorSearchOf(anchor) is { } anchorSearch)
        {
            // One request regardless of how many namesakes. Only rows that are demonstrably the admitted anchors (their work key
            // or their listed ISBN) vouch, and only when the page that carried them is whole.
            var vouching = await GetAsync<OlDiscoverySearch>(
                SearchUri(anchorSearch.Query, "key,author_key,isbn", AnchorRowLimit, 1), budget, cancellationToken);
            if (!vouching.HasData || vouching.Data!.Documents is not { } books)
            {
                // Evidence for some candidate could not be read: keep the name matches as choices and never select one.
                return DiscoveryResult.PartialSuccess(
                    choices, scope, time.GetUtcNow(), budget.Usage, [.. carried, AnchorLookupFailed, DiscoveryCodes.AnchorInsufficient]);
            }

            var evidence = ReadAnchorEvidence(anchorSearch, books, vouching.Data.NumberFound);
            if (evidence.Problems.Count > 0)
            {
                return DiscoveryResult.PartialSuccess(
                    choices, scope, time.GetUtcNow(), budget.Usage, [.. carried, .. evidence.Problems, DiscoveryCodes.AnchorInsufficient]);
            }

            corroborated = evidence.AuthorKeys;
        }

        var candidates = named
            .Select(candidate => new AuthorCandidate(candidate.Author, NameMatchesExactly: true, corroborated.Contains(candidate.Key)))
            .ToArray();
        return AuthorResolutionPolicy.Decide(anchor, candidates, scope, observed, budget.Usage, carried);
    }

    // What the single anchor request asked for, so the rows that come back can be checked against it.
    private sealed record AnchorSearch(string Query, HashSet<string> WorkKeys, HashSet<string> Isbns);

    private readonly record struct AnchorEvidence(HashSet<string> AuthorKeys, IReadOnlyList<string> Problems);

    // The anchor books reachable here: Open Library works by key and any ISBN-13 (at most MaximumAnchorParts in all). Another
    // provider's id cannot vouch. The same sets that build the query are what its rows are validated against.
    private static AnchorSearch? AnchorSearchOf(AuthorAnchor anchor)
    {
        var workKeys = new HashSet<string>(StringComparer.Ordinal);
        var isbns = new HashSet<string>(StringComparer.Ordinal);
        var parts = new List<string>();
        foreach (var book in anchor.Books)
        {
            if (parts.Count >= MaximumAnchorParts)
            {
                break;
            }

            if (book.Source is { } source && TryNativeId(source, ExternalReferenceEntityType.Work, 'W', out var workId))
            {
                if (workKeys.Add("/works/" + workId))
                {
                    parts.Add($"key:/works/{workId}");
                }
            }
            else if (book.Isbn13 is not null && isbns.Add(book.Isbn13))
            {
                parts.Add($"isbn:{book.Isbn13}");
            }
        }

        return parts.Count == 0 ? null : new AnchorSearch(string.Join(" OR ", parts), workKeys, isbns);
    }

    // A row vouches only if it is demonstrably an admitted anchor: its key is a requested work, or its isbn list (in either ISBN
    // form) holds a requested ISBN-13. A row that is neither is an anomaly in a lookup that asked for nothing else. Anything that
    // could hide a different namesake - rows beyond the page, an unreadable row, a matched row with no readable credit - makes the
    // whole evidence uncertain, so it can keep alternatives but can never certify a unique author.
    private static AnchorEvidence ReadAnchorEvidence(AnchorSearch search, IReadOnlyList<OlDiscoveryDocument?> rows, int? numberFound)
    {
        var problems = new List<string>();
        var truncated = numberFound is { } total ? total > rows.Count : rows.Count >= AnchorRowLimit;
        if (truncated)
        {
            problems.Add(AnchorEvidenceTruncated);
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        var unreadable = false;
        var unmatched = false;
        foreach (var row in rows)
        {
            if (row is null)
            {
                unreadable = true;
                continue;
            }

            if (!MatchesAnchor(search, row))
            {
                unmatched = true;
                continue;
            }

            var credited = Aligned(row.AuthorKeys).Where(key => IsOlId(key, 'A')).ToArray();
            if (credited.Length == 0)
            {
                unreadable = true;
                continue;
            }

            foreach (var key in credited)
            {
                keys.Add(key!);
            }
        }

        if (unreadable)
        {
            problems.Add(AnchorEvidenceUnreadable);
        }

        if (unmatched)
        {
            problems.Add(AnchorRowUnmatched);
        }

        return new AnchorEvidence(keys, problems);
    }

    private static bool MatchesAnchor(AnchorSearch search, OlDiscoveryDocument row)
    {
        if (row.Key is not null && search.WorkKeys.Contains(row.Key))
        {
            return true;
        }

        foreach (var listed in Aligned(row.Isbns))
        {
            if (listed is not null && IsbnNormalizer.TryNormalizeToIsbn13(listed, out var isbn13) && search.Isbns.Contains(isbn13))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<DiscoveryResult<AuthorRef>> ResolveByRecordAsync(
        AuthorAnchor anchor, string authorId, DiscoveryScope scope, IDiscoveryBudget budget, CancellationToken cancellationToken)
    {
        using var work = budget.BeginWork();
        var reply = await GetAsync<OlAuthorRecord>($"authors/{authorId}.json", budget, cancellationToken);
        if (reply.Transport == OpenLibraryTransport.NotFound)
        {
            return Empty<AuthorRef>(scope, budget);
        }

        if (!reply.HasData)
        {
            return Stopped<AuthorRef>(scope, budget, reply.Transport, reply.Code);
        }

        var record = reply.Data!;
        switch (record.Type?.Key)
        {
            case "/type/redirect":
                // A merged author points at another key. Following it would spend an unplanned attempt and assert an alias this
                // read-only preview may not claim, so the statement is reported and left unfollowed.
                return NotAnswered<AuthorRef>(scope, budget, DiscoveryOutcome.Unverified, AuthorRedirectUnfollowed);
            case "/type/delete":
                return Empty<AuthorRef>(scope, budget);
        }

        if (!string.Equals(record.Key, "/authors/" + authorId, StringComparison.Ordinal))
        {
            return NoUsable<AuthorRef>(scope, budget, DiscoveryCompletion.ExhaustedInCheckedScope, null, [AuthorAnchorMismatch]);
        }

        if (string.IsNullOrWhiteSpace(record.Name))
        {
            return NoUsable<AuthorRef>(scope, budget, DiscoveryCompletion.ExhaustedInCheckedScope, null, [DiscoveryCodes.ItemMissingId]);
        }

        var author = new AuthorRef(
            record.Name.Trim(), new SourceRef(ProviderKey, authorId, ExternalReferenceEntityType.Author), LifeSpan(record.BirthDate, record.DeathDate));
        budget.RecordItems(1);
        return AuthorResolutionPolicy.Decide(
            anchor, [new AuthorCandidate(author, NameMatchesExactly: true, CorroboratedByAnchor: false)], scope, time.GetUtcNow(), budget.Usage);
    }

    public async Task<DiscoveryResult<AuthorWork>> GetAuthorWorksAsync(
        AuthorRef author, DiscoveryCursor? cursor, IDiscoveryBudget budget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(author);
        var scope = new DiscoveryScope(DiscoveryOperation.GetAuthorWorks, author.Id, "works-indexed-with-this-author-key", "provider-index-order");
        if (!TryNativeId(author.Id, ExternalReferenceEntityType.Author, 'A', out var authorId))
        {
            return Refused<AuthorWork>(scope, budget, DiscoveryCodes.SubjectInvalid);
        }

        if (!TryPage(cursor, out var page))
        {
            return Refused<AuthorWork>(scope, budget, DiscoveryCodes.CursorInvalid);
        }

        if (!budget.TryAdmitPage())
        {
            return NotAnswered<AuthorWork>(scope, budget, DiscoveryOutcome.Failed, DiscoveryCodes.PageCeiling);
        }

        using var work = budget.BeginWork();
        var reply = await GetAsync<OlDiscoverySearch>(AuthorWorksUri(authorId, DiscoveryWorkFields, page), budget, cancellationToken);
        if (!reply.HasData)
        {
            return Stopped<AuthorWork>(scope, budget, reply.Transport, reply.Code);
        }

        if (reply.Data!.Documents is not { } documents)
        {
            return NotAnswered<AuthorWork>(scope, budget, DiscoveryOutcome.Failed, DiscoveryCodes.ProviderError);
        }

        var notes = new List<string>();
        var now = time.GetUtcNow();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var works = new List<AuthorWork>();
        foreach (var document in documents)
        {
            if (document is null)
            {
                // A slot the provider sent that cannot be read still consumed a source row; it is never silently dropped.
                notes.Add(DiscoveryCodes.ItemMissingId);
                continue;
            }

            var workId = GetWorkId(document.Key);
            if (workId is null || string.IsNullOrWhiteSpace(document.Title))
            {
                notes.Add(DiscoveryCodes.ItemMissingId);
                continue;
            }

            if (!Aligned(document.AuthorKeys).Contains(authorId))
            {
                notes.Add(AuthorAnchorMismatch);
                continue;
            }

            // A work whose series arrays cannot be paired is withheld: keeping it would read as "no series".
            var claims = ReadSeries(document);
            if (claims.Problem is not null)
            {
                notes.Add(claims.Problem);
                continue;
            }

            if (!seen.Add(workId))
            {
                notes.Add(DiscoveryCodes.DuplicateDropped);
                continue;
            }

            var seriesClaims = claims.Claims
                .Select(claim => new BookSeriesClaim(ToSeriesRef(claim, notes), PositionOf(claim)))
                .ToArray();
            works.Add(new AuthorWork(ToDiscoveredBook(document, workId, claims.Claims, now, notes), seriesClaims));
        }

        budget.RecordItems(works.Count);
        return Page(works, scope, budget, documents.Count, reply.Data, page, notes);
    }

    /// <summary>
    /// Series reached from an author, <b>derived</b> from the series claims on one page of that author's works. The series refs
    /// are native (their keys come from Open Library) while the author-to-series edge is derived and scoped to the checked
    /// pages. Deduplication is within the page only: a caller reading further pages deduplicates by the native key.
    /// </summary>
    public async Task<DiscoveryResult<AuthorSeriesEntry>> GetAuthorSeriesAsync(
        AuthorRef author, DiscoveryCursor? cursor, IDiscoveryBudget budget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(author);
        var scope = new DiscoveryScope(
            DiscoveryOperation.GetAuthorSeries, author.Id, "series-claimed-by-checked-author-works", "first-seen-in-works-order");
        if (!TryNativeId(author.Id, ExternalReferenceEntityType.Author, 'A', out var authorId))
        {
            return Refused<AuthorSeriesEntry>(scope, budget, DiscoveryCodes.SubjectInvalid);
        }

        if (!TryPage(cursor, out var page))
        {
            return Refused<AuthorSeriesEntry>(scope, budget, DiscoveryCodes.CursorInvalid);
        }

        if (!budget.TryAdmitPage())
        {
            return NotAnswered<AuthorSeriesEntry>(scope, budget, DiscoveryOutcome.Failed, DiscoveryCodes.PageCeiling);
        }

        using var work = budget.BeginWork();
        var reply = await GetAsync<OlDiscoverySearch>(AuthorWorksUri(authorId, SeriesOnlyFields, page), budget, cancellationToken);
        if (!reply.HasData)
        {
            return Stopped<AuthorSeriesEntry>(scope, budget, reply.Transport, reply.Code);
        }

        if (reply.Data!.Documents is not { } documents)
        {
            return NotAnswered<AuthorSeriesEntry>(scope, budget, DiscoveryOutcome.Failed, DiscoveryCodes.ProviderError);
        }

        var notes = new List<string> { DerivedFromAuthorWorks };
        var native = new Dictionary<string, SeriesClaim>(StringComparer.Ordinal);
        var labelOnly = new Dictionary<string, SeriesClaim>(StringComparer.Ordinal);
        var order = new List<SeriesClaim>();
        foreach (var document in documents)
        {
            if (document is null)
            {
                notes.Add(DiscoveryCodes.ItemMissingId);
                continue;
            }

            if (!Aligned(document.AuthorKeys).Contains(authorId))
            {
                notes.Add(AuthorAnchorMismatch);
                continue;
            }

            var claims = ReadSeries(document);
            if (claims.Problem is not null)
            {
                notes.Add(claims.Problem);
                continue;
            }

            foreach (var claim in claims.Claims)
            {
                if (claim.Key is not null)
                {
                    if (native.TryGetValue(claim.Key, out var first))
                    {
                        if (!string.Equals(first.Label, claim.Label, StringComparison.Ordinal))
                        {
                            notes.Add(SeriesLabelConflict);
                        }

                        continue;
                    }

                    native[claim.Key] = claim;
                    order.Add(claim);
                }
                else if (labelOnly.TryAdd(CatalogIdentityPolicy.AuthorKey(claim.Label), claim))
                {
                    // A label alone is a scoped, unbound grouping within this page; it is never given a native id.
                    order.Add(claim);
                }
            }
        }

        var entries = order
            .Select(claim => new AuthorSeriesEntry(ToSeriesRef(claim, notes), RelationshipEvidence.Derived(DerivedScope)))
            .ToList();
        budget.RecordItems(entries.Count);
        if (entries.Count == 0)
        {
            notes.Add(NoSeriesClaimsOnPage);
        }

        return Page(entries, scope, budget, documents.Count, reply.Data, page, notes);
    }

    // ------------------------------------------------------------------------------------------------- transport

    private enum OpenLibraryTransport
    {
        Ok,
        NotFound,
        Throttled,
        TimedOut,
        Unavailable,
        Refused,
        Failed
    }

    private sealed record OpenLibraryReply<T>(T? Data, OpenLibraryTransport Transport, string? Code)
        where T : class
    {
        public bool HasData => Data is not null;

        public static OpenLibraryReply<T> Stopped(OpenLibraryTransport transport, string code) => new(null, transport, code);
    }

    // One logical request is one physical attempt: nothing in this handler chain retries. The attempt is reserved before
    // dispatch (a request the gate then never lets through is still charged, the conservative direction), the gate's pacing
    // wait counts as provider work, and the whole request is bounded by the remaining provider-work time.
    private async Task<OpenLibraryReply<T>> GetAsync<T>(string relativeUri, IDiscoveryBudget budget, CancellationToken cancellationToken)
        where T : class
    {
        var remaining = budget.RemainingWork;
        if (remaining <= TimeSpan.Zero)
        {
            return OpenLibraryReply<T>.Stopped(OpenLibraryTransport.TimedOut, DiscoveryCodes.DeadlineReached);
        }

        var admission = budget.TryAdmitAttempt(isRetry: false);
        if (admission != AttemptAdmission.Admitted)
        {
            return admission switch
            {
                AttemptAdmission.DeadlineExceeded => OpenLibraryReply<T>.Stopped(OpenLibraryTransport.TimedOut, DiscoveryCodes.DeadlineReached),
                AttemptAdmission.JourneyExpired => OpenLibraryReply<T>.Stopped(OpenLibraryTransport.Refused, DiscoveryCodes.JourneyExpired),
                _ => OpenLibraryReply<T>.Stopped(OpenLibraryTransport.Refused, DiscoveryCodes.AttemptCeiling)
            };
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(remaining);
        try
        {
            using var response = await httpClient.GetAsync(relativeUri, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            return await ReadAsync<T>(response, budget, deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The provider-work deadline, or the client's own timeout, fired; the caller did not cancel.
            return OpenLibraryReply<T>.Stopped(OpenLibraryTransport.TimedOut, DiscoveryCodes.DeadlineReached);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            // Headers arrive first (ResponseHeadersRead), so a connection that ends or resets while the body streams surfaces
            // here as an HttpIOException (an IOException, not an HttpRequestException). Only these network conditions become a
            // typed failure; a programming error is not swallowed, and cancellation is handled above.
            return OpenLibraryReply<T>.Stopped(OpenLibraryTransport.Failed, DiscoveryCodes.ProviderError);
        }
    }

    private static async Task<OpenLibraryReply<T>> ReadAsync<T>(
        HttpResponseMessage response, IDiscoveryBudget budget, CancellationToken cancellationToken)
        where T : class
    {
        switch (response.StatusCode)
        {
            case HttpStatusCode.NotFound:
                return OpenLibraryReply<T>.Stopped(OpenLibraryTransport.NotFound, DiscoveryCodes.ProviderError);
            case HttpStatusCode.TooManyRequests:
                // Reported, never retried: a wait that does not fit the remaining work is said so, any other is "throttled".
                var wait = response.Headers.RetryAfter?.Delta
                    ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
                return OpenLibraryReply<T>.Stopped(
                    OpenLibraryTransport.Throttled,
                    wait is { } known && known > budget.RemainingWork ? DiscoveryCodes.RetryAfterTooLong : DiscoveryCodes.Throttled);
            case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                return OpenLibraryReply<T>.Stopped(OpenLibraryTransport.Unavailable, DiscoveryCodes.AuthorizationDenied);
        }

        if (!response.IsSuccessStatusCode)
        {
            return OpenLibraryReply<T>.Stopped(OpenLibraryTransport.Failed, DiscoveryCodes.ProviderError);
        }

        if (response.Content.Headers.ContentLength is > MaximumBodyBytes)
        {
            return OpenLibraryReply<T>.Stopped(OpenLibraryTransport.Failed, ResponseTooLarge);
        }

        using var buffer = new MemoryStream();
        await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
        {
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > MaximumBodyBytes)
                {
                    return OpenLibraryReply<T>.Stopped(OpenLibraryTransport.Failed, ResponseTooLarge);
                }

                buffer.Write(chunk, 0, read);
            }
        }

        try
        {
            var data = JsonSerializer.Deserialize<T>(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
            return data is null
                ? OpenLibraryReply<T>.Stopped(OpenLibraryTransport.Failed, DiscoveryCodes.ProviderError)
                : new OpenLibraryReply<T>(data, OpenLibraryTransport.Ok, null);
        }
        catch (JsonException)
        {
            return OpenLibraryReply<T>.Stopped(OpenLibraryTransport.Failed, DiscoveryCodes.ProviderError);
        }
    }

    private static string SearchUri(string query, string fields, int limit, int page) =>
        $"search.json?q={Uri.EscapeDataString(query)}&fields={Uri.EscapeDataString(fields)}" +
        $"&limit={limit.ToString(CultureInfo.InvariantCulture)}&page={page.ToString(CultureInfo.InvariantCulture)}";

    // author=<OL…A> is an exact author_key filter on Open Library's side; the id was validated before it got here.
    private static string AuthorWorksUri(string authorId, string fields, int page) =>
        $"search.json?author={authorId}&fields={Uri.EscapeDataString(fields)}" +
        $"&limit={DiscoveryPageSize.ToString(CultureInfo.InvariantCulture)}&page={page.ToString(CultureInfo.InvariantCulture)}";

    // ------------------------------------------------------------------------------------------------ id handling

    // Open Library keys are OL, digits and one type letter. Validating the whole shape keeps an id out of the URL unless it is
    // exactly one, and means an author id can never be read as a name by the author= parameter.
    private static bool IsOlId(string? value, char suffix) =>
        value is { Length: > 3 and <= 32 } &&
        value.StartsWith("OL", StringComparison.Ordinal) &&
        value[^1] == suffix &&
        value.AsSpan(2, value.Length - 3).IndexOfAnyExceptInRange('0', '9') < 0;

    private static bool TryNativeId(SourceRef reference, ExternalReferenceEntityType kind, char suffix, out string id)
    {
        id = reference.ExternalId;
        return string.Equals(reference.ProviderId, ProviderKey, StringComparison.Ordinal) &&
            reference.Kind == kind &&
            IsOlId(id, suffix);
    }

    private static SourceRef? AuthorRefOf(string? key) =>
        IsOlId(key, 'A') ? new SourceRef(ProviderKey, key!, ExternalReferenceEntityType.Author) : null;

    private static bool TryPage(DiscoveryCursor? cursor, out int page)
    {
        page = 1;
        return cursor is null ||
            (cursor.Value.StartsWith("p:", StringComparison.Ordinal) &&
                int.TryParse(cursor.Value.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out page) &&
                page is >= 2 and <= MaximumPage);
    }

    private static string[] Distinct(IEnumerable<string> notes) => notes.Distinct(StringComparer.Ordinal).ToArray();

    // ----------------------------------------------------------------------------------------------- shared mapping

    // Open Library sends parallel arrays. Each element keeps its position (a non-text element becomes null instead of being
    // skipped), so index i of one array is only ever compared with index i of another of the same length.
    private static List<string?> Aligned(JsonElement value)
    {
        var items = new List<string?>();
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                items.Add(value.GetString());
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    items.Add(item.ValueKind switch
                    {
                        JsonValueKind.String => item.GetString(),
                        JsonValueKind.Number => item.GetRawText(),
                        _ => null
                    });
                }

                break;
        }

        return items;
    }

    private sealed record SeriesClaim(string Label, string? Key, string? Position, decimal? Sort);

    private readonly record struct SeriesRead(IReadOnlyList<SeriesClaim> Claims, string? Problem);

    // series_key / series_name / series_position describe the same edges by position. Anything that cannot be paired exactly
    // makes the whole row unusable: a guessed pairing would attach a position to the wrong series.
    private static SeriesRead ReadSeries(OlDiscoveryDocument document)
    {
        var keys = Aligned(document.SeriesKeys);
        var names = Aligned(document.SeriesNames);
        var positions = Aligned(document.SeriesPositions);
        if (keys.Count == 0 && names.Count == 0 && positions.Count == 0)
        {
            return new SeriesRead([], null);
        }

        if (names.Count == 0 || (keys.Count != 0 && keys.Count != names.Count) ||
            (positions.Count != 0 && positions.Count != names.Count))
        {
            return new SeriesRead([], SeriesArraysMismatch);
        }

        var claims = new List<SeriesClaim>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < names.Count; index++)
        {
            var label = names[index]?.Trim();
            if (string.IsNullOrEmpty(label))
            {
                return new SeriesRead([], SeriesLabelMissing);
            }

            string? key = null;
            if (keys.Count != 0)
            {
                if (!IsOlId(keys[index], 'L'))
                {
                    return new SeriesRead([], SeriesKeyInvalid);
                }

                key = keys[index];
            }

            var position = positions.Count == 0 ? null : positions[index]?.Trim();
            position = string.IsNullOrEmpty(position) ? null : position;
            if (seen.Add($"{key}|{label}|{position}"))
            {
                claims.Add(new SeriesClaim(label, key, position, SortOf(position)));
            }
        }

        return new SeriesRead(claims, null);
    }

    // A plain non-negative number sorts; a range such as "1-7", "1.1" style text or anything else keeps only its label.
    private static decimal? SortOf(string? position) =>
        position is not null &&
            decimal.TryParse(position, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) &&
            value >= 0
                ? value
                : null;

    private static SeriesRef ToSeriesRef(SeriesClaim claim, List<string> notes)
    {
        if (claim.Key is null)
        {
            notes.Add(DiscoveryCodes.NoNativeSeriesId);
            return new SeriesRef(claim.Label, null);
        }

        return new SeriesRef(claim.Label, new SourceRef(ProviderKey, claim.Key, ExternalReferenceEntityType.Series));
    }

    private static PositionClaim? PositionOf(SeriesClaim claim) =>
        claim.Position is null
            ? null
            : new PositionClaim(
                claim.Position,
                claim.Sort,
                null,
                claim.Key is null ? null : new SourceRef(ProviderKey, claim.Key, ExternalReferenceEntityType.Series));

    private static DiscoveredBook ToDiscoveredBook(
        OlDiscoveryDocument document, string workId, IReadOnlyList<SeriesClaim> claims, DateTimeOffset observedAtUtc, List<string> notes)
    {
        var names = Aligned(document.AuthorNames);
        var keys = Aligned(document.AuthorKeys);
        var credits = new List<CreditRef>();
        if (names.Count != 0 && (keys.Count == 0 || keys.Count == names.Count))
        {
            for (var ordinal = 0; ordinal < names.Count; ordinal++)
            {
                if (!string.IsNullOrWhiteSpace(names[ordinal]))
                {
                    credits.Add(new CreditRef(ordinal, names[ordinal]!.Trim(), null, keys.Count == 0 ? null : AuthorRefOf(keys[ordinal])));
                }
            }
        }
        else if (names.Count != 0)
        {
            // Names without a trustworthy pairing: the labels stay, no id is attached to any of them.
            notes.Add(CreditArraysMismatch);
            for (var ordinal = 0; ordinal < names.Count; ordinal++)
            {
                if (!string.IsNullOrWhiteSpace(names[ordinal]))
                {
                    credits.Add(new CreditRef(ordinal, names[ordinal]!.Trim(), null, null));
                }
            }
        }
        else if (keys.Count != 0)
        {
            notes.Add(CreditArraysMismatch);
        }

        // first_publish_year is the work's earliest indexed publication year (Open Library's work-level first publication,
        // the same meaning the ordinary search mapping already gives first_publish_date). It is a year, never a day.
        var publication = document.FirstPublishYear is >= 1 and <= 9999
            ? PublicationEvidence.Dated(new DateOnly(document.FirstPublishYear.Value, 1, 1), DatePrecision.Year, DateBasis.Original)
            : PublicationEvidence.NotStated;

        return new DiscoveredBook(
            new SourceRef(ProviderKey, workId, ExternalReferenceEntityType.Work),
            ToTitleForm(document.Title!.Trim(), claims),
            credits,
            publication,
            observedAtUtc);
    }

    // Open Library appends "<series> #<position>" to some work titles. The reported title is always kept; the suffix is lifted
    // into the normalized form only when this very row's own series claim names that exact series and position, so no subtitle
    // is ever cut on a guess.
    private static TitleForm ToTitleForm(string reported, IReadOnlyList<SeriesClaim> claims)
    {
        foreach (var claim in claims)
        {
            if (claim.Position is null)
            {
                continue;
            }

            var suffix = $" - {claim.Label} #{claim.Position}";
            if (reported.Length > suffix.Length && reported.EndsWith(suffix, StringComparison.Ordinal))
            {
                var stripped = reported[..^suffix.Length].TrimEnd();
                if (stripped.Length > 0)
                {
                    return new TitleForm(reported, stripped, "series-suffix");
                }
            }
        }

        return new TitleForm(reported);
    }

    private static string? LifeSpan(string? birth, string? death)
    {
        var born = string.IsNullOrWhiteSpace(birth) ? null : birth.Trim();
        var died = string.IsNullOrWhiteSpace(death) ? null : death.Trim();
        var text = (born, died) switch
        {
            (null, null) => null,
            (_, null) => $"born {born}",
            (null, _) => $"died {died}",
            _ => $"{born} – {died}"
        };
        return text is { Length: > 100 } ? text[..100] : text;
    }

    // ------------------------------------------------------------------------------------------- result building

    private DiscoveryResult<T> Page<T>(
        List<T> items,
        DiscoveryScope scope,
        IDiscoveryBudget budget,
        int rowsRead,
        OlDiscoverySearch response,
        int page,
        List<string> notes)
    {
        var observed = time.GetUtcNow();
        var offset = response.Start ?? (page - 1) * DiscoveryPageSize;
        // numFound says whether more exists; without it a full page is the only hint, and is reported as "more" rather than guessed away.
        var more = response.NumberFound is { } found
            ? found > offset + rowsRead && rowsRead > 0
            : rowsRead >= DiscoveryPageSize;
        if (page > 1 && rowsRead == 0)
        {
            // A continuation was issued because this page was expected to exist; an empty one means the data moved underneath it.
            notes.Add(DiscoveryCodes.ProviderMutatedBetweenPages);
        }

        var codes = Distinct(notes);
        var next = more ? new DiscoveryCursor("p:" + (page + 1).ToString(CultureInfo.InvariantCulture)) : null;
        if (rowsRead == 0 && page == 1 && response.NumberFound is > 0)
        {
            // The provider says records exist and sent none: a contradictory answer, never a verified-empty scope.
            return DiscoveryResult.NoUsableItems<T>(
                scope, DiscoveryCompletion.ExhaustedInCheckedScope, null, observed, budget.Usage, [DiscoveryCodes.ProviderError]);
        }

        if (items.Count == 0)
        {
            // Rows the provider returned but none usable are not an empty scope, and a full page of them still has a next page.
            // Only a page with no source rows at all, and nothing after it, is verified empty (never after a continuation).
            if (rowsRead == 0 && !more && page == 1)
            {
                return DiscoveryResult.Empty<T>(scope, observed, budget.Usage, codes);
            }

            var reasons = codes.Length == 0 ? [DiscoveryCodes.ItemMissingId] : codes;
            return more
                ? DiscoveryResult.NoUsableItems<T>(scope, DiscoveryCompletion.MoreAvailable, next, observed, budget.Usage, reasons)
                : DiscoveryResult.NoUsableItems<T>(scope, DiscoveryCompletion.ExhaustedInCheckedScope, null, observed, budget.Usage, reasons);
        }

        return more
            ? DiscoveryResult.Found(items, scope, DiscoveryCompletion.MoreAvailable, observed, budget.Usage, next, codes)
            : DiscoveryResult.Found(items, scope, DiscoveryCompletion.ExhaustedInCheckedScope, observed, budget.Usage, notes: codes);
    }

    private DiscoveryResult<T> Empty<T>(DiscoveryScope scope, IDiscoveryBudget budget, IReadOnlyList<string>? notes = null) =>
        DiscoveryResult.Empty<T>(scope, time.GetUtcNow(), budget.Usage, notes);

    private DiscoveryResult<T> NoUsable<T>(
        DiscoveryScope scope, IDiscoveryBudget budget, DiscoveryCompletion completion, DiscoveryCursor? next, IReadOnlyList<string> reasons) =>
        DiscoveryResult.NoUsableItems<T>(scope, completion, next, time.GetUtcNow(), budget.Usage, reasons);

    // A request refused before any provider call: nothing was dispatched, so usage is whatever the budget already holds.
    private DiscoveryResult<T> Refused<T>(DiscoveryScope scope, IDiscoveryBudget budget, string code) =>
        NotAnswered<T>(scope, budget, DiscoveryOutcome.Failed, code);

    private DiscoveryResult<T> NotAnswered<T>(DiscoveryScope scope, IDiscoveryBudget budget, DiscoveryOutcome outcome, string code) =>
        DiscoveryResult.NotAnswered<T>(outcome, scope, time.GetUtcNow(), budget.Usage, [code]);

    private DiscoveryResult<T> Stopped<T>(DiscoveryScope scope, IDiscoveryBudget budget, OpenLibraryTransport transport, string? reason)
    {
        var (outcome, code) = transport switch
        {
            OpenLibraryTransport.Throttled => (DiscoveryOutcome.Throttled, reason ?? DiscoveryCodes.Throttled),
            OpenLibraryTransport.TimedOut => (DiscoveryOutcome.TimedOut, DiscoveryCodes.DeadlineReached),
            OpenLibraryTransport.Unavailable => (DiscoveryOutcome.Unavailable, reason ?? DiscoveryCodes.AuthorizationDenied),
            _ => (DiscoveryOutcome.Failed, reason ?? DiscoveryCodes.ProviderError)
        };
        return NotAnswered<T>(scope, budget, outcome, code);
    }

    // ------------------------------------------------------------------------------------------------------ models

    private sealed class OlDiscoverySearch
    {
        [JsonPropertyName("numFound")]
        public int? NumberFound { get; init; }

        [JsonPropertyName("start")]
        public int? Start { get; init; }

        [JsonPropertyName("docs")]
        public IReadOnlyList<OlDiscoveryDocument?>? Documents { get; init; }
    }

    private sealed class OlDiscoveryDocument
    {
        [JsonPropertyName("key")]
        public string? Key { get; init; }

        [JsonPropertyName("title")]
        public string? Title { get; init; }

        [JsonPropertyName("author_key")]
        public JsonElement AuthorKeys { get; init; }

        [JsonPropertyName("author_name")]
        public JsonElement AuthorNames { get; init; }

        [JsonPropertyName("first_publish_year")]
        public int? FirstPublishYear { get; init; }

        [JsonPropertyName("isbn")]
        public JsonElement Isbns { get; init; }

        [JsonPropertyName("series_key")]
        public JsonElement SeriesKeys { get; init; }

        [JsonPropertyName("series_name")]
        public JsonElement SeriesNames { get; init; }

        [JsonPropertyName("series_position")]
        public JsonElement SeriesPositions { get; init; }
    }

    private sealed class OlAuthorSearch
    {
        [JsonPropertyName("numFound")]
        public int? NumberFound { get; init; }

        [JsonPropertyName("docs")]
        public IReadOnlyList<OlAuthorDocument?>? Documents { get; init; }
    }

    private sealed class OlAuthorDocument
    {
        [JsonPropertyName("key")]
        public string? Key { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("birth_date")]
        public string? BirthDate { get; init; }

        [JsonPropertyName("death_date")]
        public string? DeathDate { get; init; }

        [JsonPropertyName("work_count")]
        public int? WorkCount { get; init; }
    }

    private sealed class OlAuthorRecord
    {
        [JsonPropertyName("key")]
        public string? Key { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("birth_date")]
        public string? BirthDate { get; init; }

        [JsonPropertyName("death_date")]
        public string? DeathDate { get; init; }

        [JsonPropertyName("type")]
        public OlTypeRef? Type { get; init; }
    }

    private sealed class OlTypeRef
    {
        [JsonPropertyName("key")]
        public string? Key { get; init; }
    }
}
