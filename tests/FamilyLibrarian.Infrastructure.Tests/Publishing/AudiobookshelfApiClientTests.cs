using System.Net;
using System.Text;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Matching;
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
            new PassThroughProtector(),
            NewMatchService());

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
        Assert.IsFalse(handler.Body.Contains("name=files", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ASingleMatchingItemIsReturnedAsAMatch()
    {
        var client = ClientReturningItems(Item("Moby Dick", "Herman Melville", "li_1"));

        var result = await client.FindExistingItemIdAsync("Moby Dick", "Herman Melville", CancellationToken.None);

        Assert.AreEqual(BookMatchDecision.Match, result.Decision);
        Assert.AreEqual("li_1", result.MatchedId);
    }

    [TestMethod]
    public async Task TwoMatchingEditionsAreAmbiguousRatherThanTheFirstOneSilentlyWinning()
    {
        // This is the naive-implementation bug this test guards against: the
        // old FindMatchingItemId returned the first substring match, which
        // could silently attach the wrong edition.
        var client = ClientReturningItems(
            Item("Moby Dick", "Herman Melville", "li_1"),
            Item("Moby Dick (Annotated)", "Herman Melville", "li_2"));

        var result = await client.FindExistingItemIdAsync("Moby Dick", "Herman Melville", CancellationToken.None);

        Assert.AreEqual(BookMatchDecision.Ambiguous, result.Decision);
        Assert.AreEqual(2, result.Candidates.Count);
    }

    [TestMethod]
    public async Task ANonMatchingLibraryReturnsNoMatch()
    {
        var client = ClientReturningItems(Item("Debt of Honor", "Tom Clancy", "li_1"));

        var result = await client.FindExistingItemIdAsync("Moby Dick", "Herman Melville", CancellationToken.None);

        Assert.AreEqual(BookMatchDecision.NoMatch, result.Decision);
    }

    [TestMethod]
    public async Task AConflictingAuthorExcludesAnOtherwiseTitleMatchingItem()
    {
        var client = ClientReturningItems(Item("Moby Dick", "A Different Author", "li_1"));

        var result = await client.FindExistingItemIdAsync("Moby Dick", "Herman Melville", CancellationToken.None);

        Assert.AreEqual(BookMatchDecision.NoMatch, result.Decision);
    }

    private static string Item(string title, string? author, string id)
    {
        var authorField = author is null ? string.Empty : $", \"authorName\": \"{author}\"";
        return $$"""{"id": "{{id}}", "media": {"metadata": {"title": "{{title}}"{{authorField}} } } }""";
    }

    private static AudiobookshelfApiClient ClientReturningItems(params string[] items)
    {
        var body = $"{{\"results\": [{string.Join(",", items)}]}}";
        var handler = new FixedBodyHandler(body);
        var settings = new AudiobookshelfSettings(DateTimeOffset.UtcNow);
        settings.SetSettings("http://abs.example", "library-id", "folder-id", null, DateTimeOffset.UtcNow);
        settings.SetApiToken("protected-token", 1, null, null, DateTimeOffset.UtcNow);
        return new AudiobookshelfApiClient(
            new TestHttpClientFactory(handler), new SettingsStore(settings), new PassThroughProtector(), NewMatchService());
    }

    private static BookMatchService NewMatchService() =>
        new(new DeterministicBookMatcher(), new NoOpAmbiguityResolver());

    private sealed class FixedBodyHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
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
