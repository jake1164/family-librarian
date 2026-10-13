using FamilyLibrarian.Application.Acquisition;

namespace FamilyLibrarian.Infrastructure.Tests.Acquisition;

/// <summary>AF-10: how often a request that is waiting on a person, or has no acceptable copy yet, is looked at again.</summary>
[TestClass]
public sealed class RecheckScheduleTests
{
    private static readonly DateTimeOffset Requested = new(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset On(int year, int month, int day) => new(year, month, day, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    [DataRow(2026, 10, 10, 1, DisplayName = "5 days after the request: daily")]
    [DataRow(2026, 10, 18, 1, DisplayName = "13 days: still daily")]
    [DataRow(2026, 10, 19, 3, DisplayName = "14 days: every 3 days")]
    [DataRow(2026, 11, 20, 3, DisplayName = "46 days: every 3 days")]
    [DataRow(2027, 1, 4, 7, DisplayName = "91 days: weekly")]
    [DataRow(2027, 3, 1, 7, DisplayName = "147 days: weekly")]
    [DataRow(2027, 10, 5, 30, DisplayName = "a year: monthly")]
    [DataRow(2028, 1, 1, 30, DisplayName = "over a year: monthly")]
    public void AnAlreadyPublishedBookIsAnchoredOnTheRequestDate(int year, int month, int day, int expectedDays)
    {
        // Published before it was requested: the request date is the anchor.
        var interval = RecheckSchedule.WaitingInterval(new DateOnly(2026, 10, 1), Requested, On(year, month, day));

        Assert.AreEqual(TimeSpan.FromDays(expectedDays), interval);
    }

    [TestMethod]
    public void AnUnknownPublicationDateIsAnchoredOnTheRequestDate()
    {
        Assert.AreEqual(TimeSpan.FromDays(1), RecheckSchedule.WaitingInterval(null, Requested, On(2026, 10, 10)));
        Assert.AreEqual(TimeSpan.FromDays(3), RecheckSchedule.WaitingInterval(null, Requested, On(2026, 11, 20)));
    }

    [TestMethod]
    public void AFuturePublicationDateIsTheAnchorAndTheIntervalIsTheDaysLeftBetweenOneAndSeven()
    {
        var publication = new DateOnly(2026, 12, 1);

        Assert.AreEqual(TimeSpan.FromDays(7), RecheckSchedule.WaitingInterval(publication, Requested, On(2026, 10, 10)));
        Assert.AreEqual(TimeSpan.FromDays(2), RecheckSchedule.WaitingInterval(publication, Requested, On(2026, 11, 29)));
        Assert.AreEqual(TimeSpan.FromDays(1), RecheckSchedule.WaitingInterval(publication, Requested, On(2026, 11, 30)));
    }

    [TestMethod]
    public void AfterAFuturePublicationDatePassesTheScaleRunsFromThatDate()
    {
        var publication = new DateOnly(2026, 12, 1);

        Assert.AreEqual(TimeSpan.FromDays(1), RecheckSchedule.WaitingInterval(publication, Requested, On(2026, 12, 5)));
        Assert.AreEqual(TimeSpan.FromDays(3), RecheckSchedule.WaitingInterval(publication, Requested, On(2026, 12, 20)));
    }

    [TestMethod]
    public void TheScaleSlowsDownAndNeverStops()
    {
        var previous = TimeSpan.Zero;
        for (var days = 0; days < 3000; days += 7)
        {
            var interval = RecheckSchedule.WaitingInterval(null, Requested, Requested.AddDays(days));
            Assert.IsGreaterThanOrEqualTo(previous, interval, $"day {days}");
            Assert.IsGreaterThan(TimeSpan.Zero, interval);
            previous = interval;
        }

        Assert.AreEqual(RecheckSchedule.MaximumInterval, previous);
    }
}
