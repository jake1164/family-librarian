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

    /// <summary>The precision to record beside a parsed date: null for a full date, so the historical meaning of a date is unchanged.</summary>
    internal static DatePrecision? Stated(ParsedDate? parsed) => parsed?.Precision is DatePrecision.Day ? null : parsed?.Precision;
}
