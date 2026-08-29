using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Contracts.Authentication;
using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Contracts.Requests;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// The request workflow against the real host and a real PostgreSQL database.
/// </summary>
/// <remarks>
/// Nothing is stubbed: requests are created through the same endpoints the
/// browser calls, with a genuine Identity cookie and a genuine anti-forgery
/// token, and the assertions about ownership are the ones a curious family
/// member could test by hand.
/// </remarks>
[TestClass]
public sealed class RequestWorkflowEndpointTests
{
    private static readonly string[] BothFormats = ["Ebook", "Audiobook"];
    private static readonly string[] EbookOnly = ["Ebook"];
    private static readonly string[] CancelOnly = ["Cancelled"];

    private static WebTestFixture? _fixture;

    [ClassInitialize]
    public static async Task InitializeAsync(TestContext testContext)
    {
        ArgumentNullException.ThrowIfNull(testContext);
        _fixture = await WebTestFixture.CreateAsync();
    }

    [ClassCleanup]
    public static async Task CleanupAsync()
    {
        if (_fixture is not null)
        {
            await _fixture.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task AUserCanRequestBothFormatsAndSeeItInTheirOwnList()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateRequestingClientAsync(fixture);
        var workId = await ResolveWorkAsync(client, "the-hobbit");

        var created = await CreateRequestAsync(client, workId, ["Ebook", "Audiobook"], "For the car.");

        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
        var request = await created.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(request);
        Assert.AreEqual("PendingAcquisition", request.Status);
        Assert.IsTrue(request.IsActive);
        Assert.AreEqual("For the car.", request.Note);
        CollectionAssert.AreEquivalent(
            BothFormats,
            request.Formats.Select(format => format.MediaType).ToArray());

        var mine = await client.GetFromJsonAsync<BookRequestListResponse>("/api/v1/me/requests");
        Assert.IsNotNull(mine);
        Assert.IsTrue(mine.Active.Any(item => item.Id == request.Id));
        Assert.AreEqual("The Hobbit", mine.Active.Single(item => item.Id == request.Id).WorkTitle);
    }

    [TestMethod]
    public async Task ASecondOverlappingRequestIsAnsweredWithTheOutstandingOne()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateRequestingClientAsync(fixture);
        var workId = await ResolveWorkAsync(client, "a-wrinkle-in-time");

        var first = await CreateRequestAsync(client, workId, ["Ebook"], null);
        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode);

        var second = await CreateRequestAsync(client, workId, ["Ebook"], null);

        Assert.AreEqual(HttpStatusCode.Conflict, second.StatusCode);
        var conflict = await second.Content.ReadFromJsonAsync<CreateBookRequestConflictResponse>();
        Assert.IsNotNull(conflict);
        Assert.AreEqual("Duplicate", conflict.Kind);
        Assert.IsNotNull(conflict.Duplicate);
        CollectionAssert.AreEqual(EbookOnly, conflict.Duplicate.OverlappingFormats.ToArray());
        Assert.IsNotNull(conflict.Duplicate.ExistingRequest);

        // The warning is not a wall: confirming it creates the repeat request.
        var confirmed = await CreateRequestAsync(client, workId, ["Ebook"], null, confirmDuplicate: true);
        Assert.AreEqual(HttpStatusCode.Created, confirmed.StatusCode);
    }

    [TestMethod]
    public async Task ARequestForAnOwnedEbookIsAnsweredWithAnOwnedWarning()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = new FamilyLibrarianAppFactory(
            fixture.ConnectionString,
            services =>
            {
                services.RemoveAll<ICwaCatalogClient>();
                services.AddSingleton<ICwaCatalogClient>(new DeterministicCatalogClient("42"));
            });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await SignInAsync(client, WebTestFixture.UserEmail, WebTestFixture.UserPassword);
        var userToken = await WebTestFixture.GetAntiforgeryTokenAsync(client);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, userToken);

        using var adminClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await SignInAsync(adminClient, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword);
        var adminToken = await WebTestFixture.GetAntiforgeryTokenAsync(adminClient);
        adminClient.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, adminToken);
        await ConfigureCwaAsync(adminClient);

        var workId = await ResolveWorkAsync(client, "the-hobbit");

        var response = await CreateRequestAsync(client, workId, ["Ebook"], null);

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
        var conflict = await response.Content.ReadFromJsonAsync<CreateBookRequestConflictResponse>();
        Assert.IsNotNull(conflict);
        Assert.AreEqual("Owned", conflict.Kind);
        Assert.IsNotNull(conflict.Owned);
        var owned = conflict.Owned.OwnedFormats.Single();
        Assert.AreEqual("Ebook", owned.MediaType);
        Assert.AreEqual("cwa", owned.ProviderId);

        // The warning is not a wall: confirming it creates the request anyway.
        // confirmDuplicate is also set here since this demo work/user pair is
        // shared with other tests in this class against the same database —
        // this assertion is only about the ownership confirmation itself.
        var confirmed = await CreateRequestAsync(
            client, workId, ["Ebook"], null, confirmDuplicate: true, confirmOwned: true);
        Assert.AreEqual(HttpStatusCode.Created, confirmed.StatusCode);
    }

    [TestMethod]
    public async Task ARequestForAnUnknownWorkIsNotFound()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateRequestingClientAsync(fixture);

        var response = await CreateRequestAsync(client, Guid.NewGuid(), ["Ebook"], null);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task ARequestWithNoFormatIsRejected()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateRequestingClientAsync(fixture);
        var workId = await ResolveWorkAsync(client, "project-hail-mary");

        var response = await CreateRequestAsync(client, workId, [], null);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task ARequesterCanCancelAndReopenTheirRequest()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateRequestingClientAsync(fixture);
        var workId = await ResolveWorkAsync(client, "project-hail-mary");
        var request = await CreateAndReadRequestAsync(client, workId, ["Audiobook"]);

        var cancelled = await TransitionAsync(client, request.Id, "Cancelled", "Borrowed it.");
        await AssertStatusAsync(HttpStatusCode.OK, cancelled);
        var afterCancel = await cancelled.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(afterCancel);
        Assert.AreEqual("Cancelled", afterCancel.Status);
        Assert.IsFalse(afterCancel.IsActive);
        Assert.IsTrue(afterCancel.Formats.All(format => format.Status == "Cancelled"));

        var mine = await client.GetFromJsonAsync<BookRequestListResponse>("/api/v1/me/requests");
        Assert.IsNotNull(mine);
        Assert.IsTrue(mine.History.Any(item => item.Id == request.Id));
        Assert.IsFalse(mine.Active.Any(item => item.Id == request.Id));

        var reopened = await TransitionAsync(client, request.Id, "PendingAcquisition", null);
        Assert.AreEqual(HttpStatusCode.OK, reopened.StatusCode);
        var afterReopen = await reopened.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(afterReopen);
        Assert.AreEqual("PendingAcquisition", afterReopen.Status);
        Assert.IsTrue(afterReopen.Formats.All(format => format.Status == "Requested"));
    }

    [TestMethod]
    public async Task ARequesterCannotDriveAStatusReservedForTheLibrarian()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateRequestingClientAsync(fixture);
        var workId = await ResolveWorkAsync(client, "the-hobbit");
        var request = await CreateAndReadRequestAsync(client, workId, ["Ebook"], confirmDuplicate: true);

        var needsReview = await TransitionAsync(client, request.Id, "NeedsReview", null);
        var notAvailable = await TransitionAsync(client, request.Id, "NotAvailable", null);

        Assert.AreEqual(HttpStatusCode.BadRequest, needsReview.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, notAvailable.StatusCode);
        CollectionAssert.AreEqual(
            CancelOnly,
            request.AvailableTransitions.ToArray());
    }

    [TestMethod]
    public async Task OneUsersRequestIsInvisibleAndUntouchableToAnother()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var owner = await CreateRequestingClientAsync(fixture);
        var workId = await ResolveWorkAsync(owner, "a-wrinkle-in-time");
        var request = await CreateAndReadRequestAsync(
            owner,
            workId,
            ["Audiobook"],
            confirmDuplicate: true);

        // A different account — and an administrator at that, so this also shows
        // the admin queue is not reachable through the requester's own routes.
        using var other = await fixture.CreateAdminClientAsync();
        var otherToken = await WebTestFixture.GetAntiforgeryTokenAsync(other);
        other.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, otherToken);

        var theirList = await other.GetFromJsonAsync<BookRequestListResponse>("/api/v1/me/requests");
        var cancelAttempt = await TransitionAsync(other, request.Id, "Cancelled", null);

        Assert.IsNotNull(theirList);
        Assert.IsFalse(theirList.Active.Any(item => item.Id == request.Id));
        Assert.IsFalse(theirList.History.Any(item => item.Id == request.Id));

        // 404, not 403: answering "forbidden" would confirm the request exists.
        Assert.AreEqual(HttpStatusCode.NotFound, cancelAttempt.StatusCode);

        var stillMine = await owner.GetFromJsonAsync<BookRequestListResponse>("/api/v1/me/requests");
        Assert.IsNotNull(stillMine);
        Assert.AreEqual(
            "PendingAcquisition",
            stillMine.Active.Single(item => item.Id == request.Id).Status);
    }

    [TestMethod]
    public async Task ARequestSurvivesARestartOfTheHost()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateRequestingClientAsync(fixture);
        var workId = await ResolveWorkAsync(client, "project-hail-mary");
        var request = await CreateAndReadRequestAsync(
            client,
            workId,
            ["Ebook"],
            confirmDuplicate: true);

        await using var restarted = fixture.RestartHost();
        using var afterRestart = await restarted.CreateUserClientAsync();

        var mine = await afterRestart.GetFromJsonAsync<BookRequestListResponse>("/api/v1/me/requests");

        Assert.IsNotNull(mine);
        var found = mine.Active.SingleOrDefault(item => item.Id == request.Id);
        Assert.IsNotNull(found, "The request did not survive the restart.");
        Assert.AreEqual("Project Hail Mary", found.WorkTitle);
    }

    private static async Task<HttpClient> CreateRequestingClientAsync(WebTestFixture fixture)
    {
        var client = await fixture.CreateUserClientAsync();
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(client);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);
        return client;
    }

    /// <summary>
    /// Turns a deterministic demo candidate into a canonical Work, the same way
    /// the browser does before offering a request action.
    /// </summary>
    private static async Task<Guid> ResolveWorkAsync(HttpClient client, string externalId)
    {
        var response = await client.PostAsync(
            $"/api/v1/catalog/candidates/demo/{externalId}/resolve",
            content: null);
        response.EnsureSuccessStatusCode();

        var work = await response.Content.ReadFromJsonAsync<CatalogWorkResponse>();
        Assert.IsNotNull(work);
        return work.Id;
    }

    private static Task<HttpResponseMessage> CreateRequestAsync(
        HttpClient client,
        Guid workId,
        string[] formats,
        string? note,
        bool confirmDuplicate = false,
        bool confirmOwned = false) =>
        client.PostAsJsonAsync(
            "/api/v1/requests/",
            new CreateBookRequestRequest(workId, formats, note, confirmDuplicate, confirmOwned));

    private static async Task<BookRequestResponse> CreateAndReadRequestAsync(
        HttpClient client,
        Guid workId,
        string[] formats,
        bool confirmDuplicate = false,
        bool confirmOwned = false)
    {
        var response = await CreateRequestAsync(client, workId, formats, null, confirmDuplicate, confirmOwned);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);

        var request = await response.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(request);
        return request;
    }

    private static async Task SignInAsync(HttpClient client, string email, string password)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = email, Password = password });
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task ConfigureCwaAsync(HttpClient client)
    {
        var settings = await client.PutAsJsonAsync(
            "/api/v1/admin/publishing/cwa/",
            new FamilyLibrarian.Contracts.Publishing.SetCwaSettingsRequest(
                "Local", "/data/cwa-ingest-test", null, null, null, null, "PrivateKey", "https://cwa.example.test", null));
        settings.EnsureSuccessStatusCode();

        // Enabling requires a passing connection test for the saved configuration
        // (docs/01 §12.1.1) -- FamilyLibrarianAppFactory registers a default-safe
        // ICwaConnectionTester double, so this succeeds without a reachable CWA.
        var test = await client.PostAsJsonAsync("/api/v1/admin/publishing/cwa/test", new { });
        test.EnsureSuccessStatusCode();

        var enabled = await client.PutAsJsonAsync(
            "/api/v1/admin/publishing/cwa/enabled",
            new FamilyLibrarian.Contracts.Publishing.SetPublishingEnabledRequest(true));
        enabled.EnsureSuccessStatusCode();
    }

    private sealed class DeterministicCatalogClient(string? bookId) : ICwaCatalogClient
    {
        public Task<string?> FindBookIdAsync(string title, string? author, CancellationToken cancellationToken) =>
            Task.FromResult(bookId);
    }

    /// <summary>
    /// Asserts a status code and reports the response body when it does not
    /// match, so a failure names the reason instead of only the number.
    /// </summary>
    private static async Task AssertStatusAsync(
        HttpStatusCode expected,
        HttpResponseMessage response)
    {
        if (response.StatusCode == expected)
        {
            return;
        }

        Assert.Fail(
            $"Expected {expected} but got {response.StatusCode} for " +
            $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}: " +
            await response.Content.ReadAsStringAsync());
    }

    private static Task<HttpResponseMessage> TransitionAsync(
        HttpClient client,
        Guid requestId,
        string status,
        string? reason) =>
        client.PostAsJsonAsync(
            $"/api/v1/requests/{requestId}/transitions",
            new ChangeBookRequestStatusRequest(status, reason));
}
