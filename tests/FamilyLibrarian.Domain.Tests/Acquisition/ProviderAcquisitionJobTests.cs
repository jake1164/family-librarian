using FamilyLibrarian.Domain.Acquisition;

namespace FamilyLibrarian.Domain.Tests.Acquisition;

[TestClass]
public sealed class ProviderAcquisitionJobTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    private static ProviderAcquisitionJob NewJob() =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "example-source", null, "idem-key", "candidate-ref", null, null, Now);

    private static ProviderAcquisitionJob NewWaitingJob()
    {
        var job = NewJob();
        job.RecordSubmission("provider-job-1", ProviderAcquisitionJobLifecycleState.Waiting, Now, Now);
        return job;
    }

    [TestMethod]
    [DataRow(ProviderAcquisitionJobLifecycleState.Failed)]
    [DataRow(ProviderAcquisitionJobLifecycleState.Cancelled)]
    public void ATerminalSubmissionCanStillRecordTheLocalFailure(ProviderAcquisitionJobLifecycleState remoteState)
    {
        var job = NewJob();
        job.RecordSubmission("provider-job-1", remoteState, Now.AddMinutes(15), Now);

        job.RecordFailure("PROVIDER_CANCELLED", "The provider ended the job.", false, null, null, Now.AddSeconds(1));

        Assert.AreEqual("provider-job-1", job.ProviderJobId);
        Assert.AreEqual(ProviderAcquisitionJobLifecycleState.Failed, job.LifecycleState);
        Assert.IsNull(job.NextPollAtUtc);
    }

    [TestMethod]
    public void ALocallyCancelledJobCannotBeReopenedBySubmission()
    {
        var job = NewJob();
        job.Cancel("The administrator abandoned this job.", Now);

        Assert.ThrowsExactly<InvalidProviderAcquisitionJobTransitionException>(() =>
            job.RecordSubmission("provider-job-1", ProviderAcquisitionJobLifecycleState.Cancelled, Now, Now));

        Assert.AreEqual(ProviderAcquisitionJobLifecycleState.Cancelled, job.LifecycleState);
        Assert.IsNull(job.NextPollAtUtc);
    }

    [TestMethod]
    public void RecordingAnInteractionSessionStartRequiresTheJobToBeWaiting()
    {
        var job = NewJob(); // starts Queued

        Assert.ThrowsExactly<InvalidOperationException>(() => job.RecordInteractionSessionStarted(Now));
    }

    [TestMethod]
    public void RecordingAnInteractionSessionStartSetsTheTimestamp()
    {
        var job = NewWaitingJob();

        job.RecordInteractionSessionStarted(Now.AddSeconds(5));

        Assert.AreEqual(Now.AddSeconds(5), job.InteractionViewSessionStartedAtUtc);
    }

    [TestMethod]
    public void LeavingWaitingClearsTheInteractionSessionStart()
    {
        var job = NewWaitingJob();
        job.RecordInteractionSessionStarted(Now.AddSeconds(5));

        job.ApplyStatus(
            ProviderAcquisitionJobLifecycleState.Completed, phase: null,
            interactionType: null, interactionMessage: null, interactionExpiresAtUtc: null,
            interactionResumeSupported: null, interactionActionUrl: null,
            progressPercent: null, progressBytesCompleted: null, progressBytesTotal: null, progressMessage: null,
            nextPollAtUtc: null, atUtc: Now.AddSeconds(10));

        Assert.IsNull(job.InteractionViewSessionStartedAtUtc);
    }

    [TestMethod]
    public void StayingInWaitingKeepsTheInteractionSessionStart()
    {
        var job = NewWaitingJob();
        job.RecordInteractionSessionStarted(Now.AddSeconds(5));

        job.ApplyStatus(
            ProviderAcquisitionJobLifecycleState.Waiting, phase: "user-interaction",
            interactionType: "browser", interactionMessage: "still waiting", interactionExpiresAtUtc: Now.AddMinutes(10),
            interactionResumeSupported: true, interactionActionUrl: null,
            progressPercent: null, progressBytesCompleted: null, progressBytesTotal: null, progressMessage: null,
            nextPollAtUtc: Now.AddSeconds(15), atUtc: Now.AddSeconds(10));

        Assert.AreEqual(Now.AddSeconds(5), job.InteractionViewSessionStartedAtUtc);
    }

    [TestMethod]
    public void MovingIntoWaitingFromANonWaitingStateSetsWaitingSinceUtc()
    {
        var job = NewJob(); // starts Queued

        job.RecordSubmission("provider-job-1", ProviderAcquisitionJobLifecycleState.Waiting, Now, Now.AddSeconds(1));

        Assert.AreEqual(Now.AddSeconds(1), job.WaitingSinceUtc);
        Assert.IsNull(job.LeftWaitingAtUtc);
    }

    [TestMethod]
    public void MovingOutOfWaitingSetsLeftWaitingAtUtcAndClearsWaitingSinceUtc()
    {
        var job = NewWaitingJob();

        job.ApplyStatus(
            ProviderAcquisitionJobLifecycleState.Completed, phase: null,
            interactionType: null, interactionMessage: null, interactionExpiresAtUtc: null,
            interactionResumeSupported: null, interactionActionUrl: null,
            progressPercent: null, progressBytesCompleted: null, progressBytesTotal: null, progressMessage: null,
            nextPollAtUtc: null, atUtc: Now.AddSeconds(10));

        Assert.IsNull(job.WaitingSinceUtc);
        Assert.AreEqual(Now.AddSeconds(10), job.LeftWaitingAtUtc);
    }

    [TestMethod]
    public void StayingInWaitingChangesNeitherWaitingTimestamp()
    {
        var job = NewWaitingJob(); // WaitingSinceUtc == Now

        job.ApplyStatus(
            ProviderAcquisitionJobLifecycleState.Waiting, phase: "user-interaction",
            interactionType: "browser", interactionMessage: "still waiting", interactionExpiresAtUtc: Now.AddMinutes(10),
            interactionResumeSupported: true, interactionActionUrl: null,
            progressPercent: null, progressBytesCompleted: null, progressBytesTotal: null, progressMessage: null,
            nextPollAtUtc: Now.AddSeconds(15), atUtc: Now.AddSeconds(10));

        Assert.AreEqual(Now, job.WaitingSinceUtc);
        Assert.IsNull(job.LeftWaitingAtUtc);
    }
}
