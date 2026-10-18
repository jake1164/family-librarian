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

    public static bool TryParse(string text, out string seriesName, out decimal? position) =>
        TryParse(text, out seriesName, out position, out _);

    public static bool TryParse(string text, out string seriesName, out decimal? position, out string? afterTitle)
    {
        afterTitle = null;
        seriesName = string.Empty;
        position = null;
        var input = text.Trim().TrimEnd('?', '.', '!').Trim();

        foreach (var pattern in new[] { NextAfterSeriesFirstPattern(), NextAfterSeriesLastPattern(), AfterTitlePattern() })
        {
            if (pattern.Match(input) is { Success: true } after && after.Groups["t"].Value.Trim() is { Length: > 0 and <= 120 } title
                && CatalogSearchLimits.IsValidQueryText(title))
            {
                var series = after.Groups["s"].Success ? after.Groups["s"].Value : string.Empty;
                if (series.Length > 0 && !Accept(series, null, out seriesName, out position))
                {
                    return false;
                }

                afterTitle = title.Trim('"', '“', '”').Trim();
                return true;
            }
        }

        foreach (var pattern in new[] { OrdinalOfPattern(), OrdinalBookPattern(), AnyNewBookPattern() })
        {
            if (pattern.Match(input) is { Success: true } match && ReadOrdinal(match.Groups["pos"].Success ? match.Groups["pos"].Value : "newest") is { } ordinal)
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

        // "list all the Jack Ryan books in order": drop the request words, then read what is left as a listing.
        var listLead = ListLeadPattern().Match(input);
        var listing = listLead.Success ? input[listLead.Length..].Trim() : input;
        if (OrderPattern().Match(listing) is { Success: true } order)
        {
            return Accept(order.Groups["s"].Value, null, out seriesName, out position);
        }

        if (listLead.Success && AllBooksPattern().Match(listing) is { Success: true } all)
        {
            return Accept(all.Groups["s"].Value, null, out seriesName, out position);
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
        decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number) && number is >= 1 and <= 99 ? number : null;

    private static decimal? ReadOrdinal(string word)
    {
        var lower = word.ToLowerInvariant();
        if (lower is "last" or "latest" or "newest" or "new" or "most recent")
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

    [GeneratedRegex(@"^(?:what(?:'s| is) )?(?:the )?(?<pos>" + Ordinals + @"|\d{1,2}(?:st|nd|rd|th)|last|latest|newest|new|most recent)\s+(?:book|novel|entry|volume|installment|instalment|story|one)\s+(?:of|in|from)\s+(?:the\s+)?(?<s>.+?)(?:\s+series)?$", RegexOptions.IgnoreCase)]
    private static partial Regex OrdinalOfPattern();

    [GeneratedRegex(@"^(?:what(?:'s| is) )?(?:the )?(?<pos>" + Ordinals + @"|\d{1,2}(?:st|nd|rd|th)|last|latest|newest)\s+(?<s>.+?)\s+(?:book|novel|volume|installment|instalment|entry)$", RegexOptions.IgnoreCase)]
    private static partial Regex OrdinalBookPattern();

    [GeneratedRegex(@"^(?:book|volume|vol\.?|number|no\.?|#)\s*(?<n>\d{1,2}(?:\.\d{1,2})?)\s+(?:of|in|from)\s+(?:the\s+)?(?<s>.+?)(?:\s+series)?$", RegexOptions.IgnoreCase)]
    private static partial Regex NumberedOfPattern();

    [GeneratedRegex(@"^(?:the\s+)?(?<s>.+?)(?:\s+series)?[,:]?\s+(?:book|volume|vol\.?|part|#)\s*(?<n>\d{1,2}(?:\.\d{1,2})?)$", RegexOptions.IgnoreCase)]
    private static partial Regex SeriesNumberedPattern();

    [GeneratedRegex(@"^(?:what(?:'s| is| are) )?(?:the )?(?:(?:books|titles|order|reading order|series order|list of books|books in order)\s+(?:of|in|for)\s+(?:the\s+)?(?<s>.+?)(?:\s+series)?|(?<s>.+?)(?:\s+series)?\s+(?:reading order|series order|books in order|in order))$", RegexOptions.IgnoreCase)]
    private static partial Regex OrderPattern();

    [GeneratedRegex(@"^(?:is|are) there\s+(?:a|any)\s+(?:new|newer|newest)\s+(?<s>.+?)(?:\s+series)?\s+(?:books?|novels?)$", RegexOptions.IgnoreCase)]
    private static partial Regex AnyNewBookPattern();

    [GeneratedRegex(@"^(?:please\s+)?(?:(?:list|show|give|tell)(?:\s+me)?\s+)?(?:all\s+(?:of\s+)?)?(?:the\s+)?(?=\S)", RegexOptions.IgnoreCase)]
    private static partial Regex ListLeadPattern();

    [GeneratedRegex(@"^(?<s>.+?)(?:\s+series)?\s+(?:books|novels)$", RegexOptions.IgnoreCase)]
    private static partial Regex AllBooksPattern();

    [GeneratedRegex(@"^(?:what(?:'s| is)\s+)?(?:the\s+)?next\s+(?:(?:book|novel|entry|one)\s+)?(?:in|of|from)\s+(?:the\s+)?(?<s>.+?)(?:\s+series)?\s+after\s+(?<t>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex NextAfterSeriesFirstPattern();

    [GeneratedRegex(@"^(?:what(?:'s| is)\s+)?(?:the\s+)?next\s+(?<s>.+?)\s+(?:book|novel|entry)\s+after\s+(?<t>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex NextAfterSeriesLastPattern();

    [GeneratedRegex(@"^(?:what(?:'s| is)?\s+(?:the\s+)?(?:(?:next\s+)?(?:book|novel|one)\s+)?(?:(?:is|comes|follows)\s+)?(?:next\s+)?|(?:the\s+)?(?:next\s+)?(?:book|novel|one)\s+(?:(?:is|comes)\s+)?(?:next\s+)?)after\s+(?<t>.+?)(?:\s+(?:in|from)\s+(?:the\s+)?(?<s>.+?)(?:\s+series)?)?$", RegexOptions.IgnoreCase)]
    private static partial Regex AfterTitlePattern();

    [GeneratedRegex(@"^(\d{1,2})(?:st|nd|rd|th)$")]
    private static partial Regex NumericOrdinalPattern();
}
