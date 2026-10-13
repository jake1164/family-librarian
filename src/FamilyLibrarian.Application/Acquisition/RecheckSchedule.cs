namespace FamilyLibrarian.Application.Acquisition;

/// <summary>
/// AF-10: how often to look again for a request that is waiting on a person, or that has results but none acceptable. The scale
/// slows down and never stops, measured from the catalog publication date when that is still ahead of the request, otherwise
/// from the request date: a book not yet published is looked for as its release nears, a new one daily, an obscure one
/// that surfaces years late still gets found.
/// </summary>
public static class RecheckSchedule
{
    /// <summary>The longest wait between looks, reached a year after the anchor.</summary>
    public static readonly TimeSpan MaximumInterval = TimeSpan.FromDays(30);

    private static readonly TimeSpan MaximumPreReleaseInterval = TimeSpan.FromDays(7);

    /// <summary>
    /// Before the anchor (pre-release): the days left, clamped to one to seven. After it: daily for the first 14 days, every 3
    /// days to 90 days, weekly to a year, then monthly. Whole days, in UTC.
    /// </summary>
    public static TimeSpan WaitingInterval(DateOnly? publicationDate, DateTimeOffset requestedAtUtc, DateTimeOffset now)
    {
        var requestedOn = DateOnly.FromDateTime(requestedAtUtc.UtcDateTime);
        var anchor = publicationDate is { } published && published > requestedOn ? published : requestedOn;
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        var daysUntil = anchor.DayNumber - today.DayNumber;
        if (daysUntil > 0)
        {
            return TimeSpan.FromDays(Math.Clamp(daysUntil, 1, (int)MaximumPreReleaseInterval.TotalDays));
        }

        return -daysUntil switch
        {
            < 14 => TimeSpan.FromDays(1),
            < 90 => TimeSpan.FromDays(3),
            < 365 => TimeSpan.FromDays(7),
            _ => MaximumInterval
        };
    }
}
