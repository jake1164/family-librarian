using FamilyLibrarian.Application.Delivery;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Publishing;
using FamilyLibrarian.Infrastructure.Publishing;

namespace FamilyLibrarian.Infrastructure.Tests.Publishing;

/// <summary>
/// Settings resolution and CWA-to-provider-boundary status mapping for
/// <see cref="CwaEreaderDeliveryProvider"/>. The HTTP/login mechanics
/// themselves are covered separately in <see cref="CwaEreaderSessionClientTests"/>;
/// this class stubs <see cref="ICwaEreaderSessionClient"/> entirely.
/// </summary>
[TestClass]
public sealed class CwaEreaderDeliveryProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task NoSettingsRowAtAllIsNotConfiguredAndDoesNotThrow()
    {
        var context = new TestContext();

        Assert.IsFalse(await context.Provider.CanDeliverAsync(CancellationToken.None));
        var deliver = await context.Provider.DeliverAsync("42", "EPUB", false, "reader@kindle.com", CancellationToken.None);
        Assert.AreEqual(EbookDeliveryStatus.NotConfigured, deliver.Status);
        var test = await context.Provider.TestAsync(CancellationToken.None);
        Assert.IsFalse(test.Succeeded);
    }

    [TestMethod]
    public async Task ABlankUsernameOrMissingPasswordIsNotConfigured()
    {
        var context = new TestContext();
        context.Settings.SetSettings(
            CwaTransportMode.Local, "/ingest", null, null, null, null,
            CwaSftpAuthenticationMode.PrivateKey, "https://cwa.example.test", null, null, null, null, Now);
        context.SettingsStore.Settings = context.Settings;

        Assert.IsFalse(await context.Provider.CanDeliverAsync(CancellationToken.None));
        var deliver = await context.Provider.DeliverAsync("42", "EPUB", false, "reader@kindle.com", CancellationToken.None);
        Assert.AreEqual(EbookDeliveryStatus.NotConfigured, deliver.Status);
    }

    [TestMethod]
    public async Task AnUndecryptablePasswordIsNotConfiguredWithADistinctMessage()
    {
        var context = new TestContext(protector: new FailingProtector());
        context.Configure();

        var deliver = await context.Provider.DeliverAsync("42", "EPUB", false, "reader@kindle.com", CancellationToken.None);

        Assert.AreEqual(EbookDeliveryStatus.NotConfigured, deliver.Status);
        StringAssert.Contains(deliver.Message, "no longer be decrypted");
    }

    [TestMethod]
    public async Task ASuccessfulSendMapsToDelivered()
    {
        var context = new TestContext();
        context.Configure();
        context.SessionClient.NextSendResult = new CwaEreaderSendResult(CwaEreaderSendStatus.Success, "Success!");

        var result = await context.Provider.DeliverAsync("42", "EPUB", false, "reader@kindle.com", CancellationToken.None);

        Assert.AreEqual(EbookDeliveryStatus.Delivered, result.Status);
    }

    [TestMethod]
    public async Task ALoginFailureMapsToNotConfigured()
    {
        var context = new TestContext();
        context.Configure();
        context.SessionClient.NextSendResult = new CwaEreaderSendResult(CwaEreaderSendStatus.LoginFailed, null);

        var result = await context.Provider.DeliverAsync("42", "EPUB", false, "reader@kindle.com", CancellationToken.None);

        Assert.AreEqual(EbookDeliveryStatus.NotConfigured, result.Status);
    }

    [TestMethod]
    public async Task ASendRejectionMapsToRejectedAndPassesTheCwaMessageThrough()
    {
        var context = new TestContext();
        context.Configure();
        context.SessionClient.NextSendResult = new CwaEreaderSendResult(
            CwaEreaderSendStatus.SendRejected, "Please configure the SMTP mail settings first...");

        var result = await context.Provider.DeliverAsync("42", "EPUB", false, "reader@kindle.com", CancellationToken.None);

        Assert.AreEqual(EbookDeliveryStatus.Rejected, result.Status);
        Assert.AreEqual("Please configure the SMTP mail settings first...", result.Message);
    }

    [TestMethod]
    public async Task ATransportFailureMapsToTransportFailure()
    {
        var context = new TestContext();
        context.Configure();
        context.SessionClient.NextSendResult = new CwaEreaderSendResult(CwaEreaderSendStatus.TransportFailure, null);

        var result = await context.Provider.DeliverAsync("42", "EPUB", false, "reader@kindle.com", CancellationToken.None);

        Assert.AreEqual(EbookDeliveryStatus.TransportFailure, result.Status);
    }

    [TestMethod]
    public async Task TheSessionClientReceivesTheOpdsBaseUrlSettingsObjectNotJustAHost()
    {
        var context = new TestContext();
        context.Configure();
        context.SessionClient.NextSendResult = new CwaEreaderSendResult(CwaEreaderSendStatus.Success, "ok");

        await context.Provider.DeliverAsync("42", "EPUB", false, "reader@kindle.com", CancellationToken.None);

        Assert.AreSame(context.Settings, context.SessionClient.LastSettings);
        Assert.AreEqual("https://cwa.example.test", context.SessionClient.LastSettings!.OpdsBaseUrl);
    }

    private sealed class TestContext
    {
        public TestContext(ICredentialProtector? protector = null)
        {
            SettingsStore = new FakeCwaSettingsStore();
            SessionClient = new FakeCwaEreaderSessionClient();
            Provider = new CwaEreaderDeliveryProvider(
                SettingsStore, protector ?? new PassthroughProtector(), SessionClient);
        }

        public CwaSettings Settings { get; } = new(Now);

        public FakeCwaSettingsStore SettingsStore { get; }

        public FakeCwaEreaderSessionClient SessionClient { get; }

        public CwaEreaderDeliveryProvider Provider { get; }

        public void Configure()
        {
            Settings.SetSettings(
                CwaTransportMode.Local, "/ingest", null, null, null, null,
                CwaSftpAuthenticationMode.PrivateKey, "https://cwa.example.test", null, null, "service-account",
                null, Now);
            Settings.SetEreaderServiceAccountPassword("protected-password", 1, null, null, Now);
            SettingsStore.Settings = Settings;
        }
    }

    private sealed class FakeCwaSettingsStore : ICwaSettingsStore
    {
        public CwaSettings? Settings { get; set; }

        public Task<CwaSettings?> FindAsync(CancellationToken cancellationToken) => Task.FromResult(Settings);

        public Task<CwaSettings> GetOrCreateAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Settings ?? throw new InvalidOperationException("No settings configured."));

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeCwaEreaderSessionClient : ICwaEreaderSessionClient
    {
        public CwaSettings? LastSettings { get; private set; }

        public CwaEreaderSendResult NextSendResult { get; set; } = new(CwaEreaderSendStatus.Success, "ok");

        public CwaEreaderLoginResult NextLoginResult { get; set; } = new(true, "ok");

        public Task<CwaEreaderSendResult> SendSelectedAsync(
            CwaSettings settings, string serviceAccountUsername, string serviceAccountPassword,
            string cwaBookId, string bookFormat, bool convert, string recipientEmail,
            CancellationToken cancellationToken)
        {
            LastSettings = settings;
            return Task.FromResult(NextSendResult);
        }

        public Task<CwaEreaderLoginResult> TestLoginAsync(
            CwaSettings settings, string serviceAccountUsername, string serviceAccountPassword,
            CancellationToken cancellationToken)
        {
            LastSettings = settings;
            return Task.FromResult(NextLoginResult);
        }
    }

    private sealed class PassthroughProtector : ICredentialProtector
    {
        public int FormatVersion => 1;

        public string Protect(string providerId, string plaintext) => plaintext;

        public string? Unprotect(string providerId, string protectedValue, int formatVersion) => protectedValue;
    }

    private sealed class FailingProtector : ICredentialProtector
    {
        public int FormatVersion => 1;

        public string Protect(string providerId, string plaintext) => plaintext;

        public string? Unprotect(string providerId, string protectedValue, int formatVersion) => null;
    }
}
