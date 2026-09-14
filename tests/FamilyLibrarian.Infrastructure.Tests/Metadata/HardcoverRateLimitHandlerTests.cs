using System.Net;
using System.Text;
using FamilyLibrarian.Infrastructure.Metadata;
using Microsoft.Extensions.Options;

namespace FamilyLibrarian.Infrastructure.Tests.Metadata;

[TestClass]
public sealed class HardcoverRateLimitHandlerTests
{
    [TestMethod]
    public async Task SendAsyncRetriesAfterA429AndSucceedsOnTheNextAttempt()
    {
        var attempt = 0;
        using var innerHandler = new StubHttpMessageHandler((_, _) =>
        {
            attempt++;
            if (attempt == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                    TimeSpan.FromMilliseconds(1));
                return Task.FromResult(response);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("ok", Encoding.UTF8, "text/plain")
            });
        });
        using var httpClient = CreateHttpClient(innerHandler, maxRetryAttempts: 3);

        var response = await httpClient.PostAsync(
            string.Empty,
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(2, attempt);
    }

    [TestMethod]
    public async Task SendAsyncGivesUpAndReturnsThe429AfterExhaustingConfiguredRetries()
    {
        var attempt = 0;
        using var innerHandler = new StubHttpMessageHandler((_, _) =>
        {
            attempt++;
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                TimeSpan.FromMilliseconds(1));
            return Task.FromResult(response);
        });
        using var httpClient = CreateHttpClient(innerHandler, maxRetryAttempts: 2);

        var response = await httpClient.PostAsync(
            string.Empty,
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.AreEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
        // The initial send plus exactly MaxRetryAttempts retries, no more.
        Assert.AreEqual(3, attempt);
    }

    [TestMethod]
    public async Task SendAsyncResendsTheOriginalRequestBodyOnRetry()
    {
        var bodiesSeen = new List<string>();
        var attempt = 0;
        using var innerHandler = new StubHttpMessageHandler(async (request, ct) =>
        {
            attempt++;
            bodiesSeen.Add(await request.Content!.ReadAsStringAsync(ct));
            if (attempt == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                    TimeSpan.FromMilliseconds(1));
                return response;
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var httpClient = CreateHttpClient(innerHandler, maxRetryAttempts: 3);

        await httpClient.PostAsync(
            string.Empty,
            new StringContent("""{"query":"..."}""", Encoding.UTF8, "application/json"));

        Assert.HasCount(2, bodiesSeen);
        Assert.AreEqual(bodiesSeen[0], bodiesSeen[1]);
    }

    [TestMethod]
    public async Task SendAsyncDoesNotRetryANonRateLimitedFailure()
    {
        var attempt = 0;
        using var innerHandler = new StubHttpMessageHandler((_, _) =>
        {
            attempt++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        });
        using var httpClient = CreateHttpClient(innerHandler, maxRetryAttempts: 3);

        var response = await httpClient.PostAsync(
            string.Empty,
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.AreEqual(1, attempt);
    }

    private static HttpClient CreateHttpClient(HttpMessageHandler innerHandler, int maxRetryAttempts)
    {
        var handler = new HardcoverRateLimitHandler(Options.Create(new HardcoverMetadataOptions
        {
            MaxRetryAttempts = maxRetryAttempts,
            MaxRetryDelaySeconds = 30
        }))
        {
            InnerHandler = innerHandler
        };

        return new HttpClient(handler) { BaseAddress = new Uri("https://api.hardcover.app/v1/graphql") };
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            responseFactory(request, cancellationToken);
    }
}
