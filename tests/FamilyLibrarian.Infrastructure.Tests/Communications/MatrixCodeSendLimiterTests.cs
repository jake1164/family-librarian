using FamilyLibrarian.Application.Communications;

namespace FamilyLibrarian.Infrastructure.Tests.Communications;

[TestClass]
public sealed class MatrixCodeSendLimiterTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void FiveSendsAreAllowedInAnyFifteenMinutesAndEachExpiresOnItsOwn()
    {
        var limiter = new MatrixCodeSendLimiter();
        var member = Guid.NewGuid();
        for (var minute = 0; minute < 5; minute++)
        {
            Assert.IsTrue(limiter.TryRecord(member, Start.AddMinutes(minute), out _), $"send {minute + 1}");
        }

        Assert.IsFalse(limiter.TryRecord(member, Start.AddMinutes(5), out var wait));
        Assert.AreEqual(TimeSpan.FromMinutes(10), wait);
        // Asking again while refused does not push the wait out.
        Assert.IsFalse(limiter.TryRecord(member, Start.AddMinutes(14.5), out wait));
        Assert.AreEqual(TimeSpan.FromSeconds(30), wait);

        // The first send is now 15 minutes old, so exactly one more is allowed.
        Assert.IsTrue(limiter.TryRecord(member, Start.AddMinutes(15), out _));
        Assert.IsFalse(limiter.TryRecord(member, Start.AddMinutes(15), out wait));
        Assert.AreEqual(TimeSpan.FromMinutes(1), wait);
    }

    [TestMethod]
    public void OneMembersLimitDoesNotAffectAnother()
    {
        var limiter = new MatrixCodeSendLimiter();
        var member = Guid.NewGuid();
        for (var send = 0; send < MatrixCodeSendLimiter.MaxSends; send++)
        {
            limiter.TryRecord(member, Start, out _);
        }

        Assert.IsFalse(limiter.TryRecord(member, Start, out _));
        Assert.IsTrue(limiter.TryRecord(Guid.NewGuid(), Start, out _));
    }

    [TestMethod]
    [DataRow(15 * 60, "about 15 minutes")]
    [DataRow(61, "about 2 minutes")]
    [DataRow(60, "about 1 minute.")]
    [DataRow(1, "about 1 minute.")]
    public void TheMessageSaysItIsALimitAndNeverPromisesTooShortAWait(int seconds, string expected)
    {
        var message = MatrixCodeSendLimiter.LimitMessage(TimeSpan.FromSeconds(seconds));

        StringAssert.Contains(message, "which is the limit. Nothing is broken");
        StringAssert.Contains(message, expected);
    }
}
