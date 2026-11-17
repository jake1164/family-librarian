using FamilyLibrarian.Domain.Catalog;

namespace FamilyLibrarian.Application.Catalog.Identity;

/// <summary>
/// Decides whether a publication date means the book has been published, from the date, how much of it a source stated and today.
/// A future date is never "published"; a coarse date whose whole interval has not yet ended is never called either way.
/// </summary>
public static class PublicationStatusPolicy
{
    /// <param name="date">The date as stored: a partial date is its first day.</param>
    /// <param name="precision">How much of the date a source stated; null is a full date (the historical meaning).</param>
    /// <param name="today">The current day, from the injected clock.</param>
    public static PublicationStatus Evaluate(DateOnly? date, DatePrecision? precision, DateOnly today)
    {
        if (date is not { } start)
        {
            return PublicationStatus.Unknown;
        }

        var (first, last) = Interval(start, precision);
        if (last < today)
        {
            return PublicationStatus.Published;
        }

        if (first > today)
        {
            return PublicationStatus.Forthcoming;
        }

        // A full date that is today has happened; a month or year that merely contains today has not yet ended, so nothing is claimed.
        return precision is null or DatePrecision.Day ? PublicationStatus.Published : PublicationStatus.Unknown;
    }

    /// <summary>The days a date could mean: the day itself, the whole month, or the whole year.</summary>
    public static (DateOnly First, DateOnly Last) Interval(DateOnly date, DatePrecision? precision) =>
        precision switch
        {
            DatePrecision.Year => (new DateOnly(date.Year, 1, 1), new DateOnly(date.Year, 12, 31)),
            DatePrecision.Month => (new DateOnly(date.Year, date.Month, 1),
                new DateOnly(date.Year, date.Month, DateTime.DaysInMonth(date.Year, date.Month))),
            _ => (date, date)
        };
}
