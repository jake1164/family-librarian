using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Domain.Acquisition;

namespace FamilyLibrarian.Infrastructure.Tests.Acquisition;

[TestClass]
public sealed class ProviderAttemptDisplayTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 14, 19, 37, 0, TimeSpan.Zero);
    private static readonly Guid Format = Guid.NewGuid();
    private static readonly Guid OtherFormat = Guid.NewGuid();

    private static ProviderAttempt Row(
        ProviderAttemptOutcome outcome, int minutes, string provider = "fetch", Guid? format = null, Guid? job = null) =>
        new(Guid.NewGuid(), format ?? Format, provider, outcome, "summary", T0.AddMinutes(minutes), null, job);

    private static string Shown(ProviderAttempt row, params ProviderAttempt[] others) =>
        ProviderAttemptDisplay.Derive([row, .. others], [])[row.Id];

    private static string Shown(ProviderAttempt row, AttemptJobFacts[] jobs, params ProviderAttempt[] others) =>
        ProviderAttemptDisplay.Derive([row, .. others], jobs)[row.Id];

    // The lab: both rows showed "In progress" long after the download had ended.
    [TestMethod]
    [DataRow(ProviderAcquisitionJobLifecycleState.Queued, "Submitted")]
    [DataRow(ProviderAcquisitionJobLifecycleState.Running, "Submitted")]
    [DataRow(ProviderAcquisitionJobLifecycleState.Waiting, "Submitted")]
    [DataRow(ProviderAcquisitionJobLifecycleState.Completed, ProviderAttemptDisplay.Finished)]
    [DataRow(ProviderAcquisitionJobLifecycleState.Failed, ProviderAttemptDisplay.Finished)]
    [DataRow(ProviderAcquisitionJobLifecycleState.Cancelled, ProviderAttemptDisplay.Finished)]
    public void ASubmittedRowFollowsItsLinkedJob(ProviderAcquisitionJobLifecycleState state, string expected)
    {
        var jobId = Guid.NewGuid();
        var row = Row(ProviderAttemptOutcome.Submitted, 0, job: jobId);
        Assert.AreEqual(expected, Shown(row, [new AttemptJobFacts(jobId, state, null)]));
    }

    [TestMethod]
    public void ALinkedJobDecidesEvenWhenALaterRowExists()
    {
        var jobId = Guid.NewGuid();
        var row = Row(ProviderAttemptOutcome.Submitted, 0, job: jobId);
        var later = Row(ProviderAttemptOutcome.NoMatch, 5);
        Assert.AreEqual("Submitted", Shown(row, [new AttemptJobFacts(jobId, ProviderAcquisitionJobLifecycleState.Running, null)], later));
    }

    [TestMethod]
    public void APartSetIsRunningWhileAnyMemberIs()
    {
        var setId = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var row = Row(ProviderAttemptOutcome.Submitted, 0, job: first);
        var running = new[]
        {
            new AttemptJobFacts(first, ProviderAcquisitionJobLifecycleState.Completed, setId),
            new AttemptJobFacts(second, ProviderAcquisitionJobLifecycleState.Running, setId)
        };
        var ended = new[]
        {
            new AttemptJobFacts(first, ProviderAcquisitionJobLifecycleState.Completed, setId),
            new AttemptJobFacts(second, ProviderAcquisitionJobLifecycleState.Failed, setId)
        };
        Assert.AreEqual("Submitted", Shown(row, running));
        Assert.AreEqual(ProviderAttemptDisplay.Finished, Shown(row, ended));
    }

    [TestMethod]
    public void WithNoLinkARowIsFinishedOnceTheSameSourceHasALaterRowForTheFormat()
    {
        var row = Row(ProviderAttemptOutcome.Submitted, 0);
        Assert.AreEqual(ProviderAttemptDisplay.Finished, Shown(row, Row(ProviderAttemptOutcome.Retrying, 1)));
        Assert.AreEqual(ProviderAttemptDisplay.Finished, Shown(row, Row(ProviderAttemptOutcome.Acquired, 4, provider: "FETCH")));
    }

    // Why the legacy fallback requires the same source and the same format.
    [TestMethod]
    public void WithNoLinkAnotherSourcesOrAnotherFormatsLaterRowDoesNotEndIt()
    {
        var row = Row(ProviderAttemptOutcome.Submitted, 0);
        Assert.AreEqual("Submitted", Shown(row, Row(ProviderAttemptOutcome.NoMatch, 1, provider: "annas")));
        Assert.AreEqual("Submitted", Shown(row, Row(ProviderAttemptOutcome.Acquired, 1, format: OtherFormat)));
        Assert.AreEqual("Submitted", Shown(row), "Alone and unlinked, it may still be running.");
    }

    [TestMethod]
    public void ALinkedRowWhoseJobIsGoneFallsBackToTheLaterRowRule()
    {
        var row = Row(ProviderAttemptOutcome.Submitted, 0, job: Guid.NewGuid());
        Assert.AreEqual(ProviderAttemptDisplay.Finished, Shown(row, Row(ProviderAttemptOutcome.Failed, 3)));
        Assert.AreEqual("Submitted", Shown(row));
    }

    [TestMethod]
    public void ARetryingRowIsMovedOnOnceAnyLaterRowForTheFormatExists()
    {
        var row = Row(ProviderAttemptOutcome.Retrying, 0);
        Assert.AreEqual(ProviderAttemptDisplay.MovedOn, Shown(row, Row(ProviderAttemptOutcome.Submitted, 1, provider: "annas")));
        Assert.AreEqual("Retrying", Shown(row), "As the newest row for the format it is still the current step.");
        Assert.AreEqual("Retrying", Shown(row, Row(ProviderAttemptOutcome.NoMatch, 1, format: OtherFormat)));
    }

    [TestMethod]
    [DataRow(ProviderAttemptOutcome.NoMatch)]
    [DataRow(ProviderAttemptOutcome.CandidatesFound)]
    [DataRow(ProviderAttemptOutcome.Acquired)]
    [DataRow(ProviderAttemptOutcome.Failed)]
    [DataRow(ProviderAttemptOutcome.Blocked)]
    [DataRow(ProviderAttemptOutcome.Paused)]
    [DataRow(ProviderAttemptOutcome.Resumed)]
    public void EveryOtherOutcomeIsShownAsStored(ProviderAttemptOutcome outcome)
    {
        var row = Row(outcome, 0);
        Assert.AreEqual(outcome.ToString(), Shown(row, Row(ProviderAttemptOutcome.Submitted, 5)));
    }

    [TestMethod]
    public void DerivingNeverChangesAStoredRow()
    {
        var jobId = Guid.NewGuid();
        var submitted = Row(ProviderAttemptOutcome.Submitted, 0, job: jobId);
        var retrying = Row(ProviderAttemptOutcome.Retrying, 1);
        var later = Row(ProviderAttemptOutcome.Acquired, 2);
        _ = ProviderAttemptDisplay.Derive([submitted, retrying, later], [new AttemptJobFacts(jobId, ProviderAcquisitionJobLifecycleState.Failed, null)]);
        Assert.AreEqual(ProviderAttemptOutcome.Submitted, submitted.Outcome);
        Assert.AreEqual(ProviderAttemptOutcome.Retrying, retrying.Outcome);
    }
}
