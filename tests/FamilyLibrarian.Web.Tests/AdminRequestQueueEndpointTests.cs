using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Contracts.Requests;
using FamilyLibrarian.Domain.Audit;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// Exercises the entire administrator review loop through the real host and
/// PostgreSQL. In particular, these tests prove that queue visibility is an
/// Admin-only exception to the otherwise private request model.
/// </summary>
[TestClass]
public sealed class AdminRequestQueueEndpointTests
{
    private static readonly string[] AdminPendingTransitions =
        ["NeedsReview", "NotAvailable", "Cancelled"];

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
    public async Task AnAdminCanReviewARequestAddANoteAndLeaveAnAuditableTimeline()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var owner = await CreateTokenClientAsync(fixture, isAdmin: false);
        var workId = await ResolveWorkAsync(owner, "the-hobbit");
        var created = await CreateRequestAsync(owner, workId);

        using var admin = await CreateTokenClientAsync(fixture, isAdmin: true);
        var queue = await admin.GetFromJsonAsync<AdminBookRequestListResponse>(
            "/api/v1/admin/requests/?status=PendingAcquisition");

        Assert.IsNotNull(queue);
        var queued = queue.Requests.Single(item => item.Request.Id == created.Id);
        Assert.AreEqual(WebTestFixture.UserEmail, queued.RequesterEmail);
        Assert.AreEqual("Reader", queued.RequesterDisplayName);
        CollectionAssert.AreEqual(
            AdminPendingTransitions,
            queued.Request.AvailableTransitions.ToArray());

        var needsReview = await admin.PostAsJsonAsync(
            $"/api/v1/admin/requests/{created.Id}/transitions",
            new ChangeBookRequestStatusRequest(
                "NeedsReview", "Which narrator would you prefer?", queued.Request.Version));
        Assert.AreEqual(HttpStatusCode.OK, needsReview.StatusCode);
        var reviewed = await needsReview.Content.ReadFromJsonAsync<AdminBookRequestResponse>();
        Assert.IsNotNull(reviewed);
        Assert.AreEqual("NeedsReview", reviewed.Request.Status);
        Assert.AreEqual(2, reviewed.StatusHistory.Count);
        Assert.AreEqual(
            "Which narrator would you prefer?",
            reviewed.StatusHistory[reviewed.StatusHistory.Count - 1].Reason);

        var note = await admin.PutAsJsonAsync(
            $"/api/v1/admin/requests/{created.Id}/note",
            new SetAdminBookRequestNoteRequest("I found two audiobook editions.", reviewed.Request.Version));
        Assert.AreEqual(HttpStatusCode.OK, note.StatusCode);
        var noted = await note.Content.ReadFromJsonAsync<AdminBookRequestResponse>();
        Assert.IsNotNull(noted);
        Assert.AreEqual("I found two audiobook editions.", noted.Request.AdminNote);

        // The queue's first copy is stale after the note update, so it cannot
        // silently overwrite the administrator who just saved that note.
        var stale = await admin.PostAsJsonAsync(
            $"/api/v1/admin/requests/{created.Id}/transitions",
            new ChangeBookRequestStatusRequest("NotAvailable", null, reviewed.Request.Version));
        Assert.AreEqual(HttpStatusCode.Conflict, stale.StatusCode);

        var mine = await owner.GetFromJsonAsync<BookRequestListResponse>("/api/v1/me/requests");
        Assert.IsNotNull(mine);
        var ownerView = mine.Active.Single(item => item.Id == created.Id);
        Assert.AreEqual("NeedsReview", ownerView.Status);
        Assert.AreEqual("I found two audiobook editions.", ownerView.AdminNote);

        await using var scope = fixture.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var audits = await database.AuditEvents
            .Where(entry => entry.SubjectType == AuditSubjectTypes.BookRequest &&
                entry.SubjectId == created.Id.ToString())
            .Select(entry => entry.Action)
            .ToArrayAsync();
        CollectionAssert.AreEquivalent(
            new[] { AuditActions.BookRequestStatusChanged, AuditActions.BookRequestNoteChanged },
            audits);
    }

    [TestMethod]
    public async Task ARequestMutationWithoutAnAntiforgeryTokenIsRejectedForAnAdminToo()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var admin = await fixture.CreateAdminClientAsync();

        var response = await admin.PutAsJsonAsync(
            $"/api/v1/admin/requests/{Guid.NewGuid()}/note",
            new SetAdminBookRequestNoteRequest("attempted cross-site write", 1));

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<HttpClient> CreateTokenClientAsync(
        WebTestFixture fixture,
        bool isAdmin)
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
            new CreateBookRequestRequest(workId, ["Audiobook"], "For a road trip.", false));
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        var request = await response.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(request);
        return request;
    }
}
