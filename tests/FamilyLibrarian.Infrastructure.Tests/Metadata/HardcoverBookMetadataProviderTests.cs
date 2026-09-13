using System.Net;
using System.Text;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Infrastructure.Integrations;
using FamilyLibrarian.Infrastructure.Metadata;

namespace FamilyLibrarian.Infrastructure.Tests.Metadata;

[TestClass]
public sealed class HardcoverBookMetadataProviderTests
{
    [TestMethod]
    public async Task SearchAsyncIssuesASearchThenABooksLookupAndOrdersCandidatesByRelevance()
    {
        var requestBodies = new List<string>();
        using var handler = new StubHttpMessageHandler(async (request, ct) =>
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            requestBodies.Add(body);

            if (body.Contains("query Search", StringComparison.Ordinal))
            {
                return JsonResponse("""{"data":{"search":{"ids":[42,7]}}}""");
            }

            // Deliberately returned out of relevance order to prove the
            // provider re-sorts by the ids the search call ranked, not by
            // whatever order the books lookup happened to return them in.
            return JsonResponse(
                """
                {
                  "data": {
                    "books": [
                      {
                        "id": 7,
                        "title": "Red Storm Rising",
                        "contributions": [{ "author": { "name": "Tom Clancy" } }]
                      },
                      {
                        "id": 42,
                        "title": "The Hunt for Red October",
                        "contributions": [{ "author": { "name": "Tom Clancy" } }]
                      }
                    ]
                  }
                }
                """);
        });
        var provider = CreateProvider(handler);

        var result = await provider.SearchAsync(new BookSearchQuery("clancy"), CancellationToken.None);

        Assert.HasCount(2, result.Candidates);
        Assert.AreEqual("42", result.Candidates[0].ExternalId);
        Assert.AreEqual("The Hunt for Red October", result.Candidates[0].Title);
        Assert.AreEqual("7", result.Candidates[1].ExternalId);
        Assert.HasCount(2, requestBodies);
        StringAssert.Contains(requestBodies[1], "BooksByIds");
    }

    [TestMethod]
    public async Task SearchAsyncDropsAStubBookThatHasNothingButATitle()
    {
        // Reproduces a real result observed live: searching Hardcover for the
        // author "Tom Clancy" surfaced a bare book, itself titled
        // "Tom Clancy", with no cover, no description, no linked contributor,
        // and no editions — a community-catalog stub, not a usable result.
        using var handler = new StubHttpMessageHandler(async (request, ct) =>
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            if (body.Contains("query Search", StringComparison.Ordinal))
            {
                return JsonResponse("""{"data":{"search":{"ids":[7,99]}}}""");
            }

            return JsonResponse(
                """
                {
                  "data": {
                    "books": [
                      {
                        "id": 7,
                        "title": "Red Storm Rising",
                        "contributions": [{ "author": { "name": "Tom Clancy" } }]
                      },
                      { "id": 99, "title": "Tom Clancy" }
                    ]
                  }
                }
                """);
        });
        var provider = CreateProvider(handler);

        var result = await provider.SearchAsync(new BookSearchQuery("Tom Clancy"), CancellationToken.None);

        Assert.HasCount(1, result.Candidates);
        Assert.AreEqual("Red Storm Rising", result.Candidates[0].Title);
    }

    [TestMethod]
    public async Task SearchAsyncReturnsAnEmptyPageWithoutASecondRequestWhenNothingMatches()
    {
        var requestCount = 0;
        using var handler = new StubHttpMessageHandler((_, _) =>
        {
            requestCount++;
            return Task.FromResult(JsonResponse("""{"data":{"search":{"ids":[]}}}"""));
        });
        var provider = CreateProvider(handler);

        var result = await provider.SearchAsync(new BookSearchQuery("no such book"), CancellationToken.None);

        Assert.IsEmpty(result.Candidates);
        Assert.IsFalse(result.HasMore);
        Assert.AreEqual(1, requestCount);
    }

    [TestMethod]
    public async Task GetDetailsAsyncRejectsANonNumericExternalIdWithoutSendingARequest()
    {
        var requestCount = 0;
        using var handler = new StubHttpMessageHandler((_, _) =>
        {
            requestCount++;
            return Task.FromResult(JsonResponse("{}"));
        });
        var provider = CreateProvider(handler);

        var result = await provider.GetDetailsAsync("not-a-hardcover-id", CancellationToken.None);

        Assert.IsNull(result);
        Assert.AreEqual(0, requestCount);
    }

    [TestMethod]
    public async Task GetDetailsAsyncMapsEveryFieldAccordingToTheRealSchema()
    {
        using var handler = new StubHttpMessageHandler(async (request, ct) =>
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            if (body.Contains("languages {", StringComparison.Ordinal))
            {
                return JsonResponse(
                    """{"data":{"languages":[{"id":1,"code2":"en"},{"id":17,"code2":"es"}]}}""");
            }

            return JsonResponse(
                """
                {
                  "data": {
                    "books_by_pk": {
                      "id": 235462,
                      "title": " The Hobbit ",
                      "description": "  Bilbo goes on an adventure.  ",
                      "release_date": "1937-09-21",
                      "slug": "the-hobbit",
                      "cached_image": { "url": "https://assets.hardcover.app/covers/hobbit.jpg" },
                      "contributions": [
                        { "author": { "name": "J.R.R. Tolkien" } },
                        { "author": { "name": "J.R.R. Tolkien" } }
                      ],
                      "book_series": [
                        {
                          "position": 1.0,
                          "featured": true,
                          "series": { "name": "Middle-earth", "is_completed": true }
                        }
                      ],
                      "default_physical_edition": { "language_id": 1, "pages": 310 },
                      "default_ebook_edition": null,
                      "editions": [
                        {
                          "isbn_13": null,
                          "isbn_10": "0-395-07122-4",
                          "edition_format": "Hardcover",
                          "release_date": "1937-09-21"
                        }
                      ]
                    }
                  }
                }
                """);
        });
        var provider = CreateProvider(handler);

        var candidate = await provider.GetDetailsAsync("235462", CancellationToken.None);

        Assert.IsNotNull(candidate);
        Assert.AreEqual("hardcover", candidate.ProviderId);
        Assert.AreEqual("235462", candidate.ExternalId);
        Assert.AreEqual("The Hobbit", candidate.Title);
        Assert.AreEqual("Bilbo goes on an adventure.", candidate.Description);
        Assert.AreEqual(new DateOnly(1937, 9, 21), candidate.PublicationDate);
        Assert.AreEqual("https://assets.hardcover.app/covers/hobbit.jpg", candidate.CoverUrl);
        Assert.AreEqual(310, candidate.PageCount);
        Assert.AreEqual("en", candidate.Language);
        Assert.AreEqual("https://hardcover.app/books/the-hobbit", candidate.SourceUrl);
        Assert.HasCount(1, candidate.Authors);
        Assert.AreEqual("J.R.R. Tolkien", candidate.Authors[0]);

        Assert.HasCount(1, candidate.Editions);
        var edition = candidate.Editions[0];
        Assert.AreEqual("Hardcover", edition.Format);
        Assert.AreEqual(new DateOnly(1937, 9, 21), edition.PublicationDate);
        // isbn_13 was null on the wire; isbn_10 must still be normalized into it.
        Assert.AreEqual("9780395071229", edition.Isbn13);

        Assert.HasCount(1, candidate.Series);
        var series = candidate.Series[0];
        Assert.AreEqual("Middle-earth", series.Name);
        Assert.IsTrue(series.IsPrimary);
        Assert.AreEqual(1m, series.PositionSort);
        Assert.AreEqual("1", series.PositionLabel);
        Assert.IsTrue(series.IsCompleted);
    }

    [TestMethod]
    public async Task GetDetailsAsyncReturnsNullWhenTheBookIsNotFound()
    {
        using var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(JsonResponse("""{"data":{"books_by_pk":null}}""")));
        var provider = CreateProvider(handler);

        var result = await provider.GetDetailsAsync("999999999", CancellationToken.None);

        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task SearchAsyncPropagatesAPersistentRateLimitFailure()
    {
        using var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)));
        var provider = CreateProvider(handler);

        await Assert.ThrowsExactlyAsync<HttpRequestException>(() =>
            provider.SearchAsync(new BookSearchQuery("dune"), CancellationToken.None));
    }

    private static HardcoverBookMetadataProvider CreateProvider(HttpMessageHandler innerHandler)
    {
        var authorizedHandler = new HardcoverAuthorizationHandler(new StubCredentialAccessor("test-token"))
        {
            InnerHandler = innerHandler
        };
        var httpClient = new HttpClient(authorizedHandler)
        {
            BaseAddress = new Uri("https://api.hardcover.app/v1/graphql")
        };

        var languageHttpClient = new HttpClient(new HardcoverAuthorizationHandler(
            new StubCredentialAccessor("test-token"))
        {
            InnerHandler = innerHandler
        })
        {
            BaseAddress = new Uri("https://api.hardcover.app/v1/graphql")
        };
        var languageLookup = new HardcoverLanguageLookupCache(
            new StubHttpClientFactory(languageHttpClient));

        return new HardcoverBookMetadataProvider(httpClient, languageLookup);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class StubCredentialAccessor(string? token) : IMetadataCredentialAccessor
    {
        public Task<string?> GetCredentialAsync(
            string providerId,
            CancellationToken cancellationToken) =>
            Task.FromResult(token);
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
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
