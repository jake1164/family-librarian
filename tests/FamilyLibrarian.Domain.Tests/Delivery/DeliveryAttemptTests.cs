using FamilyLibrarian.Domain.Delivery;

namespace FamilyLibrarian.Domain.Tests.Delivery;

[TestClass]
public sealed class DeliveryAttemptTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void ANewAttemptStartsPendingWithNoCompletion()
    {
        var attempt = CreateAttempt();

        Assert.AreEqual(DeliveryAttemptStatus.Pending, attempt.Status);
        Assert.AreEqual(1, attempt.AttemptNumber);
        Assert.IsNull(attempt.StartedAtUtc);
        Assert.IsNull(attempt.CompletedAtUtc);
        Assert.IsFalse(attempt.IsRetryable);
    }

    [TestMethod]
    public void TransitioningToSubmittedSetsStartedAndCompleted()
    {
        var attempt = CreateAttempt();

        attempt.TransitionTo(DeliveryAttemptStatus.Submitting, Now.AddSeconds(1));
        attempt.TransitionTo(DeliveryAttemptStatus.Submitted, Now.AddSeconds(2));

        Assert.AreEqual(DeliveryAttemptStatus.Submitted, attempt.Status);
        Assert.AreEqual(Now.AddSeconds(1), attempt.StartedAtUtc);
        Assert.AreEqual(Now.AddSeconds(2), attempt.CompletedAtUtc);
        Assert.IsNull(attempt.FailureReason);
    }

    [TestMethod]
    public void TransitioningToFailedRecordsTheReasonAndRetryability()
    {
        var attempt = CreateAttempt();
        attempt.TransitionTo(DeliveryAttemptStatus.Submitting, Now.AddSeconds(1));

        attempt.TransitionTo(DeliveryAttemptStatus.Failed, Now.AddSeconds(2), "network timeout", retryable: true);

        Assert.AreEqual(DeliveryAttemptStatus.Failed, attempt.Status);
        Assert.AreEqual("network timeout", attempt.FailureReason);
        Assert.IsTrue(attempt.IsRetryable);
    }

    [TestMethod]
    public void ATerminalStatusCannotTransitionFurther()
    {
        var attempt = CreateAttempt();
        attempt.TransitionTo(DeliveryAttemptStatus.Submitting, Now.AddSeconds(1));
        attempt.TransitionTo(DeliveryAttemptStatus.Submitted, Now.AddSeconds(2));

        Assert.ThrowsExactly<InvalidDeliveryAttemptTransitionException>(() =>
            attempt.TransitionTo(DeliveryAttemptStatus.Submitting, Now.AddSeconds(3)));
    }

    [TestMethod]
    public void PendingCannotJumpDirectlyToSubmitted()
    {
        var attempt = CreateAttempt();

        Assert.ThrowsExactly<InvalidDeliveryAttemptTransitionException>(() =>
            attempt.TransitionTo(DeliveryAttemptStatus.Submitted, Now.AddSeconds(1)));
    }

    [TestMethod]
    public void ConstructionRequiresAPositiveAttemptNumber()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            new DeliveryAttempt(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "cwa", "42", "epub",
                convert: false, attemptNumber: 0, Now));
    }

    private static DeliveryAttempt CreateAttempt() =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "cwa", "42", "epub", convert: false, attemptNumber: 1, Now);
}
