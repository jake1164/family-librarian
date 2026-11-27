using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Publishing;

namespace FamilyLibrarian.Infrastructure.Tests.Publishing;

/// <summary>
/// <see cref="AudiobookshelfSettingsService.GetRequestReadinessErrorAsync"/> --
/// the Audiobookshelf half of the format-readiness gate, mirroring
/// <c>CwaSettingsServiceTests</c> -- and the same enable rules: the saved settings
/// must be complete and must pass a test, which enabling runs when needed.
/// </summary>
[TestClass]
public sealed class AudiobookshelfSettingsServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task RequestReadinessIsRejectedWhenNeverEnabled()
    {
        var context = new TestContext();
        await context.SetSettingsAndTokenAsync();
        context.ConnectionTester.NextOutcome = new ConnectionTestOutcome(true, "Connected.");
        await context.Service.TestConnectionAsync(CancellationToken.None);

        var error = await context.Service.GetRequestReadinessErrorAsync(CancellationToken.None);

        StringAssert.Contains(error, "Audiobookshelf is not enabled");
    }

    [TestMethod]
    public async Task ATestOfUnsavedValuesIsNotRecorded()
    {
        // TestConfigurationAsync checks the values on screen. A pass for values that
        // are not saved must never satisfy the readiness gate for the saved ones.
        var context = new TestContext();
        await context.SetSettingsAndTokenAsync();

        var outcome = await context.Service.TestConfigurationAsync(
            "https://other.example.test", context.LibraryId, null, context.ApiToken, CancellationToken.None);

        StringAssert.Contains(outcome.Message, "not saved yet");
        Assert.IsNull((await context.Service.GetStatusAsync(CancellationToken.None)).LastTestSucceeded);
    }

    [TestMethod]
    public async Task ATestOfTheSavedValuesIsRecorded()
    {
        var context = new TestContext();
        await context.SetSettingsAndTokenAsync();

        await context.Service.TestConfigurationAsync(context.BaseUrl, context.LibraryId, null, null, CancellationToken.None);

        Assert.AreEqual(true, (await context.Service.GetStatusAsync(CancellationToken.None)).LastTestSucceeded);
    }

    [TestMethod]
    [DataRow(false, false, false, "base URL is required")]
    [DataRow(true, false, false, "library must be selected")]
    [DataRow(true, true, false, "API token is required")]
    public async Task EnablingIncompleteSettingsIsRefused(bool url, bool library, bool token, string expected)
    {
        var context = new TestContext();
        await context.Service.SetSettingsAsync(
            url ? context.BaseUrl : null, null, library ? context.LibraryId : null, null, CancellationToken.None);
        if (token) await context.Service.SetApiTokenAsync(context.ApiToken, CancellationToken.None);

        var result = await context.Service.SetEnabledAsync(true, CancellationToken.None);

        Assert.AreEqual(PublishingCommandOutcome.Invalid, result.Outcome);
        StringAssert.Contains(result.Error, expected);
        Assert.IsFalse((await context.Service.GetStatusAsync(CancellationToken.None)).IsEnabled);
    }

    [TestMethod]
    public async Task EnablingRunsTheTestAndStaysOffWhenItFails()
    {
        var context = new TestContext();
        await context.SetSettingsAndTokenAsync();
        context.ConnectionTester.NextOutcome = new ConnectionTestOutcome(false, "The token was refused.");

        var refused = await context.Service.SetEnabledAsync(true, CancellationToken.None);

        StringAssert.Contains(refused.Error, "Audiobookshelf stayed off");
        StringAssert.Contains(refused.Error, "The token was refused.");

        context.ConnectionTester.NextOutcome = new ConnectionTestOutcome(true, "Connected.");
        var enabled = await context.Service.SetEnabledAsync(true, CancellationToken.None);
        Assert.IsTrue(enabled.Status!.IsEnabled);
        Assert.IsNull(await context.Service.GetRequestReadinessErrorAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task RequestReadinessSucceedsOnceEnabledConfiguredAndTested()
    {
        var context = new TestContext();
        await context.SetSettingsAndTokenAsync();
        context.ConnectionTester.NextOutcome = new ConnectionTestOutcome(true, "Connected.");
        var testResult = await context.Service.TestConnectionAsync(CancellationToken.None);
        await context.Service.SetEnabledAsync(true, CancellationToken.None);

        Assert.IsTrue(testResult.Outcome.Succeeded);
        Assert.AreEqual(true, testResult.Status.LastTestSucceeded);
        var error = await context.Service.GetRequestReadinessErrorAsync(CancellationToken.None);

        Assert.IsNull(error);
    }

    [TestMethod]
    public async Task RequestReadinessIsRejectedAfterSettingsChangeInvalidatesTheTest()
    {
        var context = new TestContext();
        await context.SetSettingsAndTokenAsync();
        context.ConnectionTester.NextOutcome = new ConnectionTestOutcome(true, "Connected.");
        await context.Service.TestConnectionAsync(CancellationToken.None);
        await context.Service.SetEnabledAsync(true, CancellationToken.None);

        await context.Service.SetSettingsAsync(
            context.BaseUrl, null, "a-different-library", null, CancellationToken.None);

        var error = await context.Service.GetRequestReadinessErrorAsync(CancellationToken.None);

        StringAssert.Contains(error, "Test the connection");
    }

    private sealed class TestContext
    {
        public TestContext()
        {
            Store = new FakeAudiobookshelfSettingsStore();
            ConnectionTester = new FakeConnectionTester();

            Service = new AudiobookshelfSettingsService(
                Store,
                new FakeCredentialProtector(),
                ConnectionTester,
                new FakeLibraryDiscoveryClient(),
                new NullAuditWriter(),
                new StubCurrentUser(),
                new FixedClock());
        }

        public string BaseUrl { get; } = "https://audiobookshelf.example.test";

        public string LibraryId { get; } = "library-1";

        public string ApiToken { get; } = "test-token";

        public FakeAudiobookshelfSettingsStore Store { get; }

        public FakeConnectionTester ConnectionTester { get; }

        public AudiobookshelfSettingsService Service { get; }

        public async Task SetSettingsAndTokenAsync()
        {
            await Service.SetSettingsAsync(BaseUrl, null, LibraryId, null, CancellationToken.None);
            await Service.SetApiTokenAsync(ApiToken, CancellationToken.None);
        }
    }

    private sealed class FakeAudiobookshelfSettingsStore : IAudiobookshelfSettingsStore
    {
        private AudiobookshelfSettings? _settings;

        public Task<AudiobookshelfSettings?> FindAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_settings);

        public Task<AudiobookshelfSettings> GetOrCreateAsync(CancellationToken cancellationToken)
        {
            _settings ??= new AudiobookshelfSettings(Now);
            return Task.FromResult(_settings);
        }

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeConnectionTester : IAudiobookshelfConnectionTester
    {
        public ConnectionTestOutcome NextOutcome { get; set; } = new(true, "Connected.");

        public Task<ConnectionTestOutcome> TestAsync(
            AudiobookshelfSettings settings, CancellationToken cancellationToken) =>
            Task.FromResult(NextOutcome);
    }

    private sealed class FakeLibraryDiscoveryClient : IAudiobookshelfLibraryDiscoveryClient
    {
        public Task<AudiobookshelfLibraryDiscoveryOutcome> ListLibrariesAsync(
            string baseUrl, string apiToken, CancellationToken cancellationToken) =>
            Task.FromResult(AudiobookshelfLibraryDiscoveryOutcome.Success([]));
    }

    private sealed class FakeCredentialProtector : ICredentialProtector
    {
        public int FormatVersion => 1;

        public string Protect(string providerId, string plaintext) => plaintext;

        public string? Unprotect(string providerId, string protectedValue, int formatVersion) => protectedValue;
    }

    private sealed class NullAuditWriter : IAuditWriter
    {
        public Task WriteAsync(
            string action, string subjectType, string? subjectId, object? detail, CancellationToken cancellationToken) =>
            Task.CompletedTask;
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
