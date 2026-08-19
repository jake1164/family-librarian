using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Contracts.Notifications;
using FamilyLibrarian.Contracts.Requests;
using FamilyLibrarian.Web.Tests.Harness;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// The notification tray against the real host and PostgreSQL: an admin-broadcast
/// event (a request needing review) versus a single-user event (a request becoming
/// available), through the same transitions the admin queue already exercises.
/// </summary>
[TestClass]
public sealed class NotificationEndpointTests
{
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
    public async Task ANeedsReviewTransitionNotifiesAdminsOnlyNotTheOwner()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var owner = await CreateTokenClientAsync(fixture, isAdmin: false);
        var workId = await ResolveWorkAsync(owner, "the-hobbit");
        var created = await CreateRequestAsync(owner, workId);

        using var admin = await CreateTokenClientAsync(fixture, isAdmin: true);
        var moved = await admin.PostAsJsonAsync(
            $"/api/v1/admin/requests/{created.Id}/transitions",
            new ChangeBookRequestStatusRequest("NeedsReview", "Which edition?", created.Version));
        Assert.AreEqual(HttpStatusCode.OK, moved.StatusCode);

        var adminFeed = await admin.GetFromJsonAsync<NotificationListResponse>("/api/v1/notifications/");
        Assert.IsNotNull(adminFeed);
        var notification = adminFeed.Notifications.Single(item =>
            item.Category == "request.needs_review" && item.SubjectId == created.Id.ToString());
        Assert.AreEqual("\"The Hobbit\" needs review", notification.Title);
        Assert.AreEqual("Warning", notification.Severity);
        Assert.AreEqual(1, notification.RepeatCount);

        var ownerFeed = await owner.GetFromJsonAsync<NotificationListResponse>("/api/v1/notifications/");
        Assert.IsNotNull(ownerFeed);
        Assert.IsFalse(ownerFeed.Notifications.Any(item => item.SubjectId == created.Id.ToString()));
    }

    [TestMethod]
    public async Task ARequestBecomingAvailableNotifiesOnlyItsOwner()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var owner = await CreateTokenClientAsync(fixture, isAdmin: false);
        var workId = await ResolveWorkAsync(owner, "a-wrinkle-in-time");
        var created = await CreateRequestAsync(owner, workId);

        using var admin = await CreateTokenClientAsync(fixture, isAdmin: true);
        var moved = await admin.PostAsJsonAsync(
            $"/api/v1/admin/requests/{created.Id}/transitions",
            new ChangeBookRequestStatusRequest("Available", null, created.Version));
        Assert.AreEqual(HttpStatusCode.OK, moved.StatusCode);

        var ownerFeed = await owner.GetFromJsonAsync<NotificationListResponse>("/api/v1/notifications/");
        Assert.IsNotNull(ownerFeed);
        var notification = ownerFeed.Notifications.Single(item => item.SubjectId == created.Id.ToString());
        Assert.AreEqual("\"A Wrinkle in Time\" is available", notification.Title);
        Assert.AreEqual("Info", notification.Severity);

        var adminFeed = await admin.GetFromJsonAsync<NotificationListResponse>("/api/v1/notifications/");
        Assert.IsNotNull(adminFeed);
        Assert.IsFalse(adminFeed.Notifications.Any(item => item.SubjectId == created.Id.ToString()));
    }

    [TestMethod]
    public async Task DismissingRemovesItFromLaterListsForThatViewer()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var owner = await CreateTokenClientAsync(fixture, isAdmin: false);
        var workId = await ResolveWorkAsync(owner, "project-hail-mary");
        var created = await CreateRequestAsync(owner, workId);

        using var admin = await CreateTokenClientAsync(fixture, isAdmin: true);
        var moved = await admin.PostAsJsonAsync(
            $"/api/v1/admin/requests/{created.Id}/transitions",
            new ChangeBookRequestStatusRequest("NeedsReview", null, created.Version));
        Assert.AreEqual(HttpStatusCode.OK, moved.StatusCode);

        var before = await admin.GetFromJsonAsync<NotificationListResponse>("/api/v1/notifications/");
        Assert.IsNotNull(before);
        var notification = before.Notifications.Single(item => item.SubjectId == created.Id.ToString());

        var dismiss = await admin.PostAsync($"/api/v1/notifications/{notification.Id}/dismiss", content: null);
        Assert.AreEqual(HttpStatusCode.NoContent, dismiss.StatusCode);

        var after = await admin.GetFromJsonAsync<NotificationListResponse>("/api/v1/notifications/");
        Assert.IsNotNull(after);
        Assert.IsFalse(after.Notifications.Any(item => item.Id == notification.Id));
    }

    private static async Task<HttpClient> CreateTokenClientAsync(WebTestFixture fixture, bool isAdmin)
    {
        var client = isAdmin
            ? await fixture.CreateAdminClientAsync()
            : await fixture.CreateUserClientAsync();
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(client);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);
        return client;
    }

    private static async Task<Guid> ResolveWorkAsync(HttpClient client, string externalId)
    {
        var response = await client.PostAsync(
            $"/api/v1/catalog/candidates/demo/{externalId}/resolve", content: null);
        response.EnsureSuccessStatusCode();
        var work = await response.Content.ReadFromJsonAsync<CatalogWorkResponse>();
        Assert.IsNotNull(work);
        return work.Id;
    }

    private static async Task<BookRequestResponse> CreateRequestAsync(HttpClient client, Guid workId)
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/requests/",
            new CreateBookRequestRequest(workId, ["Ebook"], null, false));
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        var request = await response.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(request);
        return request;
    }
}
