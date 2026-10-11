using System.Globalization;
using System.Text.RegularExpressions;
using FamilyLibrarian.Application.Catalog;

namespace FamilyLibrarian.Application.Conversation;

/// <summary>
/// Recognizes "the third Murderbot book", "book 3 of Murderbot", "Murderbot book 3", "last book of Dune" and "reading order of
/// Dune". Returns the series name and a position (null for a listing, <see cref="SeriesPositionResolver.LastPosition"/> for the
/// latest). It only reads wording: whether such a series exists is decided later from catalog facts, and when none does the
/// message is searched as ordinary text.
/// </summary>
public static partial class SeriesParser
{
    private const string Ordinals =
        "first|second|third|fourth|fifth|sixth|seventh|eighth|ninth|tenth|eleventh|twelfth|thirteenth|fourteenth|fifteenth|sixteenth|seventeenth|eighteenth|nineteenth|twentieth";

    public static bool TryParse(string text, out string seriesName, out decimal? position)
    {
        seriesName = string.Empty;
        position = null;
        var input = text.Trim().TrimEnd('?', '.', '!').Trim();

        foreach (var pattern in new[] { OrdinalOfPattern(), OrdinalBookPattern() })
        {
            if (pattern.Match(input) is { Success: true } match && ReadOrdinal(match.Groups["pos"].Value) is { } ordinal)
            {
                return Accept(match.Groups["s"].Value, ordinal, out seriesName, out position);
            }
        }

        if (NumberedOfPattern().Match(input) is { Success: true } numbered)
        {
            return Accept(numbered.Groups["s"].Value, ParseNumber(numbered.Groups["n"].Value), out seriesName, out position);
        }

        if (SeriesNumberedPattern().Match(input) is { Success: true } trailing)
        {
            return Accept(trailing.Groups["s"].Value, ParseNumber(trailing.Groups["n"].Value), out seriesName, out position);
        }

        if (OrderPattern().Match(input) is { Success: true } order)
        {
            var name = order.Groups["s"].Value;
            return Accept(name, null, out seriesName, out position);
        }

        return false;
    }

    private static bool Accept(string name, decimal? foundPosition, out string seriesName, out decimal? position)
    {
        seriesName = name.Trim().Trim('"', '“', '”').Trim();
        position = foundPosition;
        return CatalogSearchLimits.IsValidQueryText(seriesName) && seriesName.Length <= 80;
    }

    private static decimal? ParseNumber(string value) =>
        decimal.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number is >= 1 and <= 99 ? number : null;

    private static decimal? ReadOrdinal(string word)
    {
        var lower = word.ToLowerInvariant();
        if (lower is "last" or "latest" or "newest" or "most recent")
        {
            return SeriesPositionResolver.LastPosition;
        }

        var index = Array.IndexOf(Ordinals.Split('|'), lower);
        if (index >= 0)
        {
            return index + 1;
        }

        return NumericOrdinalPattern().Match(lower) is { Success: true } numeric ? ParseNumber(numeric.Groups[1].Value) : null;
    }

    [GeneratedRegex(@"^(?:what(?:'s| is) )?(?:the )?(?<pos>" + Ordinals + @"|\d{1,2}(?:st|nd|rd|th)|last|latest|newest|most recent)\s+(?:book|novel|entry|volume|installment|instalment|story|one)\s+(?:of|in|from)\s+(?:the\s+)?(?<s>.+?)(?:\s+series)?$", RegexOptions.IgnoreCase)]
    private static partial Regex OrdinalOfPattern();

    [GeneratedRegex(@"^(?:the )?(?<pos>" + Ordinals + @"|\d{1,2}(?:st|nd|rd|th)|last|latest|newest)\s+(?<s>.+?)\s+(?:book|novel|volume|installment|instalment|entry)$", RegexOptions.IgnoreCase)]
    private static partial Regex OrdinalBookPattern();

    [GeneratedRegex(@"^(?:book|volume|vol\.?|number|no\.?|#)\s*(?<n>\d{1,2})\s+(?:of|in|from)\s+(?:the\s+)?(?<s>.+?)(?:\s+series)?$", RegexOptions.IgnoreCase)]
    private static partial Regex NumberedOfPattern();

    [GeneratedRegex(@"^(?:the\s+)?(?<s>.+?)(?:\s+series)?[,:]?\s+(?:book|volume|vol\.?|part|#)\s*(?<n>\d{1,2})$", RegexOptions.IgnoreCase)]
    private static partial Regex SeriesNumberedPattern();

    [GeneratedRegex(@"^(?:what(?:'s| is| are) )?(?:the )?(?:(?:books|titles|order|reading order|series order|list of books|books in order)\s+(?:of|in|for)\s+(?:the\s+)?(?<s>.+?)(?:\s+series)?|(?<s>.+?)(?:\s+series)?\s+(?:reading order|series order|books in order|in order))$", RegexOptions.IgnoreCase)]
    private static partial Regex OrderPattern();

    [GeneratedRegex(@"^(\d{1,2})(?:st|nd|rd|th)$")]
    private static partial Regex NumericOrdinalPattern();
}
