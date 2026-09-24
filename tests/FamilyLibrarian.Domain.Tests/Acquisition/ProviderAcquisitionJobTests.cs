using FamilyLibrarian.Domain.Acquisition;

namespace FamilyLibrarian.Domain.Tests.Acquisition;

[TestClass]
public sealed class ProviderAcquisitionJobTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    private static ProviderAcquisitionJob NewJob() =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "annas", null, "idem-key", "candidate-ref", null, null, Now);

    private static ProviderAcquisitionJob NewWaitingJob()
    {
        var job = NewJob();
        job.RecordSubmission("provider-job-1", ProviderAcquisitionJobLifecycleState.Waiting, Now, Now);
        return job;
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
}
