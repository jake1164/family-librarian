using System.Globalization;
using FamilyLibrarian.Domain.Catalog;

namespace FamilyLibrarian.Application.Catalog;

/// <summary>One book known to belong to a series, with where it sits and how to reach its catalog record.</summary>
public sealed record SeriesBook(
    string SeriesName, decimal? Position, string Title, string Authors, string ProviderId, string ExternalId);

public enum SeriesLookupKind
{
    /// <summary>No known series matches the name.</summary>
    NotFound,

    /// <summary>More than one different series matches; the caller should ask which.</summary>
    AmbiguousSeries,

    /// <summary>The series is known but nothing is known at the requested position.</summary>
    PositionMissing,

    Found
}

/// <param name="Books">
/// For a position: the book(s) known at it (more than one means providers disagree and the caller must ask).
/// For a listing: the known books in series order.
/// </param>
/// <param name="KnownPositions">Positions known for the series, for explaining a miss.</param>
public sealed record SeriesLookupResult(
    SeriesLookupKind Kind,
    string? SeriesName,
    IReadOnlyList<SeriesBook> Books,
    IReadOnlyList<string> AmbiguousSeriesNames,
    IReadOnlyList<decimal> KnownPositions);

/// <summary>
/// Decides which known series a name refers to and which book sits at a position, from series facts that already exist in
/// the catalog: the series providers report on their search results, and series recorded for works the household has already
/// resolved. Pure logic, so ambiguity handling is fully testable; nothing here invents a position.
/// </summary>
public static class SeriesPositionResolver
{
    private static readonly HashSet<string> NoiseWords = ["the", "a", "an", "series", "saga", "books", "book", "novels"];

    /// <param name="position">A position, <see cref="LastPosition"/> for "the latest", or null to list the series in order.</param>
    public static SeriesLookupResult Resolve(IEnumerable<SeriesBook> known, string wantedSeries, decimal? position, int maxListed)
    {
        var wanted = Tokens(wantedSeries);
        if (wanted.Count == 0)
        {
            return new SeriesLookupResult(SeriesLookupKind.NotFound, null, [], [], []);
        }

        var candidates = known
            .Where(book => Tokens(book.SeriesName) is { Count: > 0 } tokens && wanted.IsSubsetOf(tokens))
            .ToArray();
        if (candidates.Length == 0)
        {
            // People name a series after its first book ("the Fourth Wing series") while the catalog calls it something else
            // ("The Empyrean"): a book whose whole title is the wanted name leads to the series it belongs to.
            var wantedTitle = CatalogText.NormalizeForMatch(wantedSeries);
            var owners = known.Where(book => CatalogText.NormalizeForMatch(book.Title) == wantedTitle && Tokens(book.SeriesName).Count > 0)
                .Select(book => book.SeriesName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            candidates = owners.Length == 0
                ? []
                : known.Where(book => owners.Contains(book.SeriesName, StringComparer.OrdinalIgnoreCase)).ToArray();
        }

        var bySeries = candidates
            .GroupBy(book => string.Join(' ', Tokens(book.SeriesName).Order(StringComparer.Ordinal)))
            .ToArray();
        if (bySeries.Length == 0)
        {
            return new SeriesLookupResult(SeriesLookupKind.NotFound, null, [], [], []);
        }

        // An exact name wins over a longer one that merely contains the words ("Dune" over "Dune Chronicles Companion").
        var exact = bySeries.Where(group => Tokens(group.First().SeriesName).SetEquals(wanted)).ToArray();
        if (exact.Length == 1)
        {
            bySeries = exact;
        }

        if (bySeries.Length > 1)
        {
            return new SeriesLookupResult(
                SeriesLookupKind.AmbiguousSeries, null, [],
                bySeries.Select(group => group.First().SeriesName).Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToArray(), []);
        }

        var books = Distinct(bySeries[0]).ToArray();
        var name = books.GroupBy(book => book.SeriesName).OrderByDescending(group => group.Count()).First().Key;
        var positions = books.Where(book => book.Position is not null).Select(book => book.Position!.Value).Distinct().Order().ToArray();

        if (position is null)
        {
            // Numbered books in order, then entries the catalog gives no number (companion novellas such as a "Threshing Day").
            var listing = books.OrderBy(book => book.Position is null).ThenBy(book => book.Position).ThenBy(book => book.Title, StringComparer.Ordinal)
                .Take(maxListed).ToArray();
            return listing.Length == 0
                ? new SeriesLookupResult(SeriesLookupKind.PositionMissing, name, [], [], positions)
                : new SeriesLookupResult(SeriesLookupKind.Found, name, listing, [], positions);
        }

        var target = position == LastPosition ? positions.LastOrDefault() : position.Value;
        var atPosition = positions.Length == 0 ? [] : books.Where(book => book.Position == target).Take(maxListed).ToArray();
        return atPosition.Length == 0
            ? new SeriesLookupResult(SeriesLookupKind.PositionMissing, name, [], [], positions)
            : new SeriesLookupResult(SeriesLookupKind.Found, name, atPosition, [], positions);
    }

    /// <summary>
    /// The book that follows the named one: the next higher position, so a half-step such as 3.5 is reached in between. The series
    /// comes from <paramref name="wantedSeries"/>, or, when none was said, from the series the named title itself belongs to.
    /// Never guesses: an unknown title, or one with no position, gives no answer.
    /// </summary>
    public static SeriesLookupResult ResolveAfter(IEnumerable<SeriesBook> known, string? wantedSeries, string afterTitle, int maxListed)
    {
        var books = known as IReadOnlyCollection<SeriesBook> ?? known.ToArray();
        var anchorName = CatalogText.NormalizeForMatch(afterTitle);
        if (anchorName.Length == 0)
        {
            return new SeriesLookupResult(SeriesLookupKind.NotFound, null, [], [], []);
        }

        var seriesName = wantedSeries;
        if (string.IsNullOrWhiteSpace(seriesName))
        {
            var owners = books.Where(book => IsTitle(book.Title, anchorName) && !string.IsNullOrWhiteSpace(book.SeriesName))
                .Select(book => book.SeriesName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (owners.Length == 0)
            {
                return new SeriesLookupResult(SeriesLookupKind.NotFound, null, [], [], []);
            }

            var distinctSeries = owners.GroupBy(name => string.Join(' ', Tokens(name).Order(StringComparer.Ordinal))).ToArray();
            if (distinctSeries.Length > 1)
            {
                return new SeriesLookupResult(SeriesLookupKind.AmbiguousSeries, null, [], owners.Take(5).ToArray(), []);
            }

            seriesName = owners[0];
        }

        var listed = Resolve(books, seriesName, null, int.MaxValue);
        if (listed.Kind is SeriesLookupKind.NotFound or SeriesLookupKind.AmbiguousSeries)
        {
            return listed;
        }

        var anchor = listed.Books.FirstOrDefault(book => IsTitle(book.Title, anchorName));
        if (anchor is null)
        {
            return new SeriesLookupResult(SeriesLookupKind.NotFound, null, [], [], []);
        }

        var following = anchor.Position is { } at
            ? listed.Books.Where(book => book.Position > at).ToArray()
            : [];
        if (following.Length == 0)
        {
            return new SeriesLookupResult(SeriesLookupKind.PositionMissing, listed.SeriesName, [], [], listed.KnownPositions);
        }

        var next = following.Min(book => book.Position);
        return new SeriesLookupResult(
            SeriesLookupKind.Found, listed.SeriesName, following.Where(book => book.Position == next).Take(maxListed).ToArray(), [], listed.KnownPositions);
    }

    /// <summary>True when the title is the named one, ignoring case, punctuation and a leading article, or one contains the other as whole words.</summary>
    private static bool IsTitle(string title, string normalizedWanted)
    {
        var normalized = CatalogText.NormalizeForMatch(title);
        if (normalized.Length == 0)
        {
            return false;
        }

        if (normalized == normalizedWanted)
        {
            return true;
        }

        var (shorter, longer) = normalized.Length <= normalizedWanted.Length ? (normalized, normalizedWanted) : (normalizedWanted, normalized);
        return shorter.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 2 && $" {longer} ".Contains($" {shorter} ", StringComparison.Ordinal);
    }

    /// <summary>Stands for "the last/latest book" in a lookup.</summary>
    public const decimal LastPosition = -1m;

    /// <summary>Numeric position of a catalog series entry, or null when the label is not a plain number.</summary>
    public static decimal? PositionOf(decimal? sort, string? label)
    {
        if (sort is { } value)
        {
            return value;
        }

        return decimal.TryParse(label, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static IEnumerable<SeriesBook> Distinct(IEnumerable<SeriesBook> books) =>
        books.GroupBy(book => (book.Position, Title: CatalogText.NormalizeForMatch(book.Title)))
            .Select(group => group.OrderByDescending(book => book.Authors.Length).First());

    private static HashSet<string> Tokens(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return [];
        }

        return CatalogText.NormalizeForMatch(name).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => !NoiseWords.Contains(token)).ToHashSet(StringComparer.Ordinal);
    }
}

/// <summary>Gathers series facts from provider search results and from the series the household's catalog already records.</summary>
public sealed class SeriesPositionService(ICatalogSearchService search, ICatalogRepository catalog)
{
    public async Task<SeriesLookupResult> FindAsync(
        string seriesName, decimal? position, string? afterTitle, int maxBooks, CancellationToken cancellationToken)
    {
        var name = seriesName.Trim();
        var title = afterTitle?.Trim();
        if ((name.Length > 0 || title is not { Length: > 0 }) && !CatalogSearchLimits.IsValidQueryText(name))
        {
            return new SeriesLookupResult(SeriesLookupKind.NotFound, null, [], [], []);
        }

        // A "next after" question searches the series name and the named title: the title is what tells us the series when none was said.
        var queries = new[] { name, title ?? string.Empty }.Where(query => query.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var outcomes = await Task.WhenAll(queries.Select(query => search.SearchAsync(new BookSearchQuery(query), cancellationToken)));
        var known = new List<SeriesBook>();
        foreach (var candidate in outcomes.SelectMany(outcome => outcome.Candidates))
        {
            foreach (var series in candidate.Series)
            {
                known.Add(new SeriesBook(
                    series.Name, SeriesPositionResolver.PositionOf(series.PositionSort, series.PositionLabel), candidate.Title,
                    string.Join(", ", candidate.Authors.Take(3)), candidate.ProviderId, candidate.ExternalId));
            }
        }

        if (name.Length > 0)
        {
            known.AddRange(await RecordedSeriesAsync(name, cancellationToken));
        }

        return title is { Length: > 0 }
            ? SeriesPositionResolver.ResolveAfter(known, name, title, maxBooks)
            : SeriesPositionResolver.Resolve(known, name, position, maxBooks);
    }

    /// <summary>Series already recorded for works the household resolved, reached through the work's own catalog reference.</summary>
    private async Task<IEnumerable<SeriesBook>> RecordedSeriesAsync(string name, CancellationToken cancellationToken)
    {
        var recorded = await catalog.FindSeriesByNormalizedNameAsync(CatalogText.NormalizeForMatch(name), cancellationToken);
        if (recorded is null)
        {
            return [];
        }

        var loaded = await catalog.GetSeriesAsync(recorded.Id, cancellationToken);
        var books = new List<SeriesBook>();
        foreach (var entry in loaded?.Entries ?? [])
        {
            var source = (await catalog.GetWorkSourcesAsync(entry.WorkId, cancellationToken)) is { Count: > 0 } sources ? sources[0] : null;
            if (source is null)
            {
                continue;
            }

            books.Add(new SeriesBook(
                recorded.Name, SeriesPositionResolver.PositionOf(entry.PositionSort, entry.PositionLabel), entry.Work.CanonicalTitle,
                string.Join(", ", entry.Work.Authors.Where(author => author.Author is not null).OrderBy(author => author.Ordinal).Take(3).Select(author => author.Author.CanonicalName)),
                source.ProviderId, source.ExternalId));
        }

        return books;
    }
}
