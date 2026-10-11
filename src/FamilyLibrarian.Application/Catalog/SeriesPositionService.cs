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
            var listing = books.Where(book => book.Position is not null).OrderBy(book => book.Position).ThenBy(book => book.Title, StringComparer.Ordinal)
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
    public async Task<SeriesLookupResult> FindAsync(string seriesName, decimal? position, int maxBooks, CancellationToken cancellationToken)
    {
        var name = seriesName.Trim();
        if (!CatalogSearchLimits.IsValidQueryText(name))
        {
            return new SeriesLookupResult(SeriesLookupKind.NotFound, null, [], [], []);
        }

        var known = new List<SeriesBook>();
        var outcome = await search.SearchAsync(new BookSearchQuery(name), cancellationToken);
        foreach (var candidate in outcome.Candidates)
        {
            foreach (var series in candidate.Series)
            {
                known.Add(new SeriesBook(
                    series.Name, SeriesPositionResolver.PositionOf(series.PositionSort, series.PositionLabel), candidate.Title,
                    string.Join(", ", candidate.Authors.Take(3)), candidate.ProviderId, candidate.ExternalId));
            }
        }

        known.AddRange(await RecordedSeriesAsync(name, cancellationToken));
        return SeriesPositionResolver.Resolve(known, name, position, maxBooks);
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
