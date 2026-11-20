using System.Globalization;
using FamilyLibrarian.Domain.Catalog;

namespace FamilyLibrarian.Infrastructure.Metadata;

/// <summary>
/// Reads a provider's publication date without inventing precision it did not state. A full date keeps its day; a year-month or a
/// year alone is kept as the first day of that interval together with <see cref="DatePrecision.Month"/> or
/// <see cref="DatePrecision.Year"/>, so nothing downstream mistakes it for an exact day.
/// </summary>
internal static class PublicationDateParser
{
    private static readonly string[] MonthFormats = ["yyyy-MM", "MMMM yyyy", "MMM yyyy"];
    private static readonly string[] HardcoverFullDateFormats = ["yyyy-MM-dd"];

    internal readonly record struct ParsedDate(DateOnly Date, DatePrecision Precision);

    /// <param name="value">The provider's text.</param>
    /// <param name="fullDateFormats">The exact day formats this provider is known to use.</param>
    internal static ParsedDate? Parse(string? value, IReadOnlyList<string> fullDateFormats)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (DateOnly.TryParseExact(text, fullDateFormats.ToArray(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            return new ParsedDate(day, DatePrecision.Day);
        }

        if (DateOnly.TryParseExact(text, MonthFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var month))
        {
            return new ParsedDate(new DateOnly(month.Year, month.Month, 1), DatePrecision.Month);
        }

        if (text.Length == 4 && text.All(char.IsAsciiDigit) &&
            int.Parse(text, CultureInfo.InvariantCulture) is >= 1 and <= 9999)
        {
            return new ParsedDate(new DateOnly(int.Parse(text, CultureInfo.InvariantCulture), 1, 1), DatePrecision.Year);
        }

        return null;
    }

    /// <summary>
    /// Hardcover's <c>release_date</c>. Its published-date guidance lets a source that knows only a year record January 1 and one that
    /// knows only a month record that month's first day, so a full ISO date cannot be read as an exact day on its own. The date
    /// value is kept exactly as stated; only the precision is bounded conservatively: January 1 is no more precise than the
    /// year, any other first of a month no more precise than the month, every other full date a day. This is a safe bound, not a
    /// claim that a given value was a placeholder (a book really released on January 1 is under-claimed, never over-claimed).
    /// An explicitly partial string ("1965", "1965-06") already carries its own precision and is unchanged. One policy for the
    /// ordinary Work mapping, the Edition mapping and discovery, so they cannot disagree.
    /// </summary>
    internal static ParsedDate? ParseHardcoverRelease(string? value)
    {
        var parsed = Parse(value, HardcoverFullDateFormats);
        if (parsed is not { Precision: DatePrecision.Day } day)
        {
            return parsed;
        }

        return day.Date switch
        {
            { Month: 1, Day: 1 } => day with { Precision = DatePrecision.Year },
            { Day: 1 } => day with { Precision = DatePrecision.Month },
            _ => day
        };
    }

    /// <summary>The precision to record beside a parsed date: null for a full date, so the historical meaning of a date is unchanged.</summary>
    internal static DatePrecision? Stated(ParsedDate? parsed) => parsed?.Precision is DatePrecision.Day ? null : parsed?.Precision;
}
