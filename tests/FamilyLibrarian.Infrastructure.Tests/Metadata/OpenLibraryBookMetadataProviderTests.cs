using System.Net;
using System.Text;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Infrastructure.Metadata;
using Microsoft.Extensions.Options;

namespace FamilyLibrarian.Infrastructure.Tests.Metadata;

[TestClass]
public sealed class OpenLibraryBookMetadataProviderTests
{
    [TestMethod]
    public async Task SearchAsyncNormalizesWorkAndEditionEvidence()
    {
        Uri? requestedUri = null;
        using var handler = new StubHttpMessageHandler((request, _) =>
        {
            requestedUri = request.RequestUri;
            return JsonResponse(
                """
                {
                  "docs": [
                    {
                      "key": "/works/OL123W",
                      "title": " Project Hail Mary ",
                      "author_name": ["Andy Weir", "Andy Weir"],
                      "first_publish_date": ["2021-05-04"],
                      "cover_i": 12345,
                      "editions": {
                        "docs": [
                          {
                            "title": "Project Hail Mary",
                            "isbn": ["0-593-13520-2"],
                            "publish_date": ["May 4, 2021"],
                            "format": ["Ebook"]
                          }
                        ]
                      }
                    },
                    {
                      "key": "/books/OL999M",
                      "title": "Not a work"
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
        Assert.AreEqual("openlibrary", candidate.ProviderId);
        Assert.AreEqual("Open Library", candidate.ProviderName);
        Assert.AreEqual("OL123W", candidate.ExternalId);
        Assert.AreEqual("Project Hail Mary", candidate.Title);
        Assert.HasCount(1, candidate.Authors);
        Assert.AreEqual("Andy Weir", candidate.Authors[0]);
        Assert.AreEqual(new DateOnly(2021, 5, 4), candidate.PublicationDate);
        Assert.AreEqual(
            "https://covers.openlibrary.org/b/id/12345-L.jpg?default=false",
            candidate.CoverUrl);
        Assert.HasCount(1, candidate.Editions);
        Assert.AreEqual("9780593135204", candidate.Editions[0].Isbn13);
        Assert.AreEqual("Ebook", candidate.Editions[0].Format);
        Assert.AreEqual(new DateOnly(2021, 5, 4), candidate.Editions[0].PublicationDate);
        Assert.IsNotNull(requestedUri);
        StringAssert.Contains(requestedUri.Query, "q=Project%20Hail%20Mary");
        StringAssert.Contains(requestedUri.Query, "limit=10");
    }

    [TestMethod]
    public async Task SearchAsyncUsesValidatedIsbnQueryAndDoesNotInventYearPrecision()
    {
        Uri? requestedUri = null;
        using var handler = new StubHttpMessageHandler((request, _) =>
        {
            requestedUri = request.RequestUri;
            return JsonResponse(
                """
                {
                  "docs": [
                    {
                      "key": "/works/OL456W",
                      "title": "The Hobbit",
                      "author_name": ["J. R. R. Tolkien"],
                      "first_publish_date": ["1937"],
                      "isbn": ["9780547928227"]
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
        Assert.HasCount(1, results[0].Editions);
        Assert.AreEqual("9780547928227", results[0].Editions[0].Isbn13);
        Assert.IsNotNull(requestedUri);
        StringAssert.Contains(requestedUri.Query, "q=isbn%3A9780547928227");
    }

    [TestMethod]
    public async Task GetDetailsAsyncMapsObjectDescriptionAndRejectsInvalidWorkId()
    {
        var requestCount = 0;
        using var handler = new StubHttpMessageHandler((request, _) =>
        {
            requestCount++;
            StringAssert.Contains(request.RequestUri!.Query, "q=key%3A%2Fworks%2FOL789W");
            return JsonResponse(
                """
                {
                  "docs": [
                    {
                      "key": "/works/OL789W",
                      "title": "A Wrinkle in Time",
                      "author_name": ["Madeleine L'Engle"],
                      "description": { "value": "  A description from the provider.  " }
                    }
                  ]
                }
                """);
        });
        using var httpClient = CreateHttpClient(handler);
        var provider = CreateProvider(httpClient);

        var invalid = await provider.GetDetailsAsync("../bad", CancellationToken.None);
        var valid = await provider.GetDetailsAsync("OL789W", CancellationToken.None);

        Assert.IsNull(invalid);
        Assert.IsNotNull(valid);
        Assert.AreEqual("A description from the provider.", valid.Description);
        Assert.AreEqual(1, requestCount);
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

    private static OpenLibraryBookMetadataProvider CreateProvider(HttpClient httpClient) =>
        new(
            httpClient,
            Options.Create(new OpenLibraryMetadataOptions
            {
                Enabled = true,
                MaxResults = 10,
                TimeoutSeconds = 15,
                RequestsPerSecond = 1
            }));

    private static HttpClient CreateHttpClient(HttpMessageHandler handler) => new(handler)
    {
        BaseAddress = new Uri("https://openlibrary.org/")
    };

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

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
