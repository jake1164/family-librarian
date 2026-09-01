using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Publishing;

namespace FamilyLibrarian.Infrastructure.Tests.Publishing;

/// <summary>
/// <see cref="AudiobookshelfSettingsService.GetRequestReadinessErrorAsync"/> --
/// the Audiobookshelf half of the format-readiness gate, mirroring
/// <c>CwaSettingsServiceTests</c>. Unlike CWA, <see cref="AudiobookshelfSettingsService.SetEnabledAsync"/>
/// itself does not require a passing test, so these tests exercise readiness
/// directly rather than an enable-time rejection.
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
    public async Task TheDraftConfigurationTestDoesNotPersistAResult()
    {
        // TestConfigurationAsync exists so an administrator can check unsaved
        // form values without committing them -- it must never let
        // LastTestSucceeded become true on its own, or the readiness gate
        // could be satisfied by a test result nothing ever actually saved.
        var context = new TestContext();
        await context.SetSettingsAndTokenAsync();
        context.ConnectionTester.NextOutcome = new ConnectionTestOutcome(true, "Connected.");

        await context.Service.TestConfigurationAsync(
            context.BaseUrl, context.LibraryId, null, context.ApiToken, CancellationToken.None);
        await context.Service.SetEnabledAsync(true, CancellationToken.None);

        var error = await context.Service.GetRequestReadinessErrorAsync(CancellationToken.None);

        StringAssert.Contains(error, "Test the connection");
    }

    [TestMethod]
    public async Task RequestReadinessIsRejectedWhenEnabledWithoutABaseUrl()
    {
        var context = new TestContext();
        await context.Service.SetEnabledAsync(true, CancellationToken.None);

        var error = await context.Service.GetRequestReadinessErrorAsync(CancellationToken.None);

        StringAssert.Contains(error, "base URL is required");
    }

    [TestMethod]
    public async Task RequestReadinessIsRejectedWhenEnabledWithoutALibrary()
    {
        var context = new TestContext();
        await context.Service.SetSettingsAsync(context.BaseUrl, null, null, CancellationToken.None);
        await context.Service.SetEnabledAsync(true, CancellationToken.None);

        var error = await context.Service.GetRequestReadinessErrorAsync(CancellationToken.None);

        StringAssert.Contains(error, "library must be selected");
    }

    [TestMethod]
    public async Task RequestReadinessIsRejectedWhenEnabledWithoutAnApiToken()
    {
        var context = new TestContext();
        await context.Service.SetSettingsAsync(context.BaseUrl, context.LibraryId, null, CancellationToken.None);
        await context.Service.SetEnabledAsync(true, CancellationToken.None);

        var error = await context.Service.GetRequestReadinessErrorAsync(CancellationToken.None);

        StringAssert.Contains(error, "API token is required");
    }

    [TestMethod]
    public async Task RequestReadinessIsRejectedWhenConfiguredButNeverTested()
    {
        var context = new TestContext();
        await context.SetSettingsAndTokenAsync();
        await context.Service.SetEnabledAsync(true, CancellationToken.None);

        var error = await context.Service.GetRequestReadinessErrorAsync(CancellationToken.None);

        StringAssert.Contains(error, "Test the connection");
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
            context.BaseUrl, "a-different-library", null, CancellationToken.None);

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
            await Service.SetSettingsAsync(BaseUrl, LibraryId, null, CancellationToken.None);
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
