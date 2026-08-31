using System.Net;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Publishing;
using FamilyLibrarian.Infrastructure.Publishing;

namespace FamilyLibrarian.Infrastructure.Tests.Publishing;

[TestClass]
public sealed class CwaCatalogClientTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task NoOpdsConfiguredReturnsNull()
    {
        var context = new TestContext();
        context.SettingsStore.Exists = false;

        var result = await context.Client.FindBookIdAsync("Debt of Honor", "Tom Clancy", [], CancellationToken.None);

        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task ASingleTitleAndAuthorMatchReturnsItsBookId()
    {
        var context = ConfiguredContext();
        context.Handler.Responses["Debt of Honor"] =
            Feed(("Debt of Honor", "Tom Clancy", "42"));

        var result = await context.Client.FindBookIdAsync("Debt of Honor", "Tom Clancy", [], CancellationToken.None);

        Assert.AreEqual("42", result);
    }

    [TestMethod]
    public async Task MultipleDistinctTitleMatchesAreAmbiguousAndReturnNull()
    {
        var context = ConfiguredContext();
        context.Handler.Responses["Debt of Honor"] = Feed(
            ("Debt of Honor", "Tom Clancy", "42"),
            ("Debt of Honor", "Tom Clancy", "99"));

        var result = await context.Client.FindBookIdAsync("Debt of Honor", "Tom Clancy", [], CancellationToken.None);

        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task ASingleIsbnHitIsReturnedWithoutEverSearchingByTitle()
    {
        var context = ConfiguredContext();
        // Deliberately a different title than requested -- proves the match
        // came from the ISBN query, not a title-substring fallback.
        context.Handler.Responses["9780000000001"] =
            Feed(("Executive Orders", "Tom Clancy", "77"));

        var result = await context.Client.FindBookIdAsync(
            "Debt of Honor", "Tom Clancy", ["9780000000001"], CancellationToken.None);

        Assert.AreEqual("77", result);
        Assert.IsFalse(context.Handler.RequestedUris.Any(
            uri => uri.AbsolutePath.EndsWith("/Debt%20of%20Honor", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task AnIsbnSearchWithNoHitFallsBackToTitleSearch()
    {
        var context = ConfiguredContext();
        context.Handler.Responses["9780000000001"] = Feed();
        context.Handler.Responses["Debt of Honor"] =
            Feed(("Debt of Honor", "Tom Clancy", "42"));

        var result = await context.Client.FindBookIdAsync(
            "Debt of Honor", "Tom Clancy", ["9780000000001"], CancellationToken.None);

        Assert.AreEqual("42", result);
    }

    [TestMethod]
    public async Task AnIsbnSearchWithMultipleHitsFallsBackToTitleSearch()
    {
        var context = ConfiguredContext();
        context.Handler.Responses["9780000000001"] = Feed(
            ("Something Else", null, "1"),
            ("Something Else Too", null, "2"));
        context.Handler.Responses["Debt of Honor"] =
            Feed(("Debt of Honor", "Tom Clancy", "42"));

        var result = await context.Client.FindBookIdAsync(
            "Debt of Honor", "Tom Clancy", ["9780000000001"], CancellationToken.None);

        Assert.AreEqual("42", result);
    }

    [TestMethod]
    public async Task ASummaryEditionIsNotTreatedAsAMatchEvenThoughItContainsTheTitle()
    {
        var context = ConfiguredContext();
        context.Handler.Responses["Debt of Honor"] =
            Feed(("Summary of Debt of Honor by Tom Clancy", "Some Summarizer", "13"));

        var result = await context.Client.FindBookIdAsync("Debt of Honor", "Tom Clancy", [], CancellationToken.None);

        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task ACombinedEditionIsNotTreatedAsAMatchEvenThoughItContainsTheTitle()
    {
        var context = ConfiguredContext();
        context.Handler.Responses["Debt of Honor"] =
            Feed(("Debt of Honor / Executive Orders", "Tom Clancy", "13"));

        var result = await context.Client.FindBookIdAsync("Debt of Honor", "Tom Clancy", [], CancellationToken.None);

        Assert.IsNull(result);
    }

    private static string Feed(params (string Title, string? Author, string BookId)[] entries)
    {
        var entryXml = string.Join(
            string.Empty,
            entries.Select(entry =>
                $"""
                <entry>
                  <title>{entry.Title}</title>
                  {(entry.Author is null ? string.Empty : $"<author><name>{entry.Author}</name></author>")}
                  <link rel="http://opds-spec.org/acquisition" href="/opds/download/{entry.BookId}"/>
                </entry>
                """));

        return $"""<feed xmlns="http://www.w3.org/2005/Atom">{entryXml}</feed>""";
    }

    private static TestContext ConfiguredContext()
    {
        var context = new TestContext();
        context.Settings.SetSettings(
            CwaTransportMode.Local, "/ingest", null, null, null, null, CwaSftpAuthenticationMode.PrivateKey,
            "https://cwa.example.test", null, null, Now);
        context.SettingsStore.Exists = true;
        return context;
    }

    private sealed class TestContext
    {
        public TestContext()
        {
            SettingsStore = new FakeCwaSettingsStore(Settings);
            Client = new CwaCatalogClient(new TestHttpClientFactory(Handler), SettingsStore, new TestCredentialProtector());
        }

        public CwaSettings Settings { get; } = new(Now);

        public FakeCwaSettingsStore SettingsStore { get; }

        public RoutingHandler Handler { get; } = new();

        public CwaCatalogClient Client { get; }
    }

    private sealed class FakeCwaSettingsStore(CwaSettings settings) : ICwaSettingsStore
    {
        public bool Exists { get; set; }

        public Task<CwaSettings?> FindAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Exists ? settings : null);

        public Task<CwaSettings> GetOrCreateAsync(CancellationToken cancellationToken) => Task.FromResult(settings);

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class TestCredentialProtector : ICredentialProtector
    {
        public int FormatVersion => 1;

        public string Protect(string providerId, string plaintext) => plaintext;

        public string? Unprotect(string providerId, string protectedValue, int formatVersion) => protectedValue;
    }

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>
    /// Serves a canned Atom body keyed by the exact (decoded) OPDS search
    /// query -- title or ISBN -- so a test can control the title-query and
    /// ISBN-query responses independently. An unconfigured query returns an
    /// empty feed rather than throwing, matching "not found" as the default.
    /// </summary>
    private sealed class RoutingHandler : HttpMessageHandler
    {
        public Dictionary<string, string> Responses { get; } = [];

        public List<Uri> RequestedUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUris.Add(request.RequestUri!);

            var query = Uri.UnescapeDataString(request.RequestUri!.Segments[^1]);
            var body = Responses.GetValueOrDefault(query, Feed());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
