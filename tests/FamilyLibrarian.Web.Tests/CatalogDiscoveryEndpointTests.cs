// One-shot expected-value arrays in assertions are clearer inline than as static fields; CA1861 targets repeated calls.
#pragma warning disable CA1861
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Catalog.Discovery;
using FamilyLibrarian.Contracts.Authentication;
using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Domain.Catalog;
using FamilyLibrarian.Web.Catalog;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// The explicit-lookup routes over the real host and a migrated PostgreSQL database: authentication, anti-forgery, owner
/// isolation, tampering, the per-user rate rule over HTTP, the proof that every browse GET adds zero provider calls, and the
/// proof that lookups change no catalog, request, follow, notification, evidence or audit row. The provider is scripted and
/// counts every discovery call; none of its data is a provider response.
/// </summary>
[TestClass]
public sealed class CatalogDiscoveryEndpointTests
{
    private const string Discovery = "/api/v1/catalog/discovery";
    private const string Browse = "/api/v1/catalog/browse";
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static WebTestFixture? fixture;

    [ClassInitialize]
    public static async Task InitializeAsync(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        fixture = await WebTestFixture.CreateAsync();
    }

    [ClassCleanup]
    public static async Task CleanupAsync()
    {
        if (fixture is not null) await fixture.DisposeAsync();
    }

    private sealed class PinnedClock(DateTimeOffset start) : IClock
    {
        private long ticks = start.UtcTicks;

        public DateTimeOffset UtcNow => new(Interlocked.Read(ref ticks), TimeSpan.Zero);

        public void Advance(TimeSpan by) => Interlocked.Add(ref ticks, by.Ticks);
    }

    private static BookCandidate Candidate(string id = "b1") =>
        new("demo", "Demo Source", id, "Book " + id, ["Frank Herbert", "Unanchored Person"], "Not cached.", null, null, [],
            [new BookSeriesCandidate("Saga", "1", true) { SourceSeriesId = "S1" }])
        {
            AuthorCredits = [new BookAuthorCredit("Frank Herbert", null, "A1")],
            SourceKind = ExternalReferenceEntityType.Work
        };

    private static FamilyLibrarianAppFactory Factory(ScriptedDiscoveryProvider provider, IClock? clock = null, bool roomyContexts = false) =>
        new(WebTestFixture.Require(fixture).ConnectionString, services =>
        {
            services.RemoveAll<IBookMetadataProvider>();
            services.AddSingleton<IBookMetadataProvider>(provider);
            if (clock is not null)
            {
                services.RemoveAll<IClock>();
                services.AddSingleton(clock);
            }

            if (roomyContexts)
            {
                // The default ten-context bound would evict the search sample after nine windows; this rule is tested elsewhere.
                services.RemoveAll<CatalogBrowseContextStore>();
                services.AddSingleton(new CatalogBrowseContextStore(new CatalogBrowseContextLimits { MaxContextsPerOwner = 100, MaxContextsTotal = 400 }));
            }
        });

    private static async Task<HttpClient> SignedInAsync(
        FamilyLibrarianAppFactory factory, string email = WebTestFixture.UserEmail, string password = WebTestFixture.UserPassword, bool withToken = true)
    {
        var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = email, Password = password });
        Assert.AreEqual(HttpStatusCode.NoContent, login.StatusCode);
        if (withToken) client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(client));
        return client;
    }

    private static async Task<T> GetAsync<T>(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, $"{url}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(WebJson))!;
    }

    private static Task<HttpResponseMessage> Expand(HttpClient client, Guid context, Guid subject, string kind) =>
        client.PostAsJsonAsync($"{Discovery}/expansions", new CatalogExpansionRequest(context, subject, kind));

    private static async Task<CatalogBookNavigationResponse> NavigateAsync(HttpClient client, string id = "b1") =>
        (await GetAsync<CatalogBookCandidateResponse>(client, $"/api/v1/catalog/candidates/demo/{id}")).Navigation!;

    private static readonly string[] SnapshotTables =
    [
        "catalog.authors", "catalog.works", "catalog.work_authors", "catalog.editions", "catalog.series",
        "catalog.series_entries", "catalog.external_references", "requests.book_requests", "requests.request_participants",
        "requests.request_formats", "requests.request_status_history", "following.follows", "notifications.notification_events",
        "notifications.notification_receipts", "communications.outbound_communications",
        "communications.outbound_communication_deliveries", "audit.audit_events"
    ];

    private static async Task<Dictionary<string, string>> SnapshotAsync()
    {
        await using var connection = new NpgsqlConnection(WebTestFixture.Require(fixture).ConnectionString);
        await connection.OpenAsync();
        var result = new Dictionary<string, string>();
        foreach (var table in SnapshotTables)
        {
            await using var command = new NpgsqlCommand(
                $"SELECT count(*)::text || ':' || coalesce(md5(string_agg(t::text, '|' ORDER BY t::text)), '') FROM {table} t", connection);
            result[table] = (string)(await command.ExecuteScalarAsync())!;
        }

        return result;
    }

    // ================================================================ authentication and anti-forgery

    [TestMethod]
    public async Task EveryRouteRequiresAuthenticationAndAPostRequiresTheAntiforgeryToken()
    {
        var provider = new ScriptedDiscoveryProvider(books: [Candidate()]);
        await using var factory = Factory(provider);
        using var anonymous = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var noToken = await SignedInAsync(factory, withToken: false);
        var id = Guid.NewGuid();

        foreach (var url in new[] { $"{Discovery}/windows/{id}", $"{Discovery}/contexts/{id}/subjects/{id}/offers" })
        {
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(url)).StatusCode, url);
        }

        Assert.AreEqual(HttpStatusCode.Unauthorized, (await Expand(anonymous, id, id, "BookSeries")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync($"{Discovery}/expansions/continue", new CatalogExpansionContinueRequest(id))).StatusCode);

        var forged = await Expand(noToken, id, id, "BookSeries");
        var forgedContinue = await noToken.PostAsJsonAsync($"{Discovery}/expansions/continue", new CatalogExpansionContinueRequest(id));
        Assert.AreEqual(HttpStatusCode.BadRequest, forged.StatusCode, "A cookie-authenticated POST without the token is refused.");
        Assert.AreEqual(HttpStatusCode.BadRequest, forgedContinue.StatusCode);
        Assert.AreEqual(0, provider.DiscoveryCalls);
    }

    // ================================================================ no provider call from any read

    [TestMethod]
    public async Task EveryBrowseAndDiscoveryGetAddsZeroDiscoveryCalls()
    {
        var provider = new ScriptedDiscoveryProvider(books: [Candidate()]);
        await using var factory = Factory(provider);
        using var client = await SignedInAsync(factory);

        var navigation = await NavigateAsync(client);
        Assert.IsNotNull(navigation.BookSubjectId, "The detail response names the book for a lookup.");
        var context = navigation.ContextId!.Value;
        await GetAsync<CatalogObservedAuthorBrowseResponse>(client, $"{Browse}/contexts/{context}/authors/{navigation.Authors[0].SubjectId}");
        await GetAsync<CatalogObservedSeriesBrowseResponse>(client, $"{Browse}/contexts/{context}/series/{navigation.Series[0].SubjectId}");
        await GetAsync<CatalogObservedBookResponse>(client, $"{Browse}/contexts/{context}/books/demo?externalId=b1");
        var offers = await GetAsync<CatalogExpansionOffersResponse>(client, $"{Discovery}/contexts/{context}/subjects/{navigation.BookSubjectId}/offers");
        Assert.AreEqual("BookSeries", offers.Offers.Single().Kind);
        Assert.AreEqual("Supported", offers.Offers.Single().Capability);
        Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync($"{Discovery}/windows/{context}")).StatusCode, "A search context is not a lookup window.");

        Assert.AreEqual(0, provider.DiscoveryCalls, "No GET contacts a provider for discovery.");
        Assert.AreEqual(1, provider.Details, "Only the detail the user asked for reached the provider.");
    }

    // ================================================================ the happy path, no writes, and the shared allowance

    [TestMethod]
    public async Task LookupsChainFromABookThroughASeriesToItsMembersAndChangeNoCatalogRequestFollowOrEvidenceRow()
    {
        var provider = new ScriptedDiscoveryProvider(books: [Candidate()]);
        await using var factory = Factory(provider);
        using var client = await SignedInAsync(factory);
        var navigation = await NavigateAsync(client);
        var before = await SnapshotAsync();

        var claims = await Expand(client, navigation.ContextId!.Value, navigation.BookSubjectId!.Value, "BookSeries");
        Assert.AreEqual(HttpStatusCode.OK, claims.StatusCode, await claims.Content.ReadAsStringAsync());
        var bookWindow = (await claims.Content.ReadFromJsonAsync<CatalogDiscoveryResponse>(WebJson))!;
        Assert.AreEqual("Ok", bookWindow.Outcome);
        Assert.AreEqual("FindSeriesForBook", bookWindow.Operation);
        Assert.AreEqual("Supported", bookWindow.Capability);
        Assert.AreEqual(navigation.ContextId, bookWindow.ParentContextId);
        var memberships = bookWindow.Books.Single().Memberships;
        Assert.AreEqual("Saga", memberships[0].Label);
        Assert.IsTrue(memberships[1].NameMissing, "The unnamed series stays unnamed on the wire.");
        Assert.IsTrue(memberships.All(membership => membership.HasSourceId));
        StringAssert.DoesNotMatch(await claims.Content.ReadAsStringAsync(), new System.Text.RegularExpressions.Regex("\"S1\"|\"S2\"|nativeSeriesId"),
            "The client never sees a native provider id.");

        var read = await GetAsync<CatalogDiscoveryResponse>(client, $"{Discovery}/windows/{bookWindow.ContextId}");
        Assert.AreEqual(bookWindow.ContextId, read.ContextId);
        Assert.AreEqual(1, provider.Calls(DiscoveryOperation.FindSeriesForBook), "Reading the window is provider-free.");

        var membersResponse = await Expand(client, bookWindow.ContextId!.Value, memberships[0].SubjectId, "SeriesMembers");
        var firstPage = (await membersResponse.Content.ReadFromJsonAsync<CatalogDiscoveryResponse>(WebJson))!;
        Assert.AreEqual("MoreAvailable", firstPage.Completion);
        Assert.IsTrue(firstPage.CanContinue);
        Assert.HasCount(2, firstPage.Books);
        var next = await client.PostAsJsonAsync($"{Discovery}/expansions/continue", new CatalogExpansionContinueRequest(firstPage.ContextId!.Value));
        var secondPage = (await next.Content.ReadFromJsonAsync<CatalogDiscoveryResponse>(WebJson))!;
        Assert.AreEqual("ExhaustedInCheckedScope", secondPage.Completion);
        Assert.IsFalse(secondPage.CanContinue);
        Assert.AreEqual(2, secondPage.Pages, "Pages accumulate on one operation.");
        Assert.AreEqual(firstPage.ParentContextId, secondPage.ParentContextId);

        var after = await SnapshotAsync();
        foreach (var table in SnapshotTables) Assert.AreEqual(before[table], after[table], $"{table} changed.");
        Assert.AreEqual(3, provider.DiscoveryCalls);
    }

    // ================================================================ tampering, foreign owners, expiry

    [TestMethod]
    public async Task ForeignTamperedAndMisScopedRequestsAreRefusedWithNoProviderCall()
    {
        var provider = new ScriptedDiscoveryProvider(books: [Candidate()]);
        await using var factory = Factory(provider);
        using var reader = await SignedInAsync(factory);
        using var admin = await SignedInAsync(factory, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword);
        var navigation = await NavigateAsync(reader);
        var window = (await (await Expand(reader, navigation.ContextId!.Value, navigation.BookSubjectId!.Value, "BookSeries")).Content
            .ReadFromJsonAsync<CatalogDiscoveryResponse>(WebJson))!;
        var calls = provider.DiscoveryCalls;

        var foreign = await Expand(admin, navigation.ContextId.Value, navigation.BookSubjectId.Value, "BookSeries");
        var foreignWindow = await admin.GetAsync($"{Discovery}/windows/{window.ContextId}");
        var foreignContinue = await admin.PostAsJsonAsync($"{Discovery}/expansions/continue", new CatalogExpansionContinueRequest(window.ContextId!.Value));
        var foreignOffers = await admin.GetAsync($"{Discovery}/contexts/{navigation.ContextId}/subjects/{navigation.BookSubjectId}/offers");
        var unknown = await Expand(reader, Guid.NewGuid(), Guid.NewGuid(), "BookSeries");
        var wrongScope = await Expand(reader, navigation.ContextId.Value, navigation.Series[0].SubjectId, "AuthorWorks");
        var badKind = await Expand(reader, navigation.ContextId.Value, navigation.BookSubjectId.Value, "DeleteEverything");
        var emptyIds = await Expand(reader, Guid.Empty, Guid.Empty, "BookSeries");
        var noToken = await reader.PostAsJsonAsync($"{Discovery}/expansions/continue", new CatalogExpansionContinueRequest(window.ContextId.Value));

        Assert.AreEqual(HttpStatusCode.NotFound, foreign.StatusCode, "Another user's subject is indistinguishable from an unknown one.");
        Assert.AreEqual(HttpStatusCode.NotFound, foreignWindow.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, foreignContinue.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, foreignOffers.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, wrongScope.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, badKind.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, emptyIds.StatusCode);
        Assert.AreEqual(HttpStatusCode.Conflict, noToken.StatusCode, "A window with nothing to continue says so rather than inventing a page.");
        Assert.AreEqual(calls, provider.DiscoveryCalls);
    }

    [TestMethod]
    public async Task AnExpiredContextAnswersNotFoundWithoutAProviderCallAndAWindowNeverRefreshesIt()
    {
        var provider = new ScriptedDiscoveryProvider(books: [Candidate()]);
        var clock = new PinnedClock(new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero));
        await using var factory = Factory(provider, clock);
        using var client = await SignedInAsync(factory);
        var navigation = await NavigateAsync(client);
        clock.Advance(TimeSpan.FromMinutes(9));
        var window = (await (await Expand(client, navigation.ContextId!.Value, navigation.BookSubjectId!.Value, "BookSeries")).Content
            .ReadFromJsonAsync<CatalogDiscoveryResponse>(WebJson))!;
        Assert.AreEqual(navigation.ExpiresAtUtc, window.ExpiresAtUtc, "The window expires with the context it came from.");

        clock.Advance(TimeSpan.FromMinutes(2));
        var calls = provider.DiscoveryCalls;
        var late = await Expand(client, navigation.ContextId.Value, navigation.BookSubjectId.Value, "BookSeries");
        var lateWindow = await client.GetAsync($"{Discovery}/windows/{window.ContextId}");

        Assert.AreEqual(HttpStatusCode.NotFound, late.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, lateWindow.StatusCode);
        Assert.AreEqual(calls, provider.DiscoveryCalls);
    }

    // ================================================================ the per-user rule over HTTP

    [TestMethod]
    public async Task TheEleventhStartInAMinuteIs429WithRetryAfterAndNoProviderCall()
    {
        var provider = new ScriptedDiscoveryProvider(books: [Candidate()]);
        var clock = new PinnedClock(new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero));
        await using var factory = Factory(provider, clock, roomyContexts: true);
        using var client = await SignedInAsync(factory);
        var navigation = await NavigateAsync(client);

        for (var i = 0; i < 10; i++)
        {
            var ok = await Expand(client, navigation.ContextId!.Value, navigation.BookSubjectId!.Value, "BookSeries");
            Assert.AreEqual(HttpStatusCode.OK, ok.StatusCode, $"start {i + 1}: {await ok.Content.ReadAsStringAsync()}");
        }

        var refused = await Expand(client, navigation.ContextId!.Value, navigation.BookSubjectId!.Value, "BookSeries");
        var body = (await refused.Content.ReadFromJsonAsync<CatalogExpansionProblemResponse>(WebJson))!;

        Assert.AreEqual(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.AreEqual("rate-limited", body.Code);
        Assert.IsTrue(refused.Headers.RetryAfter?.Delta is { } wait && wait > TimeSpan.Zero, "The refusal says how long to wait.");
        Assert.AreEqual(10, provider.Calls(DiscoveryOperation.FindSeriesForBook));
    }

    [TestMethod]
    public async Task ASecondLookupWhileOneRunsIs429NotQueuedAndTheFirstStillCompletes()
    {
        var provider = new ScriptedDiscoveryProvider(books: [Candidate()]) { Gate = new SemaphoreSlim(0) };
        await using var factory = Factory(provider);
        using var client = await SignedInAsync(factory);
        var navigation = await NavigateAsync(client);

        var first = Expand(client, navigation.ContextId!.Value, navigation.BookSubjectId!.Value, "BookSeries");
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var second = await Expand(client, navigation.ContextId.Value, navigation.BookSubjectId.Value, "BookSeries");
        var code = (await second.Content.ReadFromJsonAsync<CatalogExpansionProblemResponse>(WebJson))!.Code;
        provider.Gate.Release();
        var completed = await first;

        Assert.AreEqual(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.AreEqual("lookup-running", code);
        Assert.AreEqual(HttpStatusCode.OK, completed.StatusCode);
        Assert.AreEqual(1, provider.Calls(DiscoveryOperation.FindSeriesForBook), "The refused request never reached the provider.");
    }

    // ================================================================ stated contributor roles reach the client

    [TestMethod]
    public async Task ASolePainterCreditTravelsToTheClientWithItsRoleAndAnAuthorlessCreditNeverBecomesAnAuthor()
    {
        var provider = new ScriptedDiscoveryProvider(books: [Candidate()])
        {
            WorksScript = (author, budget) => DiscoveryResult.Found<AuthorWork>(
                [new AuthorWork(ScriptedDiscoveryProvider.Book("w1", "Painted Only", new CreditRef(0, "Pat Painter", "illustrator", null)), [])],
                new DiscoveryScope(DiscoveryOperation.GetAuthorWorks, author.Id), DiscoveryCompletion.ExhaustedInCheckedScope,
                DateTimeOffset.UnixEpoch, budget.Usage)
        };
        await using var factory = Factory(provider);
        using var client = await SignedInAsync(factory);
        var navigation = await NavigateAsync(client);

        var response = await Expand(client, navigation.ContextId!.Value, navigation.Authors[0].SubjectId, "AuthorWorks");
        var window = (await response.Content.ReadFromJsonAsync<CatalogDiscoveryResponse>(WebJson))!;

        var credit = window.Books.Single().Credits.Single();
        Assert.AreEqual("Pat Painter", credit.Label);
        Assert.AreEqual("illustrator", credit.Role);
        var reread = await GetAsync<CatalogDiscoveryResponse>(client, $"{Discovery}/windows/{window.ContextId}");
        Assert.AreEqual("illustrator", reread.Books.Single().Credits.Single().Role, "A reopened window keeps it too.");
    }

    [TestMethod]
    public async Task ADroppedOversizedRoleReachesTheClientAsLostAndTheActualCreditLineNeverSaysByForIt()
    {
        var provider = new ScriptedDiscoveryProvider(books: [Candidate()])
        {
            WorksScript = (author, budget) => DiscoveryResult.Found<AuthorWork>(
                [new AuthorWork(ScriptedDiscoveryProvider.Book(
                    "w1", "Painted Only", new CreditRef(0, "Pat Painter", "illustrator-" + new string('x', 64), new SourceRef("demo", "A9", ExternalReferenceEntityType.Author))), [])],
                new DiscoveryScope(DiscoveryOperation.GetAuthorWorks, author.Id), DiscoveryCompletion.ExhaustedInCheckedScope,
                DateTimeOffset.UnixEpoch, budget.Usage)
        };
        await using var factory = Factory(provider);
        using var client = await SignedInAsync(factory);
        var navigation = await NavigateAsync(client);

        var response = await Expand(client, navigation.ContextId!.Value, navigation.Authors[0].SubjectId, "AuthorWorks");
        var window = (await response.Content.ReadFromJsonAsync<CatalogDiscoveryResponse>(WebJson))!;

        var book = window.Books.Single();
        Assert.IsNull(book.Credits.Single().Role);
        Assert.IsTrue(book.Credits.Single().RoleNotRetained);
        Assert.IsTrue(book.AssociationsTruncated, "The record-level flag stays.");
        var line = FamilyLibrarian.Web.Client.Catalog.CatalogDiscoveryCopy.CreditLine(book.Credits);
        Assert.AreEqual("Credited: Pat Painter (role not retained)", line);
        Assert.DoesNotContain("By ", line);
    }

    // ================================================================ source and capability states over HTTP

    [TestMethod]
    public async Task AnUnsupportedLookupIsNotActionableAndMakesNoProviderCall()
    {
        var provider = new ScriptedDiscoveryProvider(books: [Candidate()])
        {
            Capabilities = new DiscoveryCapabilities(
            [
                new(DiscoveryOperation.FindSeriesForBook, DiscoveryCapability.Unverified("live-probe-not-run")),
                new(DiscoveryOperation.GetSeriesMembers, DiscoveryCapability.Unsupported("series-endpoints-not-key-accessible"))
            ])
        };
        await using var factory = Factory(provider);
        using var client = await SignedInAsync(factory);
        var navigation = await NavigateAsync(client);
        var context = navigation.ContextId!.Value;

        var book = (await GetAsync<CatalogExpansionOffersResponse>(client, $"{Discovery}/contexts/{context}/subjects/{navigation.BookSubjectId}/offers")).Offers.Single();
        var series = (await GetAsync<CatalogExpansionOffersResponse>(client, $"{Discovery}/contexts/{context}/subjects/{navigation.Series[0].SubjectId}/offers")).Offers.Single();
        var refused = await Expand(client, context, navigation.Series[0].SubjectId, "SeriesMembers");

        Assert.AreEqual("Unverified", book.Capability);
        Assert.IsTrue(book.CanStart);
        Assert.AreEqual("Unsupported", series.Capability);
        Assert.IsFalse(series.CanStart);
        Assert.AreEqual(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.AreEqual("series-endpoints-not-key-accessible", (await refused.Content.ReadFromJsonAsync<CatalogExpansionProblemResponse>(WebJson))!.Code);
        Assert.AreEqual(0, provider.DiscoveryCalls);

        var ok = await Expand(client, context, navigation.BookSubjectId!.Value, "BookSeries");
        var window = (await ok.Content.ReadFromJsonAsync<CatalogDiscoveryResponse>(WebJson))!;
        Assert.AreEqual("Unverified", window.Capability, "A successful call does not promote the static capability.");
        Assert.AreEqual("Ok", window.Outcome);
    }

    [TestMethod]
    public async Task AnUnregisteredSourceIdInAnObservedContextCannotBeReached()
    {
        // The scripted provider answers as "demo"; a candidate observed from a source that is not installed has no lookup.
        var provider = new ScriptedDiscoveryProvider(books: [Candidate()]);
        await using var factory = Factory(provider);
        using var client = await SignedInAsync(factory);
        var navigation = await NavigateAsync(client);
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<CatalogBrowseContextStore>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
        var owner = Guid.NewGuid();
        var lease = store.Observe(owner, "x", [new BrowseObservation("removed-source", "Removed", true, [Candidate() with { ProviderId = "removed-source", ProviderName = "Removed" }])], clock.UtcNow)!;
        var record = store.TryGetRecord(owner, lease.ContextId, "removed-source", "b1", clock.UtcNow)!.Record;
        var service = scope.ServiceProvider.GetRequiredService<CatalogDiscoveryService>();

        var result = await service.ExpandAsync(owner, lease.ContextId, record.BookSubjectId!.Value, ExpansionKind.BookSeries, CancellationToken.None);

        Assert.AreEqual(ExpansionStatus.SourceUnavailable, result.Status);
        Assert.AreEqual("source-removed", result.Code);
        Assert.AreEqual(0, provider.DiscoveryCalls);
        Assert.IsNotNull(navigation);
    }
}
