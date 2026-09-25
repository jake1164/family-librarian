namespace FamilyLibrarian.Domain.Accounts;

/// <summary>
/// A household member's own daily "do not disturb" window, expressed in their
/// own local time zone (HUMAN-ACQ-1 D9). Stored as a general per-user setting;
/// as of this slice it is enforced only by the Matrix verification alert — see
/// <c>.ai_docs/human-acq-1-matrix-authorize-plan.md</c> and the deferred
/// <c>NOTIFY-QUIET-1</c> master-plan item for extending it to every notification.
/// </summary>
/// <remarks>
/// <see cref="StartMinute"/>/<see cref="EndMinute"/> are minutes after local
/// midnight (0-1439). The window crosses midnight whenever
/// <c>StartMinute &gt; EndMinute</c> (e.g. 22:00-07:00).
/// </remarks>
public sealed class QuietHours
{
    public const int MinutesPerDay = 24 * 60;

    public QuietHours(string timeZoneId, int startMinute, int endMinute)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(timeZoneId);

        if (startMinute is < 0 or >= MinutesPerDay)
        {
            throw new ArgumentOutOfRangeException(nameof(startMinute), startMinute, "Must be between 0 and 1439.");
        }

        if (endMinute is < 0 or >= MinutesPerDay)
        {
            throw new ArgumentOutOfRangeException(nameof(endMinute), endMinute, "Must be between 0 and 1439.");
        }

        if (startMinute == endMinute)
        {
            throw new ArgumentException("A quiet-hours window cannot start and end at the same minute.", nameof(endMinute));
        }

        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new ArgumentException($"'{timeZoneId}' is not a resolvable time zone.", nameof(timeZoneId), ex);
        }

        TimeZoneId = timeZoneId.Trim();
        StartMinute = startMinute;
        EndMinute = endMinute;
    }

    public string TimeZoneId { get; }

    public int StartMinute { get; }

    public int EndMinute { get; }

    /// <summary>Whether the window crosses midnight (e.g. 22:00-07:00).</summary>
    public bool CrossesMidnight => StartMinute > EndMinute;

    public bool IsQuietAt(DateTimeOffset utc)
    {
        var localMinuteOfDay = MinuteOfDayAt(utc);

        return CrossesMidnight
            ? localMinuteOfDay >= StartMinute || localMinuteOfDay < EndMinute
            : localMinuteOfDay >= StartMinute && localMinuteOfDay < EndMinute;
    }

    /// <summary>
    /// The end of this window's current-or-upcoming quiet period: if
    /// <paramref name="utc"/> falls before today's local end-of-window minute, that
    /// end; otherwise tomorrow's. This single rule holds for both a same-day window
    /// (whether currently quiet or not-yet-started today) and a midnight-crossing one
    /// (whether continuing from yesterday or started today) — in every case the next
    /// occurrence of the local end-minute is the answer.
    /// </summary>
    public DateTimeOffset NextEndAfter(DateTimeOffset utc)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
        var local = TimeZoneInfo.ConvertTime(utc, zone);
        var today = DateOnly.FromDateTime(local.Date);
        var minuteOfDay = (local.Hour * 60) + local.Minute;

        return minuteOfDay >= EndMinute
            ? AtLocalMinute(zone, today.AddDays(1), EndMinute)
            : AtLocalMinute(zone, today, EndMinute);
    }

    private static DateTimeOffset AtLocalMinute(TimeZoneInfo zone, DateOnly date, int minuteOfDay)
    {
        var unspecified = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue).AddMinutes(minuteOfDay), DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified));
    }

    private int MinuteOfDayAt(DateTimeOffset utc)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
        var local = TimeZoneInfo.ConvertTime(utc, zone);
        return (local.Hour * 60) + local.Minute;
    }
}
