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
                      "publisher": ["Ballantine Books", "Ballantine Books"],
                      "subject": ["Science fiction", "Space flight", "Science fiction"],
                      "number_of_pages_median": 476,
                      "language": ["eng"],
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

        Assert.HasCount(1, results.Candidates);
        Assert.IsFalse(results.HasMore);
        var candidate = results.Candidates[0];
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
        Assert.AreEqual("Ballantine Books", candidate.Publisher);
        Assert.AreEqual(476, candidate.PageCount);
        Assert.HasCount(2, candidate.Subjects);
        Assert.AreEqual("Science fiction", candidate.Subjects[0]);
        Assert.AreEqual("Space flight", candidate.Subjects[1]);
        Assert.AreEqual("en", candidate.Language);
        Assert.IsNotNull(requestedUri);
        StringAssert.Contains(requestedUri.Query, "q=Project%20Hail%20Mary");
        StringAssert.Contains(requestedUri.Query, "limit=10");
    }

    [TestMethod]
    public async Task SearchAsyncPrefersEditionLevelCoverPublisherAndLanguageOverWorkAggregate()
    {
        using var handler = new StubHttpMessageHandler((_, _) =>
            JsonResponse(
                """
                {
                  "docs": [
                    {
                      "key": "/works/OL448924W",
                      "title": "Clear and Present Danger",
                      "author_name": ["Tom Clancy"],
                      "cover_i": 999,
                      "publisher": ["France loisir"],
                      "language": ["fre", "eng"],
                      "editions": {
                        "docs": [
                          {
                            "title": "Clear and Present Danger",
                            "cover_i": 15249157,
                            "publisher": ["G. P. Putnam's Sons"],
                            "language": ["eng"]
                          }
                        ]
                      }
                    }
                  ]
                }
                """));
        using var httpClient = CreateHttpClient(handler);
        var provider = CreateProvider(httpClient);

        var results = await provider.SearchAsync(
            new BookSearchQuery("Tom Clancy"),
            CancellationToken.None);

        Assert.HasCount(1, results.Candidates);
        var candidate = results.Candidates[0];
        Assert.AreEqual(
            "https://covers.openlibrary.org/b/id/15249157-L.jpg?default=false",
            candidate.CoverUrl);
        Assert.AreEqual("G. P. Putnam's Sons", candidate.Publisher);
        Assert.AreEqual("en", candidate.Language);
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

        Assert.HasCount(1, results.Candidates);
        Assert.IsNull(results.Candidates[0].PublicationDate);
        Assert.HasCount(1, results.Candidates[0].Editions);
        Assert.AreEqual("9780547928227", results.Candidates[0].Editions[0].Isbn13);
        Assert.IsNotNull(requestedUri);
        StringAssert.Contains(requestedUri.Query, "q=isbn%3A9780547928227");
    }

    [TestMethod]
    public async Task SearchAsyncRequestsTheSelectedPageAndReportsMoreResults()
    {
        Uri? requestedUri = null;
        using var handler = new StubHttpMessageHandler((request, _) =>
        {
            requestedUri = request.RequestUri;
            return JsonResponse(
                """
                {
                  "num_found": 21,
                  "docs": [
                    { "key": "/works/OL123W", "title": "The Sum of All Fears" }
                  ]
                }
                """);
        });
        using var httpClient = CreateHttpClient(handler);
        var provider = CreateProvider(httpClient);

        var results = await provider.SearchAsync(
            new BookSearchQuery("Tom Clancy", Page: 2),
            CancellationToken.None);

        Assert.HasCount(1, results.Candidates);
        Assert.IsTrue(results.HasMore);
        Assert.IsNotNull(requestedUri);
        StringAssert.Contains(requestedUri.Query, "page=2");
    }

    [TestMethod]
    public async Task GetDetailsAsyncMapsObjectDescriptionAndRejectsInvalidWorkId()
    {
        var requestCount = 0;
        using var handler = new StubHttpMessageHandler((request, _) =>
        {
            requestCount++;
            if (request.RequestUri!.AbsolutePath.EndsWith("editions.json", StringComparison.Ordinal))
            {
                return JsonResponse("""{ "entries": [] }""");
            }

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
        Assert.AreEqual(2, requestCount);
    }

    [TestMethod]
    public async Task GetDetailsAsyncPrefersAPreferredLanguageEditionWhenOneExists()
    {
        using var handler = new StubHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("editions.json", StringComparison.Ordinal))
            {
                return JsonResponse(
                    """
                    {
                      "entries": [
                        {
                          "key": "/books/OL1FRE",
                          "languages": [{ "key": "/languages/fre" }],
                          "publishers": ["France loisir"],
                          "number_of_pages": 651,
                          "covers": [111]
                        },
                        {
                          "key": "/books/OL2ENG",
                          "languages": [{ "key": "/languages/eng" }],
                          "publishers": ["G. P. Putnam's Sons"],
                          "number_of_pages": 338,
                          "covers": [222]
                        }
                      ]
                    }
                    """);
            }

            return JsonResponse(
                """
                {
                  "docs": [
                    {
                      "key": "/works/OL448924W",
                      "title": "Clear and Present Danger",
                      "author_name": ["Tom Clancy"],
                      "cover_i": 999,
                      "publisher": ["France loisir"],
                      "language": ["fre", "eng"]
                    }
                  ]
                }
                """);
        });
        using var httpClient = CreateHttpClient(handler);
        var provider = CreateProvider(httpClient);

        var candidate = await provider.GetDetailsAsync("OL448924W", CancellationToken.None);

        Assert.IsNotNull(candidate);
        Assert.AreEqual("en", candidate.Language);
        Assert.AreEqual("G. P. Putnam's Sons", candidate.Publisher);
        Assert.AreEqual(338, candidate.PageCount);
        Assert.AreEqual(
            "https://covers.openlibrary.org/b/id/222-L.jpg?default=false",
            candidate.CoverUrl);
        Assert.AreEqual("https://openlibrary.org/books/OL2ENG", candidate.SourceUrl);
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
