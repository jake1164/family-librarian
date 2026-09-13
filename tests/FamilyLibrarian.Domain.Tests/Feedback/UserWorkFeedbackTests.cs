using FamilyLibrarian.Domain.Feedback;

namespace FamilyLibrarian.Domain.Tests.Feedback;

/// <summary>
/// The feedback rules that must hold no matter which caller reaches them.
/// </summary>
[TestClass]
public sealed class UserWorkFeedbackTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 8, 14, 9, 0, 0, TimeSpan.Zero);

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid WorkId = Guid.NewGuid();
    private static readonly DateOnly CompletedOn = new(2026, 8, 1);

    [TestMethod]
    public void ANewFeedbackRecordsTheCompletionDate()
    {
        var feedback = new UserWorkFeedback(UserId, WorkId, CompletedOn, CreatedAt);

        Assert.AreEqual(UserId, feedback.UserId);
        Assert.AreEqual(WorkId, feedback.WorkId);
        Assert.AreEqual(CompletedOn, feedback.CompletedOn);
        Assert.AreEqual(CreatedAt, feedback.CreatedAtUtc);
        Assert.AreEqual(CreatedAt, feedback.UpdatedAtUtc);
    }

    [TestMethod]
    public void AnEmptyUserIdIsRejected() =>
        Assert.ThrowsExactly<ArgumentException>(() =>
            new UserWorkFeedback(Guid.Empty, WorkId, CompletedOn, CreatedAt));

    [TestMethod]
    public void AnEmptyWorkIdIsRejected() =>
        Assert.ThrowsExactly<ArgumentException>(() =>
            new UserWorkFeedback(UserId, Guid.Empty, CompletedOn, CreatedAt));

    [TestMethod]
    public void CorrectingUpdatesTheDateAndTimestampWithoutChangingIdentity()
    {
        var feedback = new UserWorkFeedback(UserId, WorkId, CompletedOn, CreatedAt);
        var correctedAt = CreatedAt.AddDays(1);

        feedback.Correct(CompletedOn.AddDays(1), correctedAt);

        Assert.AreEqual(CompletedOn.AddDays(1), feedback.CompletedOn);
        Assert.AreEqual(CreatedAt, feedback.CreatedAtUtc);
        Assert.AreEqual(correctedAt, feedback.UpdatedAtUtc);
        Assert.AreEqual(UserId, feedback.UserId);
        Assert.AreEqual(WorkId, feedback.WorkId);
    }
}
