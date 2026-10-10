using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Domain.Tests.Acquisition;

[TestClass]
public sealed class AcquisitionJobTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void ANewJobStartsCreatedWithNoCandidates()
    {
        var job = new AcquisitionJob(Guid.NewGuid(), RequestMediaType.Ebook, "manual", Now);

        Assert.AreEqual(AcquisitionJobStatus.Created, job.Status);
        Assert.HasCount(0, job.Candidates);
        Assert.IsNull(job.StartedAtUtc);
        Assert.IsNull(job.CompletedAtUtc);
    }

    [TestMethod]
    public void TransitioningToCandidateAcquiredSetsStartedAndCompleted()
    {
        var job = new AcquisitionJob(Guid.NewGuid(), RequestMediaType.Ebook, "manual", Now);

        job.TransitionTo(AcquisitionJobStatus.CandidateAcquired, Now.AddMinutes(1));

        Assert.AreEqual(AcquisitionJobStatus.CandidateAcquired, job.Status);
        Assert.AreEqual(Now.AddMinutes(1), job.StartedAtUtc);
        Assert.AreEqual(Now.AddMinutes(1), job.CompletedAtUtc);
    }

    [TestMethod]
    public void TransitioningToFailedRecordsTheReason()
    {
        var job = new AcquisitionJob(Guid.NewGuid(), RequestMediaType.Ebook, "manual", Now);

        job.TransitionTo(AcquisitionJobStatus.Failed, Now.AddMinutes(1), "checksum mismatch");

        Assert.AreEqual("checksum mismatch", job.FailureReason);
    }

    [TestMethod]
    public void ATerminalStatusCannotTransitionFurther()
    {
        var job = new AcquisitionJob(Guid.NewGuid(), RequestMediaType.Ebook, "manual", Now);
        job.TransitionTo(AcquisitionJobStatus.CandidateAcquired, Now.AddMinutes(1));

        Assert.ThrowsExactly<InvalidAcquisitionJobTransitionException>(() =>
            job.TransitionTo(AcquisitionJobStatus.InProgress, Now.AddMinutes(2)));
    }

    [TestMethod]
    public void AddingACandidateRecordsItAgainstTheJob()
    {
        var job = new AcquisitionJob(Guid.NewGuid(), RequestMediaType.Ebook, "manual", Now);

        var candidate = job.AddCandidate(
            "manual", "stored-file.epub", null, null, ".epub", 1024, null, null, null, null, Now);

        Assert.HasCount(1, job.Candidates);
        Assert.AreEqual(AcquisitionCandidateStatus.Discovered, candidate.Status);
        Assert.AreEqual(job.Id, candidate.AcquisitionJobId);
    }

    [TestMethod]
    public void MarkingAnUnknownCandidateStatusThrows()
    {
        var job = new AcquisitionJob(Guid.NewGuid(), RequestMediaType.Ebook, "manual", Now);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            job.MarkCandidateStatus(Guid.NewGuid(), AcquisitionCandidateStatus.Acquired, Now));
    }

    [TestMethod]
    public void MarkingACandidateStatusUpdatesIt()
    {
        var job = new AcquisitionJob(Guid.NewGuid(), RequestMediaType.Ebook, "manual", Now);
        var candidate = job.AddCandidate(
            "manual", "stored-file.epub", null, null, ".epub", 1024, null, null, null, null, Now);

        job.MarkCandidateStatus(candidate.Id, AcquisitionCandidateStatus.Acquired, Now.AddMinutes(1));

        Assert.AreEqual(AcquisitionCandidateStatus.Acquired, candidate.Status);
    }

    [TestMethod]
    public void ProviderInteractionControlUrlIsNotPersisted()
    {
        var job = new ProviderAcquisitionJob(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "example-source", null,
            "idempotency-key", "candidate-reference", null, null, Now);

        job.ApplyStatus(
            ProviderAcquisitionJobLifecycleState.Waiting,
            "user-interaction",
            "browser",
            "Complete the check at source.example.test.",
            Now.AddMinutes(10),
            true,
            "https://provider.example.test/acquire/job/interaction?bearer=not-for-a-browser",
            null, null, null, null,
            Now.AddMinutes(1),
            Now);

        Assert.IsNull(job.InteractionActionUrl);
        Assert.AreEqual("Complete the check at source.example.test.", job.InteractionMessage);
    }
}
