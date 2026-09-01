using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Publishing;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Infrastructure.Tests.Requests;

/// <summary>
/// The combined format-readiness gate: scanner health plus the matching
/// destination's own configuration/test-passing bar.
/// </summary>
[TestClass]
public sealed class FormatReadinessServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task AnUnhealthyScannerBlocksBothFormatsEvenWhenDestinationsAreReady()
    {
        var context = new TestContext();
        await context.MakeCwaReadyAsync();
        await context.MakeAudiobookshelfReadyAsync();
        context.BoundaryGuard.IsHealthy = false;

        var ebook = await context.Service.CheckAsync(RequestMediaType.Ebook, CancellationToken.None);
        var audiobook = await context.Service.CheckAsync(RequestMediaType.Audiobook, CancellationToken.None);

        Assert.IsFalse(ebook.IsReady);
        StringAssert.Contains(ebook.Reason, "scanner");
        Assert.IsFalse(audiobook.IsReady);
        StringAssert.Contains(audiobook.Reason, "scanner");
    }

    [TestMethod]
    public async Task AnUnreadyCwaBlocksOnlyEbook()
    {
        var context = new TestContext();
        await context.MakeAudiobookshelfReadyAsync();

        var ebook = await context.Service.CheckAsync(RequestMediaType.Ebook, CancellationToken.None);
        var audiobook = await context.Service.CheckAsync(RequestMediaType.Audiobook, CancellationToken.None);

        Assert.IsFalse(ebook.IsReady);
        StringAssert.Contains(ebook.Reason, "CWA");
        Assert.IsTrue(audiobook.IsReady);
    }

    [TestMethod]
    public async Task AnUnreadyAudiobookshelfBlocksOnlyAudiobook()
    {
        var context = new TestContext();
        await context.MakeCwaReadyAsync();

        var ebook = await context.Service.CheckAsync(RequestMediaType.Ebook, CancellationToken.None);
        var audiobook = await context.Service.CheckAsync(RequestMediaType.Audiobook, CancellationToken.None);

        Assert.IsTrue(ebook.IsReady);
        Assert.IsFalse(audiobook.IsReady);
        StringAssert.Contains(audiobook.Reason, "Audiobookshelf");
    }

    [TestMethod]
    public async Task BothFormatsAreReadyWhenEverythingIsConfiguredAndHealthy()
    {
        var context = new TestContext();
        await context.MakeCwaReadyAsync();
        await context.MakeAudiobookshelfReadyAsync();

        var ebook = await context.Service.CheckAsync(RequestMediaType.Ebook, CancellationToken.None);
        var audiobook = await context.Service.CheckAsync(RequestMediaType.Audiobook, CancellationToken.None);

        Assert.IsTrue(ebook.IsReady);
        Assert.IsTrue(audiobook.IsReady);
    }

    private sealed class TestContext
    {
        private readonly CwaSettingsService cwaSettings;
        private readonly AudiobookshelfSettingsService audiobookshelfSettings;
        private readonly FakeCwaConnectionTester cwaConnectionTester = new();
        private readonly FakeAudiobookshelfConnectionTester audiobookshelfConnectionTester = new();

        public TestContext()
        {
            cwaSettings = new CwaSettingsService(
                new FakeCwaSettingsStore(),
                new FakeCredentialProtector(),
                cwaConnectionTester,
                new NullAuditWriter(),
                new StubCurrentUser(),
                new FixedClock());

            audiobookshelfSettings = new AudiobookshelfSettingsService(
                new FakeAudiobookshelfSettingsStore(),
                new FakeCredentialProtector(),
                audiobookshelfConnectionTester,
                new FakeLibraryDiscoveryClient(),
                new NullAuditWriter(),
                new StubCurrentUser(),
                new FixedClock());

            BoundaryGuard = new FakeBoundaryGuard();
            Service = new FormatReadinessService(cwaSettings, audiobookshelfSettings, BoundaryGuard);
        }

        public FakeBoundaryGuard BoundaryGuard { get; }

        public FormatReadinessService Service { get; }

        public async Task MakeCwaReadyAsync()
        {
            await cwaSettings.SetSettingsAsync(
                CwaTransportMode.Local, "/ingest", null, null, null, null,
                CwaSftpAuthenticationMode.PrivateKey, "https://cwa.example.test", "opds-user",
                CancellationToken.None);
            cwaConnectionTester.NextOutcome = new ConnectionTestOutcome(true, "Connected.");
            await cwaSettings.TestConnectionAsync(CwaConnectionTestTarget.All, CancellationToken.None);
            await cwaSettings.SetEnabledAsync(true, CancellationToken.None);
        }

        public async Task MakeAudiobookshelfReadyAsync()
        {
            await audiobookshelfSettings.SetSettingsAsync(
                "https://audiobookshelf.example.test", "library-1", null, CancellationToken.None);
            await audiobookshelfSettings.SetApiTokenAsync("test-token", CancellationToken.None);
            audiobookshelfConnectionTester.NextOutcome = new ConnectionTestOutcome(true, "Connected.");
            await audiobookshelfSettings.TestConnectionAsync(CancellationToken.None);
            await audiobookshelfSettings.SetEnabledAsync(true, CancellationToken.None);
        }
    }

    private sealed class FakeBoundaryGuard : IAcquisitionBoundaryGuard
    {
        public bool IsHealthy { get; set; } = true;

        public Task<bool> CanAcceptNewArtifactAsync(CancellationToken cancellationToken) =>
            Task.FromResult(IsHealthy);
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

    private sealed class FakeCwaConnectionTester : ICwaConnectionTester
    {
        public ConnectionTestOutcome NextOutcome { get; set; } = new(true, "Connected.");

        public Task<ConnectionTestOutcome> TestAsync(
            CwaSettings settings, CwaConnectionTestTarget target, CancellationToken cancellationToken) =>
            Task.FromResult(NextOutcome);
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

    private sealed class FakeAudiobookshelfConnectionTester : IAudiobookshelfConnectionTester
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
