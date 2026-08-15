using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Contracts.Feedback;
using FamilyLibrarian.Web.Tests.Harness;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// My Reading (completion date + rating) against the real host and a real
/// PostgreSQL database.
/// </summary>
/// <remarks>
/// Nothing is stubbed: feedback is recorded through the same endpoints the
/// browser calls, with a genuine Identity cookie and a genuine anti-forgery
/// token, and the assertions about ownership are the ones a curious family
/// member could test by hand.
/// </remarks>
[TestClass]
public sealed class FeedbackWorkflowEndpointTests
{
    private static readonly DateOnly CompletedOn = new(2026, 8, 1);

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
    public async Task ARatingCanBeRecordedAndSeenInMyReading()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateFeedbackClientAsync(fixture);
        var workId = await ResolveWorkAsync(client, "the-hobbit");
        await EnsureNoFeedbackAsync(client, workId);

        var response = await SetFeedbackAsync(client, workId, CompletedOn, 5, expectedVersion: null);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var feedback = await response.Content.ReadFromJsonAsync<WorkFeedbackResponse>();
        Assert.IsNotNull(feedback);
        Assert.AreEqual(CompletedOn, feedback.CompletedOn);
        Assert.AreEqual(5, feedback.Rating);

        var mine = await client.GetFromJsonAsync<WorkFeedbackListResponse>("/api/v1/me/feedback");
        Assert.IsNotNull(mine);
        Assert.IsTrue(mine.Items.Any(item => item.WorkId == workId && item.Rating == 5));
    }

    [TestMethod]
    public async Task RecordingFeedbackTwiceCorrectsTheExistingRowInstead()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateFeedbackClientAsync(fixture);
        var workId = await ResolveWorkAsync(client, "a-wrinkle-in-time");
        await EnsureNoFeedbackAsync(client, workId);

        var first = await SetFeedbackAsync(client, workId, CompletedOn, 3, expectedVersion: null);
        Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
        var created = await first.Content.ReadFromJsonAsync<WorkFeedbackResponse>();
        Assert.IsNotNull(created);

        var second = await SetFeedbackAsync(
            client,
            workId,
            CompletedOn.AddDays(1),
            4,
            expectedVersion: created.Version);

        Assert.AreEqual(HttpStatusCode.OK, second.StatusCode);
        var corrected = await second.Content.ReadFromJsonAsync<WorkFeedbackResponse>();
        Assert.IsNotNull(corrected);
        Assert.AreEqual(4, corrected.Rating);

        var mine = await client.GetFromJsonAsync<WorkFeedbackListResponse>("/api/v1/me/feedback");
        Assert.IsNotNull(mine);
        Assert.AreEqual(1, mine.Items.Count(item => item.WorkId == workId));
    }

    [TestMethod]
    public async Task ARatingOutsideOneToFiveIsRejected()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateFeedbackClientAsync(fixture);
        var workId = await ResolveWorkAsync(client, "project-hail-mary");

        var response = await SetFeedbackAsync(client, workId, CompletedOn, 6, expectedVersion: null);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task FeedbackForAnUnknownWorkIsNotFound()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateFeedbackClientAsync(fixture);

        var response = await SetFeedbackAsync(client, Guid.NewGuid(), CompletedOn, 3, expectedVersion: null);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task AStaleVersionIsAnsweredWithConflictRatherThanOverwriting()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateFeedbackClientAsync(fixture);
        var workId = await ResolveWorkAsync(client, "the-hobbit");
        await EnsureNoFeedbackAsync(client, workId);

        var first = await SetFeedbackAsync(client, workId, CompletedOn, 3, expectedVersion: null);
        Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
        var created = await first.Content.ReadFromJsonAsync<WorkFeedbackResponse>();
        Assert.IsNotNull(created);

        // Someone else's tab already moved the version forward.
        await SetFeedbackAsync(client, workId, CompletedOn, 4, expectedVersion: created.Version);

        var stale = await SetFeedbackAsync(client, workId, CompletedOn, 5, expectedVersion: created.Version);

        Assert.AreEqual(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [TestMethod]
    public async Task FeedbackCanBeRemoved()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateFeedbackClientAsync(fixture);
        var workId = await ResolveWorkAsync(client, "a-wrinkle-in-time");
        await EnsureNoFeedbackAsync(client, workId);

        var created = await SetFeedbackAsync(client, workId, CompletedOn, 3, expectedVersion: null);
        Assert.AreEqual(HttpStatusCode.OK, created.StatusCode);
        var feedback = await created.Content.ReadFromJsonAsync<WorkFeedbackResponse>();
        Assert.IsNotNull(feedback);

        var removed = await RemoveFeedbackAsync(client, workId, feedback.Version);
        Assert.AreEqual(HttpStatusCode.NoContent, removed.StatusCode);

        var afterRemoval = await client.GetAsync($"/api/v1/me/feedback/{workId}");
        Assert.AreEqual(HttpStatusCode.NotFound, afterRemoval.StatusCode);
    }

    [TestMethod]
    public async Task OneUsersFeedbackIsInvisibleAndUntouchableToAnother()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var owner = await CreateFeedbackClientAsync(fixture);
        var workId = await ResolveWorkAsync(owner, "project-hail-mary");
        await EnsureNoFeedbackAsync(owner, workId);
        var created = await SetFeedbackAsync(owner, workId, CompletedOn, 5, expectedVersion: null);
        Assert.AreEqual(HttpStatusCode.OK, created.StatusCode);
        var feedback = await created.Content.ReadFromJsonAsync<WorkFeedbackResponse>();
        Assert.IsNotNull(feedback);

        // A different account — and an administrator at that, so this also shows
        // an admin has no back door into another family member's private rating.
        using var other = await fixture.CreateAdminClientAsync();
        var otherToken = await WebTestFixture.GetAntiforgeryTokenAsync(other);
        other.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, otherToken);

        var theirList = await other.GetFromJsonAsync<WorkFeedbackListResponse>("/api/v1/me/feedback");
        var readAttempt = await other.GetAsync($"/api/v1/me/feedback/{workId}");
        var removeAttempt = await RemoveFeedbackAsync(other, workId, feedback.Version);

        Assert.IsNotNull(theirList);
        Assert.IsFalse(theirList.Items.Any(item => item.WorkId == workId));

        // 404, not 403: answering "forbidden" would confirm the rating exists.
        Assert.AreEqual(HttpStatusCode.NotFound, readAttempt.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, removeAttempt.StatusCode);

        var stillMine = await owner.GetFromJsonAsync<WorkFeedbackListResponse>("/api/v1/me/feedback");
        Assert.IsNotNull(stillMine);
        Assert.IsTrue(stillMine.Items.Any(item => item.WorkId == workId));
    }

    [TestMethod]
    public async Task FeedbackSurvivesARestartOfTheHost()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateFeedbackClientAsync(fixture);
        var workId = await ResolveWorkAsync(client, "project-hail-mary");
        await EnsureNoFeedbackAsync(client, workId);
        var recorded = await SetFeedbackAsync(client, workId, CompletedOn, 5, expectedVersion: null);
        Assert.AreEqual(HttpStatusCode.OK, recorded.StatusCode);

        await using var restarted = fixture.RestartHost();
        using var afterRestart = await restarted.CreateUserClientAsync();

        var mine = await afterRestart.GetFromJsonAsync<WorkFeedbackListResponse>("/api/v1/me/feedback");

        Assert.IsNotNull(mine);
        var found = mine.Items.SingleOrDefault(item => item.WorkId == workId);
        Assert.IsNotNull(found, "The feedback did not survive the restart.");
        Assert.AreEqual(5, found.Rating);
    }

    private static async Task<HttpClient> CreateFeedbackClientAsync(WebTestFixture fixture)
    {
        var client = await fixture.CreateUserClientAsync();
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(client);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);
        return client;
    }

    /// <summary>
    /// Clears any feedback the fixture's one shared reader already left for this
    /// Work. The demo catalog has only three books, so several tests in this
    /// class necessarily reuse the same (user, Work) pair; without this, a test
    /// that assumes it is creating fresh feedback can land on a row an earlier
    /// test left behind and get an unexpected 409 for the wrong reason.
    /// </summary>
    private static async Task EnsureNoFeedbackAsync(HttpClient client, Guid workId)
    {
        var existing = await client.GetAsync($"/api/v1/me/feedback/{workId}");
        if (existing.StatusCode != HttpStatusCode.OK)
        {
            return;
        }

        var feedback = await existing.Content.ReadFromJsonAsync<WorkFeedbackResponse>();
        Assert.IsNotNull(feedback);
        var removed = await RemoveFeedbackAsync(client, workId, feedback.Version);
        Assert.AreEqual(HttpStatusCode.NoContent, removed.StatusCode);
    }

    /// <summary>
    /// Turns a deterministic demo candidate into a canonical Work, the same way
    /// the browser does before offering a request or feedback action.
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

    private static Task<HttpResponseMessage> SetFeedbackAsync(
        HttpClient client,
        Guid workId,
        DateOnly completedOn,
        int rating,
        uint? expectedVersion) =>
        client.PutAsJsonAsync(
            $"/api/v1/me/feedback/{workId}",
            new SetWorkFeedbackRequest(completedOn, rating, expectedVersion));

    private static Task<HttpResponseMessage> RemoveFeedbackAsync(
        HttpClient client,
        Guid workId,
        uint expectedVersion)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/me/feedback/{workId}")
        {
            Content = JsonContent.Create(new RemoveWorkFeedbackRequest(expectedVersion))
        };
        return client.SendAsync(request);
    }
}
