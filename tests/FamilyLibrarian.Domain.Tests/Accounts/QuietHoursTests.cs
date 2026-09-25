using FamilyLibrarian.Domain.Accounts;

namespace FamilyLibrarian.Domain.Tests.Accounts;

[TestClass]
public sealed class QuietHoursTests
{
    private const string NewYork = "America/New_York";

    [TestMethod]
    public void ANormalWindowIsQuietOnlyBetweenStartAndEnd()
    {
        // 09:00-17:00 America/New_York. Jan 15 2026 is EST (UTC-5).
        var hours = new QuietHours(NewYork, startMinute: 9 * 60, endMinute: 17 * 60);

        Assert.IsFalse(hours.IsQuietAt(new DateTimeOffset(2026, 1, 15, 13, 0, 0, TimeSpan.Zero))); // 08:00 local
        Assert.IsTrue(hours.IsQuietAt(new DateTimeOffset(2026, 1, 15, 15, 0, 0, TimeSpan.Zero))); // 10:00 local
        Assert.IsFalse(hours.IsQuietAt(new DateTimeOffset(2026, 1, 15, 22, 0, 0, TimeSpan.Zero))); // 17:00 local (boundary, exclusive)
    }

    [TestMethod]
    public void ACrossMidnightWindowWrapsAroundLocalMidnight()
    {
        // 22:00-07:00 America/New_York. Jan 15 2026 is EST (UTC-5).
        var hours = new QuietHours(NewYork, startMinute: 22 * 60, endMinute: 7 * 60);

        Assert.IsTrue(hours.IsQuietAt(new DateTimeOffset(2026, 1, 15, 4, 0, 0, TimeSpan.Zero))); // 23:00 local (prev day)
        Assert.IsTrue(hours.IsQuietAt(new DateTimeOffset(2026, 1, 15, 10, 0, 0, TimeSpan.Zero))); // 05:00 local
        Assert.IsFalse(hours.IsQuietAt(new DateTimeOffset(2026, 1, 15, 15, 0, 0, TimeSpan.Zero))); // 10:00 local
    }

    [TestMethod]
    public void ASpringForwardTransitionUsesTheCorrectPostTransitionOffset()
    {
        // 22:00-07:00 America/New_York. Clocks spring forward 2:00am->3:00am on
        // 2026-03-08 (transition instant 2026-03-08T07:00:00Z, EST -5 -> EDT -4).
        var hours = new QuietHours(NewYork, startMinute: 22 * 60, endMinute: 7 * 60);

        // 2026-03-08T11:01:00Z is 07:01 local under the correct post-transition
        // EDT (-4) offset -- just past the 07:00 end, so not quiet. Using the
        // stale EST (-5) offset would wrongly compute 06:01 local (still quiet).
        Assert.IsFalse(hours.IsQuietAt(new DateTimeOffset(2026, 3, 8, 11, 1, 0, TimeSpan.Zero)));
    }

    [TestMethod]
    public void AFallBackTransitionUsesTheCorrectPostTransitionOffset()
    {
        // 22:00-07:00 America/New_York. Clocks fall back 2:00am->1:00am on
        // 2026-11-01 (transition instant 2026-11-01T06:00:00Z, EDT -4 -> EST -5).
        var hours = new QuietHours(NewYork, startMinute: 22 * 60, endMinute: 7 * 60);

        // 2026-11-01T11:30:00Z is 06:30 local under the correct post-transition
        // EST (-5) offset -- still before the 07:00 end, so quiet. Using the
        // stale EDT (-4) offset would wrongly compute 07:30 local (not quiet).
        Assert.IsTrue(hours.IsQuietAt(new DateTimeOffset(2026, 11, 1, 11, 30, 0, TimeSpan.Zero)));
    }

    [TestMethod]
    public void NextEndAfterReturnsTodaysEndWhenBeforeIt()
    {
        var hours = new QuietHours(NewYork, startMinute: 9 * 60, endMinute: 17 * 60);

        var next = hours.NextEndAfter(new DateTimeOffset(2026, 1, 15, 15, 0, 0, TimeSpan.Zero)); // 10:00 local

        Assert.AreEqual(new DateTimeOffset(2026, 1, 15, 22, 0, 0, TimeSpan.Zero), next); // 17:00 local == 22:00Z
    }

    [TestMethod]
    public void NextEndAfterReturnsTomorrowsEndWhenPastToday()
    {
        var hours = new QuietHours(NewYork, startMinute: 9 * 60, endMinute: 17 * 60);

        var next = hours.NextEndAfter(new DateTimeOffset(2026, 1, 15, 23, 0, 0, TimeSpan.Zero)); // 18:00 local

        Assert.AreEqual(new DateTimeOffset(2026, 1, 16, 22, 0, 0, TimeSpan.Zero), next); // tomorrow 17:00 local
    }

    [TestMethod]
    public void AnInvalidTimeZoneIdIsRejected()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new QuietHours("Not/A_Real_Zone", 60, 120));
    }

    [TestMethod]
    public void StartAndEndMustDiffer()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new QuietHours(NewYork, 60, 60));
    }
}
