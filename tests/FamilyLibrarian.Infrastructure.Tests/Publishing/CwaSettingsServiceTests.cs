using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Publishing;

namespace FamilyLibrarian.Infrastructure.Tests.Publishing;

[TestClass]
public sealed class CwaSettingsServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task EnablingWithoutAnyConfigurationIsRejected()
    {
        var context = new TestContext();

        var result = await context.Service.SetEnabledAsync(true, CancellationToken.None);

        Assert.AreEqual(PublishingCommandOutcome.Invalid, result.Outcome);
        StringAssert.Contains(result.Error, "local ingest path is required");
    }

    [TestMethod]
    public async Task EnablingWithAnIngestTransportButNoOpdsUrlIsRejected()
    {
        // docs/01 §12.1.1's known gap: a working ingest transport alone used to
        // be enough to enable CWA. Ownership lookup and post-ingest correlation
        // both depend on the OPDS connection independent of ingest transport.
        var context = new TestContext();
        await context.SetLocalIngestOnlyAsync();

        var result = await context.Service.SetEnabledAsync(true, CancellationToken.None);

        Assert.AreEqual(PublishingCommandOutcome.Invalid, result.Outcome);
        StringAssert.Contains(result.Error, "OPDS catalog URL is required");
    }

    [TestMethod]
    public async Task EnablingWithOpdsConfiguredButNeverTestedIsRejected()
    {
        var context = new TestContext();
        await context.SetLocalIngestAndOpdsAsync();

        var result = await context.Service.SetEnabledAsync(true, CancellationToken.None);

        Assert.AreEqual(PublishingCommandOutcome.Invalid, result.Outcome);
        StringAssert.Contains(result.Error, "Test the connection");
    }

    [TestMethod]
    public async Task EnablingWithAFailedTestIsRejected()
    {
        var context = new TestContext();
        await context.SetLocalIngestAndOpdsAsync();
        context.ConnectionTester.NextOutcome = new ConnectionTestOutcome(false, "The OPDS catalog is unreachable.");
        await context.Service.TestConnectionAsync(CwaConnectionTestTarget.All, CancellationToken.None);

        var result = await context.Service.SetEnabledAsync(true, CancellationToken.None);

        Assert.AreEqual(PublishingCommandOutcome.Invalid, result.Outcome);
        StringAssert.Contains(result.Error, "Test the connection");
    }

    [TestMethod]
    public async Task EnablingAfterAPassingTestSucceeds()
    {
        var context = new TestContext();
        await context.SetLocalIngestAndOpdsAsync();
        context.ConnectionTester.NextOutcome = new ConnectionTestOutcome(true, "Connected.");
        await context.Service.TestConnectionAsync(CwaConnectionTestTarget.All, CancellationToken.None);

        var result = await context.Service.SetEnabledAsync(true, CancellationToken.None);

        Assert.AreEqual(PublishingCommandOutcome.Success, result.Outcome);
        Assert.IsTrue(result.Status!.IsEnabled);
    }

    [TestMethod]
    public async Task ChangingSettingsAfterAPassingTestInvalidatesItAndBlocksEnabling()
    {
        // CwaSettings.ResetTestResult() fires on every settings/secret mutation --
        // this proves the invariant is "a successful test for the *currently
        // saved* configuration," not a stale pass from before the last edit.
        var context = new TestContext();
        await context.SetLocalIngestAndOpdsAsync();
        context.ConnectionTester.NextOutcome = new ConnectionTestOutcome(true, "Connected.");
        await context.Service.TestConnectionAsync(CwaConnectionTestTarget.All, CancellationToken.None);

        await context.SetLocalIngestAndOpdsAsync(localIngestPath: "/ingest-changed");

        var result = await context.Service.SetEnabledAsync(true, CancellationToken.None);

        Assert.AreEqual(PublishingCommandOutcome.Invalid, result.Outcome);
        StringAssert.Contains(result.Error, "Test the connection");
    }

    [TestMethod]
    public async Task TogglingEnabledWithoutOtherChangesDoesNotInvalidateAPassingTest()
    {
        var context = new TestContext();
        await context.SetLocalIngestAndOpdsAsync();
        context.ConnectionTester.NextOutcome = new ConnectionTestOutcome(true, "Connected.");
        await context.Service.TestConnectionAsync(CwaConnectionTestTarget.All, CancellationToken.None);
        await context.Service.SetEnabledAsync(true, CancellationToken.None);

        await context.Service.SetEnabledAsync(false, CancellationToken.None);
        var result = await context.Service.SetEnabledAsync(true, CancellationToken.None);

        Assert.AreEqual(PublishingCommandOutcome.Success, result.Outcome);
    }

    [TestMethod]
    public async Task RequestReadinessIsRejectedWhenNeverEnabled()
    {
        var context = new TestContext();
        await context.SetLocalIngestAndOpdsAsync();
        context.ConnectionTester.NextOutcome = new ConnectionTestOutcome(true, "Connected.");
        await context.Service.TestConnectionAsync(CwaConnectionTestTarget.All, CancellationToken.None);

        var error = await context.Service.GetRequestReadinessErrorAsync(CancellationToken.None);

        StringAssert.Contains(error, "CWA is not enabled");
    }

    [TestMethod]
    public async Task RequestReadinessSucceedsOnceEnabledWithAPassingTest()
    {
        var context = new TestContext();
        await context.SetLocalIngestAndOpdsAsync();
        context.ConnectionTester.NextOutcome = new ConnectionTestOutcome(true, "Connected.");
        await context.Service.TestConnectionAsync(CwaConnectionTestTarget.All, CancellationToken.None);
        await context.Service.SetEnabledAsync(true, CancellationToken.None);

        var error = await context.Service.GetRequestReadinessErrorAsync(CancellationToken.None);

        Assert.IsNull(error);
    }

    [TestMethod]
    public async Task RequestReadinessIsRejectedAfterEnablingWhenSettingsChangeInvalidatesTheTest()
    {
        // Enabling doesn't lock settings: a later change can invalidate the test
        // result without SetEnabledAsync running again, so readiness must
        // re-check the current configuration, not just the IsEnabled flag.
        var context = new TestContext();
        await context.SetLocalIngestAndOpdsAsync();
        context.ConnectionTester.NextOutcome = new ConnectionTestOutcome(true, "Connected.");
        await context.Service.TestConnectionAsync(CwaConnectionTestTarget.All, CancellationToken.None);
        await context.Service.SetEnabledAsync(true, CancellationToken.None);

        await context.SetLocalIngestAndOpdsAsync(localIngestPath: "/ingest-changed");

        var error = await context.Service.GetRequestReadinessErrorAsync(CancellationToken.None);

        StringAssert.Contains(error, "Test the connection");
    }

    private sealed class TestContext
    {
        public TestContext()
        {
            Store = new FakeCwaSettingsStore();
            ConnectionTester = new FakeConnectionTester();
            Audit = new RecordingAuditWriter();

            Service = new CwaSettingsService(
                Store, new FakeCredentialProtector(), ConnectionTester, Audit, new StubCurrentUser(), new FixedClock());
        }

        public FakeCwaSettingsStore Store { get; }

        public FakeConnectionTester ConnectionTester { get; }

        public RecordingAuditWriter Audit { get; }

        public CwaSettingsService Service { get; }

        public Task<CwaCommandResult> SetLocalIngestOnlyAsync(string localIngestPath = "/ingest") =>
            Service.SetSettingsAsync(
                CwaTransportMode.Local, localIngestPath, null, null, null, null,
                CwaSftpAuthenticationMode.PrivateKey, null, null, CancellationToken.None);

        public Task<CwaCommandResult> SetLocalIngestAndOpdsAsync(
            string localIngestPath = "/ingest", string opdsBaseUrl = "https://cwa.example.test") =>
            Service.SetSettingsAsync(
                CwaTransportMode.Local, localIngestPath, null, null, null, null,
                CwaSftpAuthenticationMode.PrivateKey, opdsBaseUrl, "opds-user", CancellationToken.None);
    }

    private sealed class FakeCwaSettingsStore : ICwaSettingsStore
    {
        private CwaSettings? _settings;

        public Task<CwaSettings?> FindAsync(CancellationToken cancellationToken) => Task.FromResult(_settings);

        public Task<CwaSettings> GetOrCreateAsync(CancellationToken cancellationToken)
        {
            _settings ??= new CwaSettings(Now);
            return Task.FromResult(_settings);
        }

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeConnectionTester : ICwaConnectionTester
    {
        public ConnectionTestOutcome NextOutcome { get; set; } = new(true, "Connected.");

        public Task<ConnectionTestOutcome> TestAsync(
            CwaSettings settings, CwaConnectionTestTarget target, CancellationToken cancellationToken) =>
            Task.FromResult(NextOutcome);
    }

    private sealed class FakeCredentialProtector : ICredentialProtector
    {
        public int FormatVersion => 1;

        public string Protect(string providerId, string plaintext) => plaintext;

        public string? Unprotect(string providerId, string protectedValue, int formatVersion) => protectedValue;
    }

    private sealed class RecordingAuditWriter : IAuditWriter
    {
        public List<(string Action, string SubjectType, string? SubjectId, object? Detail)> Entries { get; } = [];

        public Task WriteAsync(
            string action, string subjectType, string? subjectId, object? detail, CancellationToken cancellationToken)
        {
            Entries.Add((action, subjectType, subjectId, detail));
            return Task.CompletedTask;
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class StubCurrentUser : ICurrentUser
    {
        public Guid? UserId => null;

        public string? DisplayName => null;
    }
}
