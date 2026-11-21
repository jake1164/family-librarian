using System.Net;
using FamilyLibrarian.Infrastructure.Gutenberg;

namespace FamilyLibrarian.Infrastructure.Tests.Gutenberg;

[TestClass]
public sealed class GutenbergCatalogSourceFetcherTests
{
    private static readonly Uri Primary = new("https://primary.example/cache/epub/feeds/today.rss");
    private static readonly Uri Secondary = new("https://secondary.example/cache/epub/feeds/today.rss");
    private static readonly Uri Tertiary = new("https://tertiary.example/cache/epub/feeds/today.rss");

    [TestMethod]
    public async Task ReturnsThePrimarySourceWhenItSucceeds()
    {
        var handler = new StubHandler(
            (Primary, HttpStatusCode.OK),
            (Secondary, HttpStatusCode.OK));

        var result = await FetchAsync(handler, [Primary, Secondary]);

        Assert.AreEqual(0, result.SourceIndex);
        Assert.AreEqual(Primary, result.Source);
        CollectionAssert.AreEqual(new[] { Primary }, handler.Requested);
    }

    [TestMethod]
    public async Task FallsBackWhenThePrimaryReturnsServiceUnavailable()
    {
        var handler = new StubHandler(
            (Primary, HttpStatusCode.ServiceUnavailable),
            (Secondary, HttpStatusCode.OK));

        var result = await FetchAsync(handler, [Primary, Secondary]);

        Assert.AreEqual(1, result.SourceIndex);
        Assert.AreEqual(Secondary, result.Source);
        CollectionAssert.AreEqual(new[] { Primary, Secondary }, handler.Requested);
    }

    [TestMethod]
    public async Task FallsBackWhenThePrimaryDoesNotHaveTheResource()
    {
        var handler = new StubHandler(
            (Primary, HttpStatusCode.NotFound),
            (Secondary, HttpStatusCode.OK));

        var result = await FetchAsync(handler, [Primary, Secondary]);

        Assert.AreEqual(Secondary, result.Source);
    }

    [TestMethod]
    public async Task FallsBackWhenThePrimaryCannotBeReached()
    {
        var handler = new StubHandler((Secondary, HttpStatusCode.OK))
        {
            Throw = { [Primary] = new HttpRequestException("Name or service not known") }
        };

        var result = await FetchAsync(handler, [Primary, Secondary]);

        Assert.AreEqual(Secondary, result.Source);
    }

    [TestMethod]
    public async Task FallsBackWhenThePrimaryDoesNotRespondInTime()
    {
        var handler = new StubHandler((Secondary, HttpStatusCode.OK))
        {
            Hang = { Primary }
        };

        var result = await FetchAsync(handler, [Primary, Secondary], responseTimeout: TimeSpan.FromMilliseconds(50));

        Assert.AreEqual(Secondary, result.Source);
    }

    [TestMethod]
    public async Task StartsAtTheRequestedSourceAndWrapsAround()
    {
        var handler = new StubHandler(
            (Primary, HttpStatusCode.OK),
            (Secondary, HttpStatusCode.ServiceUnavailable),
            (Tertiary, HttpStatusCode.ServiceUnavailable));

        var result = await FetchAsync(handler, [Primary, Secondary, Tertiary], startIndex: 1);

        Assert.AreEqual(0, result.SourceIndex);
        CollectionAssert.AreEqual(new[] { Secondary, Tertiary, Primary }, handler.Requested);
    }

    [TestMethod]
    public async Task ReportsEverySourceWhenAllFail()
    {
        var handler = new StubHandler(
            (Primary, HttpStatusCode.ServiceUnavailable),
            (Secondary, HttpStatusCode.NotFound));

        var exception = await Assert.ThrowsExactlyAsync<HttpRequestException>(
            () => FetchAsync(handler, [Primary, Secondary]));

        StringAssert.Contains(exception.Message, "All 2");
        StringAssert.Contains(exception.Message, "primary.example returned 503");
        StringAssert.Contains(exception.Message, "secondary.example returned 404");
        Assert.AreEqual(HttpStatusCode.NotFound, exception.StatusCode);
    }

    [TestMethod]
    public async Task ReportsASingleSourceFailureWithoutTheAllSourcesWording()
    {
        var handler = new StubHandler((Primary, HttpStatusCode.ServiceUnavailable));

        var exception = await Assert.ThrowsExactlyAsync<HttpRequestException>(
            () => FetchAsync(handler, [Primary]));

        Assert.IsFalse(exception.Message.Contains("All 1", StringComparison.Ordinal));
        StringAssert.Contains(exception.Message, "primary.example returned 503");
    }

    [TestMethod]
    public async Task DoesNotTryFurtherSourcesWhenTheCallerCancels()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new StubHandler((Secondary, HttpStatusCode.OK))
        {
            OnRequest = uri =>
            {
                if (uri == Primary)
                {
                    cancellation.Cancel();
                }
            },
            Hang = { Primary }
        };

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => FetchAsync(handler, [Primary, Secondary], cancellationToken: cancellation.Token));

        CollectionAssert.AreEqual(new[] { Primary }, handler.Requested);
    }

    [TestMethod]
    public void RejectsAnEmptySourceList()
    {
        var handler = new StubHandler();

        Assert.ThrowsExactly<ArgumentException>(() =>
            GutenbergCatalogSourceFetcher.GetAsync(
                new HttpClient(handler), [], TimeSpan.FromSeconds(1), HttpCompletionOption.ResponseHeadersRead, 0, CancellationToken.None)
                .GetAwaiter().GetResult());
    }

    [TestMethod]
    public void DefaultOptionsFallBackToTheContentMirror()
    {
        var options = new GutenbergCatalogOptions();

        CollectionAssert.AreEqual(
            new[] { GutenbergCatalogOptions.DefaultArchiveUrl, GutenbergCatalogOptions.DefaultFallbackArchiveUrl },
            options.ArchiveUrls.ToArray());
        CollectionAssert.AreEqual(
            new[] { GutenbergCatalogOptions.DefaultRecentUpdatesFeedUrl, GutenbergCatalogOptions.DefaultFallbackRecentUpdatesFeedUrl },
            options.RecentUpdatesFeedUrls.ToArray());
        CollectionAssert.AreEqual(
            new[] { GutenbergCatalogOptions.DefaultEbookRdfBaseUrl, GutenbergCatalogOptions.DefaultFallbackEbookRdfBaseUrl },
            options.EbookRdfBaseUrls.ToArray());
    }

    [TestMethod]
    public void ConfiguredFallbacksReplaceTheDefaultAndMayBeEmpty()
    {
        var replaced = new GutenbergCatalogOptions { FallbackArchiveUrls = ["https://mirror.example/rdf-files.tar.bz2"] };
        var disabled = new GutenbergCatalogOptions { FallbackArchiveUrls = [] };

        CollectionAssert.AreEqual(
            new[] { GutenbergCatalogOptions.DefaultArchiveUrl, "https://mirror.example/rdf-files.tar.bz2" },
            replaced.ArchiveUrls.ToArray());
        CollectionAssert.AreEqual(new[] { GutenbergCatalogOptions.DefaultArchiveUrl }, disabled.ArchiveUrls.ToArray());
    }

    private static Task<GutenbergCatalogSourceResponse> FetchAsync(
        StubHandler handler,
        IReadOnlyList<Uri> sources,
        int startIndex = 0,
        TimeSpan? responseTimeout = null,
        CancellationToken cancellationToken = default) =>
        GutenbergCatalogSourceFetcher.GetAsync(
            new HttpClient(handler),
            sources,
            responseTimeout ?? TimeSpan.FromSeconds(30),
            HttpCompletionOption.ResponseHeadersRead,
            startIndex,
            cancellationToken);

    private sealed class StubHandler(params (Uri Uri, HttpStatusCode Status)[] responses) : HttpMessageHandler
    {
        public List<Uri> Requested { get; } = [];

        public Dictionary<Uri, Exception> Throw { get; } = [];

        public HashSet<Uri> Hang { get; } = [];

        public Action<Uri>? OnRequest { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requested.Add(uri);
            OnRequest?.Invoke(uri);
            if (Throw.TryGetValue(uri, out var exception))
            {
                throw exception;
            }

            if (Hang.Contains(uri))
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            var status = responses.Single(response => response.Uri == uri).Status;
            return new HttpResponseMessage(status) { Content = new StringContent("<rss/>") };
        }
    }
}
