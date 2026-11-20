using System.Net;
using System.Text;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Catalog.Discovery;
using FamilyLibrarian.Domain.Catalog;
using FamilyLibrarian.Infrastructure.Integrations;
using FamilyLibrarian.Infrastructure.Metadata;
using FamilyLibrarian.Infrastructure.Tests.Catalog;
using Microsoft.Extensions.Options;

namespace FamilyLibrarian.Infrastructure.Tests.Metadata;

/// <summary>
/// The optional budgeted seed/detail reads on the three existing adapters, driven through each real handler chain over a scripted
/// transport so every physical request is counted where it is dispatched: each attempt, retry and ancillary lookup is charged to
/// the journey budget, none is made outside it, and a stopped read is never an empty one. Every payload is hand-built from the
/// providers' public response shapes; none is a captured response and no network or credential is involved.
/// </summary>
[TestClass]
public sealed class BudgetedReadsTests
{
    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Throttled(int seconds)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
        return response;
    }

    private static DiscoveryOperationBudget Budget(DiscoveryOperation operation, TimeProvider clock, DiscoveryLimits? limits = null) =>
        new DiscoveryJourney(Guid.NewGuid(), limits ?? DiscoveryLimits.Default, clock).BeginOperation(operation, "provider", null)!;

    private sealed class Transport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class Credential(string? value) : IMetadataCredentialAccessor
    {
        public Task<string?> GetCredentialAsync(string providerId, CancellationToken cancellationToken) => Task.FromResult(value);
    }

    private sealed class Script
    {
        private readonly Queue<HttpResponseMessage> queue;
        private int requests;

        public Script(params HttpResponseMessage[] responses) => queue = new Queue<HttpResponseMessage>(responses);

        public List<string> Uris { get; } = [];

        public List<string> Bodies { get; } = [];

        public int Requests => Volatile.Read(ref requests);

        public async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requests);
            lock (Uris) Uris.Add(request.RequestUri!.PathAndQuery);
            if (request.Content is not null)
            {
                var body = await request.Content.ReadAsStringAsync(cancellationToken);
                lock (Bodies) Bodies.Add(body);
            }

            lock (queue) return queue.Count > 0 ? queue.Dequeue() : new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }
    }

    // =================================================================================================== Hardcover

    private sealed class HardcoverRig : IDisposable
    {
        private readonly HttpClient client;

        public HardcoverRig(Script script, string? token = "test-token")
        {
            State = new HardcoverRateState(Clock);
            var rateLimit = new HardcoverRateLimitHandler(
                Options.Create(new HardcoverMetadataOptions { MaxRetryAttempts = 3, MaxRetryDelaySeconds = 30 }),
                State,
                (wait, _) =>
                {
                    Clock.Advance(wait);
                    return Task.CompletedTask;
                })
            {
                InnerHandler = new Transport(script.HandleAsync)
            };
            client = new HttpClient(new HardcoverAuthorizationHandler(new Credential(token)) { InnerHandler = rateLimit })
            {
                BaseAddress = new Uri("https://api.hardcover.app/v1/graphql")
            };
            Provider = new HardcoverBookMetadataProvider(client, new HardcoverLanguageLookupCache(new Factory(client)), Clock);
        }

        public HardcoverBookMetadataProvider Provider { get; }

        public ManualClock Clock { get; } = new();

        public HardcoverRateState State { get; }

        public void Dispose() => client.Dispose();

        private sealed class Factory(HttpClient shared) : IHttpClientFactory
        {
            public HttpClient CreateClient(string name) => shared;
        }
    }

    private const string HardcoverSearchIds = """{"data":{"search":{"ids":[11]}}}""";

    // A record with a language id on its default edition: the ordinary mapping would look the language up in a third request.
    private const string HardcoverBooks = """
        {"data":{"books":[{"id":11,"title":"Fictional Tide","description":"A made-up book.","release_date":"2020-03-04","slug":"fictional-tide",
          "compilation":false,"is_partial_book":false,"cached_image":null,
          "contributions":[{"author":{"id":7,"name":"A. Writer"}}],
          "book_series":[{"position":2,"featured":true,"series":{"id":300,"name":"The Tide","is_completed":false}}],
          "default_physical_edition":{"language_id":1,"pages":300},"default_ebook_edition":null,
          "editions":[{"isbn_13":"9780306406157","isbn_10":null,"edition_format":"Hardcover","release_date":"2020-03-04"}]}]}}
        """;

    private const string HardcoverBook = """
        {"data":{"books_by_pk":{"id":11,"title":"Fictional Tide","description":"A made-up book.","release_date":"2020-03-04","slug":"fictional-tide",
          "compilation":false,"is_partial_book":false,"cached_image":null,
          "contributions":[{"author":{"id":7,"name":"A. Writer"}}],"book_series":[],
          "default_physical_edition":{"language_id":1,"pages":300},"default_ebook_edition":null,
          "editions":[{"isbn_13":"9780306406157","isbn_10":null,"edition_format":"Hardcover","release_date":"2020-03-04"}]}}}
        """;

    [TestMethod]
    public async Task HardcoverSearchSpendsBothItsRequestsAndNeverMakesTheUnbudgetedLanguageLookup()
    {
        var script = new Script(Json(HardcoverSearchIds), Json(HardcoverBooks));
        using var rig = new HardcoverRig(script);
        var budget = Budget(DiscoveryOperation.SearchBooks, rig.Clock);

        var read = await rig.Provider.SearchBudgetedAsync(new BookSearchQuery("tide"), budget, CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Ok, read.Outcome);
        Assert.AreEqual(2, script.Requests, "The ids query and the books query are the only requests: no language lookup.");
        Assert.AreEqual(2, budget.Usage.Attempts);
        Assert.AreEqual(1, budget.Usage.Pages);
        Assert.AreEqual(0, budget.Usage.Retries);
        var candidate = read.Value!.Candidates.Single();
        Assert.AreEqual("11", candidate.ExternalId);
        Assert.IsNull(candidate.Language, "The language stays unknown rather than costing an unbudgeted request.");
        Assert.AreEqual(ExternalReferenceEntityType.Work, candidate.SourceKind);
        Assert.IsFalse(script.Bodies.Any(body => body.Contains("languages", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task HardcoverRetryIsChargedAsARetryAndCountsAgainstTheAttemptAllowance()
    {
        var script = new Script(Throttled(1), Json(HardcoverSearchIds), Json(HardcoverBooks));
        using var rig = new HardcoverRig(script);
        var budget = Budget(DiscoveryOperation.SearchBooks, rig.Clock);

        var read = await rig.Provider.SearchBudgetedAsync(new BookSearchQuery("tide"), budget, CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Ok, read.Outcome);
        Assert.AreEqual(3, script.Requests);
        Assert.AreEqual(3, budget.Usage.Attempts, "The throttled attempt, its retry and the second request are all physical attempts.");
        Assert.AreEqual(1, budget.Usage.Retries);
    }

    [TestMethod]
    public async Task HardcoverStopsAtTheAttemptCeilingInsteadOfMakingAnUnbudgetedSecondRequest()
    {
        var script = new Script(Json(HardcoverSearchIds), Json(HardcoverBooks));
        using var rig = new HardcoverRig(script);
        var budget = Budget(DiscoveryOperation.SearchBooks, rig.Clock, DiscoveryLimits.Default with { OperationMaxAttempts = 1 });

        var read = await rig.Provider.SearchBudgetedAsync(new BookSearchQuery("tide"), budget, CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Failed, read.Outcome);
        Assert.IsNull(read.Value);
        CollectionAssert.Contains(read.Notes.ToArray(), DiscoveryCodes.AttemptCeiling);
        Assert.AreEqual(1, script.Requests, "The second request was refused by the budget before it was dispatched.");
        Assert.AreEqual(1, budget.Usage.Attempts);
    }

    [TestMethod]
    public async Task HardcoverDetailIsOneBudgetedRequestAndNoLanguageLookup()
    {
        var script = new Script(Json(HardcoverBook));
        using var rig = new HardcoverRig(script);
        var budget = Budget(DiscoveryOperation.GetBookDetails, rig.Clock);

        var read = await rig.Provider.GetDetailsBudgetedAsync("11", budget, CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Ok, read.Outcome);
        Assert.AreEqual(1, script.Requests);
        Assert.AreEqual(1, budget.Usage.Attempts);
        Assert.IsNull(read.Value!.Language);
    }

    [TestMethod]
    public async Task HardcoverFieldErrorsAndMissingCredentialsAndThrottlesAreNeverAnEmptyResult()
    {
        using var errored = new HardcoverRig(new Script(Json("""{"data":{"search":{"ids":[11]}},"errors":[{"message":"x","path":["search"]}]}""")));
        var erroredRead = await errored.Provider.SearchBudgetedAsync(new BookSearchQuery("tide"), Budget(DiscoveryOperation.SearchBooks, errored.Clock), CancellationToken.None);
        Assert.AreEqual(DiscoveryOutcome.Failed, erroredRead.Outcome);
        CollectionAssert.Contains(erroredRead.Notes.ToArray(), DiscoveryCodes.PartialFieldError);

        var noKeyScript = new Script(Json(HardcoverSearchIds));
        using var noKey = new HardcoverRig(noKeyScript, token: null);
        var noKeyRead = await noKey.Provider.SearchBudgetedAsync(new BookSearchQuery("tide"), Budget(DiscoveryOperation.SearchBooks, noKey.Clock), CancellationToken.None);
        Assert.AreEqual(DiscoveryOutcome.Unavailable, noKeyRead.Outcome);
        CollectionAssert.Contains(noKeyRead.Notes.ToArray(), DiscoveryCodes.SetupMissing);
        Assert.AreEqual(0, noKeyScript.Requests, "No credential, no request.");

        var longWait = new Script(Throttled(3600));
        using var throttled = new HardcoverRig(longWait);
        var throttledRead = await throttled.Provider.SearchBudgetedAsync(new BookSearchQuery("tide"), Budget(DiscoveryOperation.SearchBooks, throttled.Clock), CancellationToken.None);
        Assert.AreEqual(DiscoveryOutcome.Throttled, throttledRead.Outcome);
        Assert.AreEqual(1, longWait.Requests, "A wait that does not fit is reported, not waited out or retried early.");
    }

    [TestMethod]
    public async Task HardcoverAnEmptyIdListIsAVerifiedEmptyPageButAnInvalidIdMakesNoRequest()
    {
        var script = new Script(Json("""{"data":{"search":{"ids":[]}}}"""));
        using var rig = new HardcoverRig(script);
        var empty = await rig.Provider.SearchBudgetedAsync(new BookSearchQuery("nothing"), Budget(DiscoveryOperation.SearchBooks, rig.Clock), CancellationToken.None);
        Assert.AreEqual(DiscoveryOutcome.Empty, empty.Outcome);
        Assert.AreEqual(1, script.Requests);

        var invalid = await rig.Provider.GetDetailsBudgetedAsync("not-a-number", Budget(DiscoveryOperation.GetBookDetails, rig.Clock), CancellationToken.None);
        Assert.AreEqual(DiscoveryOutcome.Failed, invalid.Outcome);
        CollectionAssert.Contains(invalid.Notes.ToArray(), DiscoveryCodes.SubjectInvalid);
        Assert.AreEqual(1, script.Requests);
    }

    [TestMethod]
    public async Task HardcoverCallerCancellationIsNotConvertedIntoAResult()
    {
        var script = new Script(Json(HardcoverSearchIds));
        using var rig = new HardcoverRig(script);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            rig.Provider.SearchBudgetedAsync(new BookSearchQuery("tide"), Budget(DiscoveryOperation.SearchBooks, rig.Clock), cancelled.Token));
    }

    // ================================================================================================ Open Library

    private sealed class OpenLibraryRig : IDisposable
    {
        private readonly HttpClient client;
        private readonly OpenLibraryRequestGate gate;

        public OpenLibraryRig(Script script)
        {
            var options = Options.Create(new OpenLibraryMetadataOptions { Enabled = true, MaxResults = 10, TimeoutSeconds = 15, RequestsPerSecond = 1000 });
            gate = new OpenLibraryRequestGate(options, Clock);
            client = new HttpClient(new OpenLibraryRateLimitHandler(gate) { InnerHandler = new Transport(script.HandleAsync) })
            {
                BaseAddress = new Uri("https://openlibrary.org/")
            };
            Provider = new OpenLibraryBookMetadataProvider(client, options, Clock);
        }

        public OpenLibraryBookMetadataProvider Provider { get; }

        public ManualClock Clock { get; } = new();

        public void Dispose()
        {
            client.Dispose();
            gate.Dispose();
        }
    }

    private const string OpenLibraryDocs = """
        {"num_found":1,"docs":[{"key":"/works/OL11W","title":"Fictional Tide","author_name":["A. Writer"],"author_key":["OL7A"],"first_publish_year":2020}]}
        """;

    [TestMethod]
    public async Task OpenLibrarySearchIsOneBudgetedRequestWithTheOrdinarySearchShape()
    {
        var script = new Script(Json(OpenLibraryDocs));
        using var rig = new OpenLibraryRig(script);
        var budget = Budget(DiscoveryOperation.SearchBooks, rig.Clock);

        var read = await rig.Provider.SearchBudgetedAsync(new BookSearchQuery("tide"), budget, CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Ok, read.Outcome);
        Assert.AreEqual(1, script.Requests);
        Assert.AreEqual(1, budget.Usage.Attempts);
        Assert.AreEqual(1, budget.Usage.Pages);
        StringAssert.Contains(script.Uris.Single(), "search.json?q=tide");
        StringAssert.Contains(script.Uris.Single(), "lang=en", "The same question the ordinary search asks.");
        Assert.AreEqual("OL11W", read.Value!.Candidates.Single().ExternalId);
    }

    [TestMethod]
    public async Task OpenLibraryDetailMakesNoEditionListRequestAndSaysItDidNot()
    {
        var script = new Script(Json(OpenLibraryDocs));
        using var rig = new OpenLibraryRig(script);
        var budget = Budget(DiscoveryOperation.GetBookDetails, rig.Clock);

        var read = await rig.Provider.GetDetailsBudgetedAsync("OL11W", budget, CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Ok, read.Outcome);
        Assert.AreEqual(1, script.Requests, "The ordinary detail view makes a second, editions.json request; the budgeted one does not.");
        Assert.IsFalse(script.Uris.Any(uri => uri.Contains("editions.json", StringComparison.Ordinal)));
        CollectionAssert.Contains(read.Notes.ToArray(), "edition-list-not-read");
    }

    [TestMethod]
    public async Task OpenLibraryThrottleIsRememberedSoALaterReadMakesNoRequestAndNeitherIsEmpty()
    {
        var script = new Script(Throttled(120), Json(OpenLibraryDocs));
        using var rig = new OpenLibraryRig(script);

        var first = await rig.Provider.SearchBudgetedAsync(new BookSearchQuery("tide"), Budget(DiscoveryOperation.SearchBooks, rig.Clock), CancellationToken.None);
        var second = await rig.Provider.SearchBudgetedAsync(new BookSearchQuery("tide"), Budget(DiscoveryOperation.SearchBooks, rig.Clock), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Throttled, first.Outcome);
        Assert.AreEqual(DiscoveryOutcome.Throttled, second.Outcome);
        Assert.AreEqual(1, script.Requests, "The shared cooldown applies to budgeted reads too: the second read never reaches the network.");
    }

    [TestMethod]
    public async Task OpenLibraryAnExhaustedDeadlineIsATimeoutNotAnEmptyResultAndMakesNoRequest()
    {
        var script = new Script(Json(OpenLibraryDocs));
        using var rig = new OpenLibraryRig(script);
        var budget = Budget(DiscoveryOperation.SearchBooks, rig.Clock, DiscoveryLimits.Default with { OperationMaxWork = TimeSpan.Zero });

        var read = await rig.Provider.SearchBudgetedAsync(new BookSearchQuery("tide"), budget, CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.TimedOut, read.Outcome);
        Assert.AreEqual(0, script.Requests);
        CollectionAssert.Contains(read.Notes.ToArray(), DiscoveryCodes.DeadlineReached);
    }

    [TestMethod]
    public async Task OpenLibraryAnUnknownWorkIsAVerifiedEmptyDetailAndAMalformedIdMakesNoRequest()
    {
        var script = new Script(Json("""{"num_found":0,"docs":[]}"""));
        using var rig = new OpenLibraryRig(script);

        var missing = await rig.Provider.GetDetailsBudgetedAsync("OL99W", Budget(DiscoveryOperation.GetBookDetails, rig.Clock), CancellationToken.None);
        var malformed = await rig.Provider.GetDetailsBudgetedAsync("../etc", Budget(DiscoveryOperation.GetBookDetails, rig.Clock), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Empty, missing.Outcome);
        Assert.AreEqual(DiscoveryOutcome.Failed, malformed.Outcome);
        Assert.AreEqual(1, script.Requests);
    }

    // =================================================================================================== Google Books

    private sealed class GoogleRig : IDisposable
    {
        private readonly HttpClient client;

        public GoogleRig(Script script, string? apiKey = "test-key")
        {
            var retry = new GoogleBooksTransientRetryHandler([TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(10)])
            {
                InnerHandler = new Transport(script.HandleAsync)
            };
            client = new HttpClient(new GoogleBooksApiKeyHandler(new Credential(apiKey)) { InnerHandler = retry })
            {
                BaseAddress = new Uri("https://www.googleapis.com/books/v1/")
            };
            Provider = new GoogleBooksBookMetadataProvider(
                client, Options.Create(new GoogleBooksMetadataOptions { Enabled = true, ApiKey = "test-key", MaxResults = 10, TimeoutSeconds = 15 }), Clock);
        }

        public GoogleBooksBookMetadataProvider Provider { get; }

        public ManualClock Clock { get; } = new();

        public void Dispose() => client.Dispose();
    }

    private const string GoogleVolumes = """
        {"totalItems":1,"items":[{"id":"vol11","volumeInfo":{"title":"Fictional Tide","authors":["A. Writer"],"publishedDate":"2020","description":"A made-up book."}}]}
        """;

    [TestMethod]
    public async Task GoogleSearchIsOneBudgetedRequestAndTheKeyNeverEntersTheResult()
    {
        var script = new Script(Json(GoogleVolumes));
        using var rig = new GoogleRig(script);
        var budget = Budget(DiscoveryOperation.SearchBooks, rig.Clock);

        var read = await rig.Provider.SearchBudgetedAsync(new BookSearchQuery("tide"), budget, CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Ok, read.Outcome);
        Assert.AreEqual(1, script.Requests);
        Assert.AreEqual(1, budget.Usage.Attempts);
        StringAssert.Contains(script.Uris.Single(), "printType=books");
        Assert.AreEqual(ExternalReferenceEntityType.Edition, read.Value!.Candidates.Single().SourceKind, "A Google volume is an edition.");
        Assert.AreEqual(DateBasis.EditionDerived, read.Value.Candidates.Single().PublicationDateBasis);
        Assert.IsFalse(read.Notes.Any(note => note.Contains("test-key", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task GoogleTransientFailureUsesTheBudgetsSingleRetryNotTheHandlersTwoRetryPolicy()
    {
        var withRetry = new Script(Json("{}", HttpStatusCode.ServiceUnavailable), Json(GoogleVolumes));
        using var allowed = new GoogleRig(withRetry);
        var allowedBudget = Budget(DiscoveryOperation.SearchBooks, allowed.Clock);
        var recovered = await allowed.Provider.SearchBudgetedAsync(new BookSearchQuery("tide"), allowedBudget, CancellationToken.None);
        Assert.AreEqual(DiscoveryOutcome.Ok, recovered.Outcome);
        Assert.AreEqual(2, withRetry.Requests);
        Assert.AreEqual(1, allowedBudget.Usage.Retries);

        // With the retry allowance spent the read stops after one physical request; the handler's own two retries do not apply.
        var denied = new Script(Json("{}", HttpStatusCode.ServiceUnavailable), Json(GoogleVolumes), Json(GoogleVolumes));
        using var strict = new GoogleRig(denied);
        var strictBudget = Budget(DiscoveryOperation.SearchBooks, strict.Clock, DiscoveryLimits.Default with { OperationMaxRetries = 0 });
        var stopped = await strict.Provider.SearchBudgetedAsync(new BookSearchQuery("tide"), strictBudget, CancellationToken.None);
        Assert.AreEqual(DiscoveryOutcome.Failed, stopped.Outcome);
        Assert.AreEqual(1, denied.Requests);
        Assert.AreEqual(1, strictBudget.Usage.Attempts);
    }

    [TestMethod]
    public async Task GoogleDetailDistinguishesAVerifiedMissingVolumeFromAFailureAndAThrottle()
    {
        using var missing = new GoogleRig(new Script(Json("{}", HttpStatusCode.NotFound)));
        Assert.AreEqual(DiscoveryOutcome.Empty, (await missing.Provider.GetDetailsBudgetedAsync("vol404", Budget(DiscoveryOperation.GetBookDetails, missing.Clock), CancellationToken.None)).Outcome);

        using var failing = new GoogleRig(new Script(Json("{}", HttpStatusCode.BadRequest)));
        Assert.AreEqual(DiscoveryOutcome.Failed, (await failing.Provider.GetDetailsBudgetedAsync("vol400", Budget(DiscoveryOperation.GetBookDetails, failing.Clock), CancellationToken.None)).Outcome);

        var throttleScript = new Script(Throttled(30), Json(GoogleVolumes));
        using var throttled = new GoogleRig(throttleScript);
        Assert.AreEqual(DiscoveryOutcome.Throttled, (await throttled.Provider.GetDetailsBudgetedAsync("vol429", Budget(DiscoveryOperation.GetBookDetails, throttled.Clock), CancellationToken.None)).Outcome);
        Assert.AreEqual(1, throttleScript.Requests, "A 429 is reported and never retried.");

        var invalid = new Script(Json(GoogleVolumes));
        using var guarded = new GoogleRig(invalid);
        var rejected = await guarded.Provider.GetDetailsBudgetedAsync("../bad id", Budget(DiscoveryOperation.GetBookDetails, guarded.Clock), CancellationToken.None);
        Assert.AreEqual(DiscoveryOutcome.Failed, rejected.Outcome);
        Assert.AreEqual(0, invalid.Requests);
    }

    [TestMethod]
    public async Task GoogleWithoutAKeyIsUnavailableAndMakesNoRequest()
    {
        var script = new Script(Json(GoogleVolumes));
        using var rig = new GoogleRig(script, apiKey: null);

        var read = await rig.Provider.SearchBudgetedAsync(new BookSearchQuery("tide"), Budget(DiscoveryOperation.SearchBooks, rig.Clock), CancellationToken.None);

        Assert.AreEqual(DiscoveryOutcome.Unavailable, read.Outcome);
        Assert.AreEqual(0, script.Requests);
    }

    // ==================================================================================== the shared contract

    [TestMethod]
    public void ARegisteredAdapterIsTheSameInstanceForOrdinaryDiscoveryAndBudgetedReads()
    {
        var script = new Script();
        using var hardcover = new HardcoverRig(script);
        using var openLibrary = new OpenLibraryRig(script);
        using var google = new GoogleRig(script);

        foreach (var provider in new IBookMetadataProvider[] { hardcover.Provider, openLibrary.Provider, google.Provider })
        {
            Assert.IsInstanceOfType<IBudgetedBookMetadataProvider>(provider);
            Assert.IsInstanceOfType<IBookMetadataDiscoveryProvider>(provider);
            Assert.AreEqual(provider.Id, ((IBudgetedBookMetadataProvider)provider).ProviderId);
        }
    }

    [TestMethod]
    public void SeedAndDetailReadsAreNotDiscoveryCapabilitiesAndStayOutOfTheCapabilityTables()
    {
        Assert.IsFalse(DiscoveryOperation.SearchBooks.IsDiscoveryCapability());
        Assert.IsFalse(DiscoveryOperation.GetBookDetails.IsDiscoveryCapability());
        Assert.IsTrue(DiscoveryOperation.GetAuthorSeries.IsDiscoveryCapability());
        using var hardcover = new HardcoverRig(new Script());
        Assert.AreEqual(CapabilityState.Unverified, ((IBookMetadataDiscoveryProvider)hardcover.Provider).Capabilities.Get(DiscoveryOperation.SearchBooks).State);
    }
}
