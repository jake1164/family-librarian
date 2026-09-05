using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Contracts.Requests;
using FamilyLibrarian.Domain.Acquisition;
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

    [TestMethod]
    public async Task AttentionSummaryMakesReviewWorkAndLatestSourceFailureVisibleToAnAdmin()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var owner = await CreateTokenClientAsync(fixture, isAdmin: false);
        var workId = await ResolveWorkAsync(owner, "a-wrinkle-in-time");
        var created = await CreateRequestAsync(owner, workId);

        using var admin = await CreateTokenClientAsync(fixture, isAdmin: true);
        var moved = await admin.PostAsJsonAsync(
            $"/api/v1/admin/requests/{created.Id}/transitions",
            new ChangeBookRequestStatusRequest("NeedsReview", "Two copies need review.", created.Version));
        Assert.AreEqual(HttpStatusCode.OK, moved.StatusCode);

        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var formatId = created.Formats.Single().FormatId;
            database.ProviderAttempts.Add(new ProviderAttempt(
                created.Id,
                formatId,
                "gutendex",
                ProviderAttemptOutcome.Failed,
                "The automatic provider could not be reached; automatic retry is disabled for this source.",
                DateTimeOffset.UtcNow,
                nextEligibleCheckAtUtc: null));
            await database.SaveChangesAsync();
        }

        var response = await admin.GetAsync("/api/v1/admin/requests/attention");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var attention = await response.Content.ReadFromJsonAsync<AdminRequestAttentionResponse>();
        Assert.IsNotNull(attention);
        Assert.IsTrue(attention.NeedsReviewCount >= 1);
        var sourceIssue = attention.ProviderIssues.Single(issue => issue.ProviderId == "gutendex");
        Assert.AreEqual("Project Gutenberg", sourceIssue.DisplayName);
        Assert.AreEqual("Operational", sourceIssue.IssueKind);
        StringAssert.Contains(sourceIssue.Summary, "could not be reached");

        using var nonAdmin = await CreateTokenClientAsync(fixture, isAdmin: false);
        var forbidden = await nonAdmin.GetAsync("/api/v1/admin/requests/attention");
        Assert.AreEqual(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [TestMethod]
    public async Task RecheckByProviderOnlyRequeuesRequestsWithAPriorAttemptForThatProvider()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var owner = await CreateTokenClientAsync(fixture, isAdmin: false);
        using var admin = await CreateTokenClientAsync(fixture, isAdmin: true);

        // Failed against Gutendex — the recheck-by-provider case should pick this one up.
        var withAttempt = await CreateRequestAsync(owner, await ResolveWorkAsync(owner, "project-hail-mary"));
        var withAttemptReview = await admin.PostAsJsonAsync(
            $"/api/v1/admin/requests/{withAttempt.Id}/transitions",
            new ChangeBookRequestStatusRequest("NeedsReview", "No automatic match.", withAttempt.Version));
        Assert.AreEqual(HttpStatusCode.OK, withAttemptReview.StatusCode);
        var withAttemptReviewed = await withAttemptReview.Content.ReadFromJsonAsync<AdminBookRequestResponse>();
        Assert.IsNotNull(withAttemptReviewed);

        // No provider was ever consulted for this one — recheck-by-provider must leave it alone.
        // Ebook, not Audiobook: another test in this class already holds an active
        // Audiobook request against "the-hobbit" for this same fixed test user.
        var hobbitWorkId = await ResolveWorkAsync(owner, "the-hobbit");
        var withoutAttemptCreate = await owner.PostAsJsonAsync(
            "/api/v1/requests/",
            new CreateBookRequestRequest(hobbitWorkId, ["Ebook"], "For the recheck test.", false, false));
        Assert.AreEqual(HttpStatusCode.Created, withoutAttemptCreate.StatusCode);
        var withoutAttempt = await withoutAttemptCreate.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(withoutAttempt);
        var withoutAttemptReview = await admin.PostAsJsonAsync(
            $"/api/v1/admin/requests/{withoutAttempt.Id}/transitions",
            new ChangeBookRequestStatusRequest("NeedsReview", "Held for a manual reason.", withoutAttempt.Version));
        Assert.AreEqual(HttpStatusCode.OK, withoutAttemptReview.StatusCode);

        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            database.ProviderAttempts.Add(new ProviderAttempt(
                withAttempt.Id,
                withAttempt.Formats.Single().FormatId,
                "gutendex",
                ProviderAttemptOutcome.NoMatch,
                "No high-confidence automatic copy was found.",
                DateTimeOffset.UtcNow,
                nextEligibleCheckAtUtc: null));
            await database.SaveChangesAsync();
        }

        var recheck = await admin.PostAsJsonAsync(
            "/api/v1/admin/requests/recheck",
            new RecheckNeedsReviewRequest("gutendex"));
        Assert.AreEqual(HttpStatusCode.OK, recheck.StatusCode);
        var recheckResult = await recheck.Content.ReadFromJsonAsync<RecheckNeedsReviewResponse>();
        Assert.IsNotNull(recheckResult);
        // Other tests in this shared class fixture may also leave a NeedsReview
        // request with a "gutendex" attempt behind, so this only asserts that
        // recheck swept up at least this test's own request — the two status
        // checks below confirm which request actually moved.
        Assert.IsTrue(recheckResult.RequeuedCount >= 1);

        var requeued = await admin.GetFromJsonAsync<AdminBookRequestResponse>($"/api/v1/admin/requests/{withAttempt.Id}");
        Assert.IsNotNull(requeued);
        Assert.AreEqual("PendingAcquisition", requeued.Request.Status);
        Assert.IsTrue(requeued.Request.Version > withAttemptReviewed!.Request.Version);

        var stillNeedsReview = await admin.GetFromJsonAsync<AdminBookRequestResponse>($"/api/v1/admin/requests/{withoutAttempt.Id}");
        Assert.IsNotNull(stillNeedsReview);
        Assert.AreEqual("NeedsReview", stillNeedsReview.Request.Status);
    }

    [TestMethod]
    public async Task RecheckWithAnUnregisteredProviderIsRejected()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var admin = await CreateTokenClientAsync(fixture, isAdmin: true);

        var response = await admin.PostAsJsonAsync(
            "/api/v1/admin/requests/recheck",
            new RecheckNeedsReviewRequest("not-a-real-provider"));

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
        return await WebTestFixture.Require(_fixture).CopyWorkForTestAsync(work.Id);
    }

    private static async Task<BookRequestResponse> CreateRequestAsync(HttpClient client, Guid workId)
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/requests/",
            new CreateBookRequestRequest(workId, ["Audiobook"], "For a road trip.", false, false));
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        var request = await response.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(request);
        return request;
    }
}
