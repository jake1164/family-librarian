using System.Net;
using System.Text;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Infrastructure.Integrations;
using FamilyLibrarian.Infrastructure.Metadata;
using Microsoft.Extensions.Options;

namespace FamilyLibrarian.Infrastructure.Tests.Metadata;

[TestClass]
public sealed class GoogleBooksBookMetadataProviderTests
{
    [TestMethod]
    public async Task SearchAsyncNormalizesVolumeEvidenceAndAddsTheKeyInTheHandler()
    {
        Uri? requestedUri = null;
        using var handler = new StubHttpMessageHandler((request, _) =>
        {
            requestedUri = request.RequestUri;
            return JsonResponse(
                """
                {
                  "items": [
                    {
                      "id": "google-volume-123",
                      "volumeInfo": {
                        "title": " Project Hail Mary ",
                        "subtitle": " A Novel ",
                        "authors": ["Andy Weir", "Andy Weir"],
                        "description": "  A description from the provider.  ",
                        "publishedDate": "2021-05-04",
                        "publisher": " Ballantine Books ",
                        "pageCount": 496,
                        "categories": ["Fiction / Science Fiction / General", "Fiction / Science Fiction / General"],
                        "industryIdentifiers": [
                          { "type": "ISBN_10", "identifier": "0-593-13520-2" },
                          { "type": "ISBN_13", "identifier": "9780593135204" }
                        ],
                        "imageLinks": {
                          "thumbnail": "http://books.google.com/cover.jpg"
                        }
                      }
                    }
                  ]
                }
                """);
        });
        using var httpClient = CreateHttpClient(handler);
        var provider = CreateProvider(httpClient);

        var results = await provider.SearchAsync(
            new BookSearchQuery("Project Hail Mary"),
            CancellationToken.None);

        Assert.HasCount(1, results);
        var candidate = results[0];
        Assert.AreEqual("googlebooks", candidate.ProviderId);
        Assert.AreEqual("Google Books", candidate.ProviderName);
        Assert.AreEqual("google-volume-123", candidate.ExternalId);
        Assert.AreEqual("Project Hail Mary: A Novel", candidate.Title);
        Assert.HasCount(1, candidate.Authors);
        Assert.AreEqual("Andy Weir", candidate.Authors[0]);
        Assert.AreEqual("A description from the provider.", candidate.Description);
        Assert.AreEqual("https://books.google.com/cover.jpg", candidate.CoverUrl);
        Assert.AreEqual(new DateOnly(2021, 5, 4), candidate.PublicationDate);
        Assert.HasCount(1, candidate.Editions);
        Assert.AreEqual("9780593135204", candidate.Editions[0].Isbn13);
        Assert.AreEqual("Unknown format", candidate.Editions[0].Format);
        Assert.AreEqual("Ballantine Books", candidate.Publisher);
        Assert.AreEqual(496, candidate.PageCount);
        Assert.HasCount(1, candidate.Subjects);
        Assert.AreEqual("Fiction / Science Fiction / General", candidate.Subjects[0]);
        Assert.IsNotNull(requestedUri);
        StringAssert.Contains(requestedUri.Query, "q=Project%20Hail%20Mary");
        StringAssert.Contains(requestedUri.Query, "key=test-api-key");
    }

    [TestMethod]
    public async Task SearchAsyncUsesNormalizedIsbnAndDoesNotInventDatePrecision()
    {
        Uri? requestedUri = null;
        using var handler = new StubHttpMessageHandler((request, _) =>
        {
            requestedUri = request.RequestUri;
            return JsonResponse(
                """
                {
                  "items": [
                    {
                      "id": "hObbit-1",
                      "volumeInfo": {
                        "title": "The Hobbit",
                        "publishedDate": "1937",
                        "industryIdentifiers": [
                          { "type": "ISBN_13", "identifier": "9780547928227" }
                        ]
                      }
                    }
                  ]
                }
                """);
        });
        using var httpClient = CreateHttpClient(handler);
        var provider = CreateProvider(httpClient);

        var results = await provider.SearchAsync(
            new BookSearchQuery("978-0-547-92822-7"),
            CancellationToken.None);

        Assert.HasCount(1, results);
        Assert.IsNull(results[0].PublicationDate);
        Assert.AreEqual("9780547928227", results[0].Editions[0].Isbn13);
        Assert.IsNotNull(requestedUri);
        StringAssert.Contains(requestedUri.Query, "q=isbn%3A9780547928227");
    }

    [TestMethod]
    public async Task GetDetailsAsyncRejectsInvalidVolumeIdWithoutSendingARequest()
    {
        var requestCount = 0;
        using var handler = new StubHttpMessageHandler((_, _) =>
        {
            requestCount++;
            return JsonResponse("{}");
        });
        using var httpClient = CreateHttpClient(handler);
        var provider = CreateProvider(httpClient);

        var result = await provider.GetDetailsAsync("../bad", CancellationToken.None);

        Assert.IsNull(result);
        Assert.AreEqual(0, requestCount);
    }

    [TestMethod]
    public async Task SearchAsyncPropagatesProviderHttpFailure()
    {
        using var handler = new StubHttpMessageHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        using var httpClient = CreateHttpClient(handler);
        var provider = CreateProvider(httpClient);

        await Assert.ThrowsExactlyAsync<HttpRequestException>(() =>
            provider.SearchAsync(new BookSearchQuery("Dune"), CancellationToken.None));
    }

    private static GoogleBooksBookMetadataProvider CreateProvider(HttpClient httpClient) =>
        new(
            httpClient,
            Options.Create(new GoogleBooksMetadataOptions
            {
                Enabled = true,
                ApiKey = "test-api-key",
                MaxResults = 10,
                TimeoutSeconds = 15
            }));

    private static HttpClient CreateHttpClient(HttpMessageHandler innerHandler)
    {
        var handler = new GoogleBooksApiKeyHandler(new StubCredentialAccessor("test-api-key"))
        {
            InnerHandler = innerHandler
        };

        return new HttpClient(handler)
        {
            BaseAddress = new Uri("https://www.googleapis.com/books/v1/")
        };
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class StubCredentialAccessor(string? apiKey) : IMetadataCredentialAccessor
    {
        public Task<string?> GetCredentialAsync(
            string providerId,
            CancellationToken cancellationToken) =>
            Task.FromResult(apiKey);
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request, cancellationToken));
    }
}
