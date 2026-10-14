using System.Globalization;
using System.Text.RegularExpressions;
using FamilyLibrarian.Application.Providers;

namespace FamilyLibrarian.Application.Security;

/// <summary>
/// A trailing series designation on an audiobook's title tag, such as "Fourth Wing: Fourth Wing, Book 1" or
/// "Iron Flame (The Empyrean #2)". Removing it leaves the title the strict comparison needs. A designation whose
/// number contradicts the catalog's series position is never removed, so "Book 2" cannot stand in for book one.
/// </summary>
public static partial class AudioSeriesDesignation
{
    /// <summary>The title without exactly one trailing designation, or null when there is none or it is contradicted.</summary>
    public static string? StripConsistentWith(string title, IReadOnlyList<BookSeries> catalogSeries)
    {
        ArgumentNullException.ThrowIfNull(catalogSeries);
        var match = Designation().Match(title.Trim());
        if (!match.Success)
        {
            return null;
        }

        var core = match.Groups["core"].Value.Trim();
        if (core.Length == 0 || !decimal.TryParse(match.Groups["n"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
        {
            return null;
        }

        var knownPositions = catalogSeries
            .Select(series => decimal.TryParse(series.Position, NumberStyles.Number, CultureInfo.InvariantCulture, out var position)
                ? position
                : (decimal?)null)
            .Where(position => position is not null)
            .ToArray();
        return knownPositions.Length == 0 || knownPositions.Contains(number) ? core : null;
    }

    // ": anything, Book 1" / " - Book 2" / ", Volume 3" / "(Series #2)" / "[Series, Book 2]". "Part" is deliberately
    // absent: "Part 1" on an audiobook usually means one fragment of a split book, which must not pass as the whole.
    [GeneratedRegex(
        @"^(?<core>.+?)\s*(?:[:\-–—]\s*[^:()\[\]]*?\b(?:Book|Volume|Vol\.?)\s*(?<n>\d+(?:\.\d+)?)" +
        @"|,\s*(?:Book|Volume|Vol\.?)\s*(?<n>\d+(?:\.\d+)?)" +
        @"|[(\[]\s*[^()\[\]]*?(?:#\s*|\bBook\s*|\bVolume\s*|\bVol\.?\s*)(?<n>\d+(?:\.\d+)?)\s*[)\]])\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Designation();
}
