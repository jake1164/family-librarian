using System.Net;
using System.Text;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Publishing;
using FamilyLibrarian.Infrastructure.Publishing;

namespace FamilyLibrarian.Infrastructure.Tests.Publishing;

[TestClass]
public sealed class AudiobookshelfApiClientTests
{
    [TestMethod]
    public async Task UploadBundleAsyncUsesZeroBasedFilePartNamesRequiredByAudiobookshelf()
    {
        var handler = new RecordingHandler();
        var settings = new AudiobookshelfSettings(DateTimeOffset.UtcNow);
        settings.SetSettings("http://abs.example", "library-id", "folder-id", null, DateTimeOffset.UtcNow);
        settings.SetApiToken("protected-token", 1, null, null, DateTimeOffset.UtcNow);
        var client = new AudiobookshelfApiClient(
            new TestHttpClientFactory(handler),
            new SettingsStore(settings),
            new PassThroughProtector());

        var result = await client.UploadBundleAsync(
            [
                (new MemoryStream(Encoding.UTF8.GetBytes("first")), "01.mp3"),
                (new MemoryStream(Encoding.UTF8.GetBytes("second")), "02.mp3")
            ],
            "Jack and Jill",
            "Louisa May Alcott",
            CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("http://abs.example/api/upload", handler.RequestUri);
        StringAssert.Contains(handler.Body, "name=0; filename=01.mp3");
        StringAssert.Contains(handler.Body, "name=1; filename=02.mp3");
        StringAssert.DoesNotContain(handler.Body, "name=files");
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string RequestUri { get; private set; } = string.Empty;

        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri?.ToString() ?? string.Empty;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            };
        }
    }

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class SettingsStore(AudiobookshelfSettings settings) : IAudiobookshelfSettingsStore
    {
        public Task<AudiobookshelfSettings?> FindAsync(CancellationToken cancellationToken) => Task.FromResult<AudiobookshelfSettings?>(settings);

        public Task<AudiobookshelfSettings> GetOrCreateAsync(CancellationToken cancellationToken) => Task.FromResult(settings);

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class PassThroughProtector : ICredentialProtector
    {
        public int FormatVersion => 1;

        public string Protect(string providerId, string plaintext) => plaintext;

        public string? Unprotect(string providerId, string protectedValue, int formatVersion) => protectedValue;
    }
}
