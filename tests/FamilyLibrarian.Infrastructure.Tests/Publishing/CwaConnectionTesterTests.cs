using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Publishing;
using FamilyLibrarian.Infrastructure.Publishing;

namespace FamilyLibrarian.Infrastructure.Tests.Publishing;

[TestClass]
public sealed class CwaConnectionTesterTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task MissingPasswordIsReportedAsMissingInsteadOfADecryptionFailure()
    {
        var settings = CreatePasswordSettings();
        var tester = CreateTester(new TestCredentialProtector());

        var result = await tester.TestAsync(
            settings,
            CwaConnectionTestTarget.Ingest,
            CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.Message, "No SFTP password is saved");
        StringAssert.DoesNotMatch(
            result.Message,
            new System.Text.RegularExpressions.Regex("decrypt", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
    }

    [TestMethod]
    public async Task UndecryptablePasswordTellsTheAdministratorToReplaceIt()
    {
        var settings = CreatePasswordSettings();
        settings.SetSftpPassword("protected-value", 1, "alue", null, Now);
        var tester = CreateTester(new TestCredentialProtector { CanUnprotect = false });

        var result = await tester.TestAsync(
            settings,
            CwaConnectionTestTarget.Ingest,
            CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.Message, "saved SFTP password can no longer be decrypted");
        StringAssert.Contains(result.Message, "Enter it again");
    }

    [TestMethod]
    public async Task OpdsTestDoesNotAttemptTheConfiguredSftpTransport()
    {
        var settings = CreatePasswordSettings();
        var tester = CreateTester(new TestCredentialProtector());

        var result = await tester.TestAsync(
            settings,
            CwaConnectionTestTarget.Opds,
            CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("No OPDS URL is configured; import verification will be skipped.", result.Message);
    }

    [TestMethod]
    public async Task OpdsTestUsesTheSameSearchRouteAndRequiresAnAtomFeed()
    {
        var settings = CreatePasswordSettings();
        settings.SetSettings(
            CwaTransportMode.Sftp,
            null,
            "sftp.example.test",
            22,
            "cwa",
            "/ingest",
            CwaSftpAuthenticationMode.Password,
            "https://cwa.example.test",
            null,
            null,
            Now);
        var handler = new OpdsSearchHandler();
        var tester = CreateTester(new TestCredentialProtector(), new TestHttpClientFactory(handler));

        var result = await tester.TestAsync(settings, CwaConnectionTestTarget.Opds, CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        StringAssert.Contains(result.Message, "search endpoint");
        Assert.IsNotNull(handler.RequestUri);
        StringAssert.StartsWith(handler.RequestUri.AbsolutePath, "/opds/search/");
    }

    private static CwaSettings CreatePasswordSettings()
    {
        var settings = new CwaSettings(Now);
        settings.SetSettings(
            CwaTransportMode.Sftp,
            null,
            "sftp.example.test",
            22,
            "cwa",
            "/ingest",
            CwaSftpAuthenticationMode.Password,
            null,
            null,
            null,
            Now);
        return settings;
    }

    private static CwaConnectionTester CreateTester(
        ICredentialProtector protector,
        IHttpClientFactory? httpClientFactory = null) =>
        new(protector, httpClientFactory ?? new TestHttpClientFactory());

    private sealed class TestCredentialProtector : ICredentialProtector
    {
        public bool CanUnprotect { get; init; } = true;

        public int FormatVersion => 1;

        public string Protect(string providerId, string plaintext) => plaintext;

        public string? Unprotect(string providerId, string protectedValue, int formatVersion) =>
            CanUnprotect ? protectedValue : null;
    }

    private sealed class TestHttpClientFactory(HttpMessageHandler? handler = null) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => handler is null ? new() : new(handler, disposeHandler: false);
    }

    private sealed class OpdsSearchHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "<feed xmlns=\"http://www.w3.org/2005/Atom\"><title>Search results</title></feed>")
            });
        }
    }
}
