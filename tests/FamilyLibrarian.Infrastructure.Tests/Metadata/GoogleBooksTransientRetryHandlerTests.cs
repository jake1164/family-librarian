using System.Net;
using FamilyLibrarian.Infrastructure.Metadata;

namespace FamilyLibrarian.Infrastructure.Tests.Metadata;

/// <summary>The lab: Google Books answered 503 backendFailed to about four requests in seven, so Test Connection failed.</summary>
[TestClass]
public sealed class GoogleBooksTransientRetryHandlerTests
{
    private static readonly TimeSpan[] NoWait = [TimeSpan.Zero, TimeSpan.Zero];
    private static readonly string?[] KeptTwice = ["kept", "kept"];

    private sealed class ScriptedHandler(params HttpStatusCode[] answers) : HttpMessageHandler
    {
        public List<Uri?> Requests { get; } = [];

        public List<string?> Keys { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri);
            Keys.Add(request.Headers.TryGetValues("X-Probe", out var values) ? values.Single() : null);
            var status = answers[Math.Min(Requests.Count - 1, answers.Length - 1)];
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    private static (HttpClient Client, ScriptedHandler Inner) Client(params HttpStatusCode[] answers)
    {
        var inner = new ScriptedHandler(answers);
        return (new HttpClient(new GoogleBooksTransientRetryHandler(NoWait) { InnerHandler = inner }), inner);
    }

    [TestMethod]
    public async Task ATransientErrorIsRetriedUntilGoogleAnswers()
    {
        var (client, inner) = Client(HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);

        var response = await client.GetAsync(new Uri("https://example.test/volumes?q=x&key=SECRET"));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.HasCount(3, inner.Requests);
        Assert.IsTrue(inner.Requests.All(uri => uri!.Query.Contains("key=SECRET")), "Every attempt carries the same query, key included.");
    }

    [TestMethod]
    public async Task AGenuineOutageStillSurfacesAsTheProvidersOwnStatusAfterTheLastRetry()
    {
        var (client, inner) = Client(HttpStatusCode.ServiceUnavailable);

        var response = await client.GetAsync(new Uri("https://example.test/volumes?q=x"));

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.HasCount(3, inner.Requests, "One attempt plus two retries, then it gives up.");
    }

    [TestMethod]
    [DataRow(HttpStatusCode.NotFound)]
    [DataRow(HttpStatusCode.BadRequest)]
    [DataRow(HttpStatusCode.Forbidden)]
    [DataRow(HttpStatusCode.TooManyRequests)]
    public async Task AnswersThatRetryingCannotFixAreReturnedAtOnce(HttpStatusCode status)
    {
        var (client, inner) = Client(status);

        var response = await client.GetAsync(new Uri("https://example.test/volumes?q=x"));

        Assert.AreEqual(status, response.StatusCode);
        Assert.HasCount(1, inner.Requests);
    }

    [TestMethod]
    public async Task ARequestWithABodyIsNeverRetried()
    {
        var (client, inner) = Client(HttpStatusCode.ServiceUnavailable);

        var response = await client.PostAsync(new Uri("https://example.test/volumes"), new StringContent("{}"));

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.HasCount(1, inner.Requests);
    }

    [TestMethod]
    public async Task HeadersAreCopiedToTheRetry()
    {
        var (client, inner) = Client(HttpStatusCode.BadGateway, HttpStatusCode.OK);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/volumes?q=x");
        request.Headers.Add("X-Probe", "kept");

        await client.SendAsync(request);

        CollectionAssert.AreEqual(KeptTwice, inner.Keys);
    }

    [TestMethod]
    public async Task CancellingDuringTheWaitStopsTheRetries()
    {
        var inner = new ScriptedHandler(HttpStatusCode.ServiceUnavailable);
        using var client = new HttpClient(new GoogleBooksTransientRetryHandler([TimeSpan.FromMinutes(5)]) { InnerHandler = inner });
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => client.GetAsync(new Uri("https://example.test/volumes?q=x"), cancel.Token));
        Assert.HasCount(1, inner.Requests);
    }
}
