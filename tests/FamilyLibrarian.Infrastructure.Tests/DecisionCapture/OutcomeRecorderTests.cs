using FamilyLibrarian.Application.DecisionCapture;
using FamilyLibrarian.Domain.DecisionCapture;
using FamilyLibrarian.Domain.ModelGateway;
using FamilyLibrarian.Infrastructure.Tests.ModelGateway;
using FamilyLibrarian.Infrastructure.Tests.ModelTesting;

namespace FamilyLibrarian.Infrastructure.Tests.DecisionCapture;

/// <summary>C1-c: facts reach the writer only while saving is on, never throw into the caller, and trigger retention only when something was attached.</summary>
[TestClass]
public sealed class OutcomeRecorderTests
{
    private sealed class FakeOutcomeWriter : IDecisionOutcomeWriter
    {
        public List<DecisionOutcomeFact> Facts { get; } = [];

        public int Attach { get; set; } = 1;

        public bool Throws { get; set; }

        public Task<int> WriteAsync(DecisionOutcomeFact fact, DateTimeOffset observedAtUtc, CancellationToken cancellationToken)
        {
            if (Throws) throw new InvalidOperationException("outcome database down");
            Facts.Add(fact);
            return Task.FromResult(Attach);
        }
    }

    private sealed class CountingMaintenance : IDecisionCaptureMaintenance
    {
        public int Runs { get; private set; }

        public Task RunIfDueAsync(CancellationToken cancellationToken)
        {
            Runs++;
            return Task.CompletedTask;
        }
    }

    private static (DecisionOutcomeRecorder Recorder, FakeOutcomeWriter Writer, FakeCaptureLog Log, CountingMaintenance Maintenance) Create(bool capture = true)
    {
        var settings = new ModelGatewaySettings(CaptureRig.Now);
        settings.ConfigureDecisionCapture(capture, false, DecisionCaptureLimits.Default, null, CaptureRig.Now);
        var writer = new FakeOutcomeWriter();
        var log = new FakeCaptureLog();
        var maintenance = new CountingMaintenance();
        return (new DecisionOutcomeRecorder(new FakeGatewayStore { Settings = settings }, writer, new FakeClock(), log, maintenance), writer, log, maintenance);
    }

    private static DecisionOutcomeFact Fact() => new(
        DecisionOutcomeKind.FileRejected, "admin-rejection", DecisionOutcomeStrength.Strong, DecisionOutcomeTarget.File(Guid.NewGuid()), true);

    [TestMethod]
    public async Task AFactIsPassedToTheWriterAsReportedAndRetentionRunsAfterSomethingWasAttached()
    {
        var (recorder, writer, _, maintenance) = Create();
        var fact = Fact();

        await recorder.RecordAsync(fact, CancellationToken.None);

        Assert.AreEqual(fact, writer.Facts.Single());
        Assert.AreEqual(1, maintenance.Runs);
    }

    [TestMethod]
    public async Task AFactAttachedToNothingOrAlreadyRecordedDoesNotTriggerRetention()
    {
        var (recorder, writer, _, maintenance) = Create();
        writer.Attach = 0;

        await recorder.RecordAsync(Fact(), CancellationToken.None);

        Assert.HasCount(1, writer.Facts);
        Assert.AreEqual(0, maintenance.Runs);
    }

    [TestMethod]
    public async Task WithSavingOffNothingIsRecorded()
    {
        var (recorder, writer, _, maintenance) = Create(capture: false);

        await recorder.RecordAsync(Fact(), CancellationToken.None);

        Assert.IsEmpty(writer.Facts);
        Assert.AreEqual(0, maintenance.Runs);
    }

    [TestMethod]
    public async Task AFailingWriterNeverReachesTheCallerAndIsLoggedByTypeOnly()
    {
        var (recorder, writer, log, _) = Create();
        writer.Throws = true;

        await recorder.RecordAsync(Fact(), CancellationToken.None);

        Assert.AreEqual("failed::InvalidOperationException", log.Entries.Single(), "A null role (an outcome has no single role) and the exception type, nothing else.");
        Assert.IsFalse(log.Entries.Single().Contains("database", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task ReportingWithNoRecorderWiredIsANoOp()
    {
        IDecisionOutcomeRecorder? none = null;

        await none.ReportAsync(DecisionOutcomeKind.FileApproved, "admin-approval", DecisionOutcomeStrength.Strong, DecisionOutcomeTarget.File(Guid.NewGuid()), CancellationToken.None);
    }

    [TestMethod]
    public void OnlyFactsThatPraiseTheModelledThingArePositiveSoOnlyAMixedHistoryIsDisputed()
    {
        var positive = Enum.GetValues<DecisionOutcomeKind>().Where(DecisionOutcomeKinds.IsPositive).Order().ToArray();

        CollectionAssert.AreEqual(ExpectedPositive, positive);
    }

    private static readonly DecisionOutcomeKind[] ExpectedPositive =
    [
        DecisionOutcomeKind.FileApproved, DecisionOutcomeKind.IdentityOverridden, DecisionOutcomeKind.ModelReleaseApplied,
        DecisionOutcomeKind.CandidateDownloadedVerified, DecisionOutcomeKind.SearchFoundSelectedResult
    ];
}
