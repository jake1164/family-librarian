using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Contracts.Operations;
using FamilyLibrarian.Contracts.Requests;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Providers;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyLibrarian.Web.Tests;

[TestClass]
public sealed class AdminTasksEndpointTests
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
    public async Task DashboardCombinesRequestAndSourceActivityForAnAdminOnly()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var reader = await fixture.CreateUserClientAsync();
        reader.DefaultRequestHeaders.Add(
            AntiforgeryTokenEndpoint.HeaderName,
            await WebTestFixture.GetAntiforgeryTokenAsync(reader));
        var workId = await ResolveWorkAsync(reader, "the-hobbit");
        var create = await reader.PostAsJsonAsync(
            "/api/v1/requests/",
            new CreateBookRequestRequest(workId, ["Ebook"], "For the dashboard.", false, false));
        Assert.AreEqual(HttpStatusCode.Created, create.StatusCode);
        var request = await create.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(request);

        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            database.ProviderAttempts.Add(new ProviderAttempt(
                request.Id,
                request.Formats.Single().FormatId,
                "gutendex",
                ProviderAttemptOutcome.Acquired,
                "A verified public-domain copy was downloaded for safety checks.",
                DateTimeOffset.UtcNow,
                nextEligibleCheckAtUtc: null));
            await database.SaveChangesAsync();
        }

        using var admin = await fixture.CreateAdminClientAsync();
        var response = await admin.GetAsync("/api/v1/admin/tasks/");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var dashboard = await response.Content.ReadFromJsonAsync<AdminTasksResponse>();
        Assert.IsNotNull(dashboard);
        Assert.IsTrue(dashboard.Requests.Any(item => item.Request.Id == request.Id));
        var activity = dashboard.ProviderAttempts.Single(item => item.RequestId == request.Id && item.ProviderId == "gutendex");
        Assert.AreEqual("Ebook", activity.MediaType);
        Assert.AreEqual("Acquired", activity.Outcome);
        Assert.AreEqual("Reader", activity.RequesterDisplayName);

        var forbidden = await reader.GetAsync("/api/v1/admin/tasks/");
        Assert.AreEqual(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [TestMethod]
    public async Task SourcesShowUnusedConfigurationAndActualConcurrentWorkRatherThanHistoricalSubmissions()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var admin = await fixture.CreateAdminClientAsync();
        using var reader = await fixture.CreateUserClientAsync();
        using var anonymous = fixture.CreateAnonymousClient();
        Assert.AreEqual(HttpStatusCode.Forbidden, (await reader.GetAsync("/api/v1/admin/tasks/sources")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/admin/tasks/sources")).StatusCode);
        admin.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName,
            await WebTestFixture.GetAntiforgeryTokenAsync(admin));
        var workId = await ResolveWorkAsync(admin, "the-hobbit");
        var created = await admin.PostAsJsonAsync("/api/v1/requests/",
            new CreateBookRequestRequest(workId, ["Ebook", "Audiobook"], null, false, false));
        created.EnsureSuccessStatusCode();
        var request = await created.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(request);
        var format = request.Formats[0].FormatId;
        var otherFormat = request.Formats[1].FormatId;
        var now = DateTimeOffset.UtcNow;
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var source = new ExternalProvider("activity-source", "Activity source", "https://source.example", now);
            source.SetEnabled(true, null, now);
            source.RecordHealthCheck(true, "Reachable", "Healthy", "Available", "Available", now);
            database.ExternalProviders.Add(source);
            var unused = new ExternalProvider("unused-source", "Unused source", "https://unused.example", now);
            unused.SetEnabled(false, null, now);
            database.ExternalProviders.Add(unused);
            database.ProviderAttempts.Add(new ProviderAttempt(request.Id, format, source.ProviderId,
                ProviderAttemptOutcome.Submitted, "Old submitted row is not live activity.", now.AddDays(-3), null));
            var running = new ProviderAcquisitionJob(request.Id, format, source.Id, source.ProviderId,
                null, "source-running", "candidate-1", null, null, now);
            running.ApplyStatus(ProviderAcquisitionJobLifecycleState.Running, "downloading", null, null,
                null, null, null, null, null, null, null, now, now);
            database.ProviderAcquisitionJobs.Add(running);
            var ended = new ProviderAcquisitionJob(request.Id, otherFormat, source.Id, source.ProviderId,
                null, "source-ended", "candidate-2", null, null, now);
            ended.ApplyStatus(ProviderAcquisitionJobLifecycleState.Completed, null, null, null,
                null, null, null, null, null, null, null, null, now);
            database.ProviderAcquisitionJobs.Add(ended);
            await database.SaveChangesAsync();
        }
        var searches = fixture.Services.GetRequiredService<SourceSearchTracker>();
        using (var first = searches.Begin("activity-source"))
        using (var second = searches.Begin("ACTIVITY-SOURCE"))
        {
            var result = await admin.GetFromJsonAsync<AdminSourcesResponse>("/api/v1/admin/tasks/sources");
            Assert.IsNotNull(result);
            Assert.IsTrue(result.Sources.Any(source => source.ProviderId == "gutendex"));
            Assert.IsTrue(result.Sources.Any(source => source.ProviderId == "librivox"));
            var unused = result.Sources.Single(source => source.ProviderId == "unused-source");
            Assert.AreEqual("Disabled", unused.Availability);
            Assert.IsNull(unused.LastAttemptAtUtc);
            Assert.AreEqual(0, unused.Activities.Count);
            var active = result.Sources.Single(source => source.ProviderId == "activity-source");
            Assert.AreEqual("Ready", active.Availability);
            Assert.AreEqual(2, active.Activities.Single(activity => activity.Stage == "Searching").Count);
            Assert.AreEqual(1, active.Activities.Single(activity => activity.Stage == "Downloading").Count);
            Assert.AreEqual(2, active.Activities.Count);
            Assert.IsTrue(active.LastAttemptAtUtc < now.AddDays(-2));
            var json = await admin.GetStringAsync("/api/v1/admin/tasks/sources");
            Assert.DoesNotContain("source.example", json);
            Assert.DoesNotContain("candidate-1", json);
        }
        var after = await admin.GetFromJsonAsync<AdminSourcesResponse>("/api/v1/admin/tasks/sources");
        Assert.IsNotNull(after);
        Assert.IsFalse(after.Sources.Single(source => source.ProviderId == "activity-source")
            .Activities.Any(activity => activity.Stage == "Searching"));

        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var jobs = scope.ServiceProvider.GetRequiredService<IProviderAcquisitionJobStore>();
            var running = (await jobs.ListActiveForFormatAsync(format, CancellationToken.None))
                .Single(job => job.ProviderId == "activity-source");
            running.ApplyStatus(ProviderAcquisitionJobLifecycleState.Completed, null, null, null,
                null, null, null, null, null, null, null, null, now);
            await database.SaveChangesAsync();
        }
        after = await admin.GetFromJsonAsync<AdminSourcesResponse>("/api/v1/admin/tasks/sources");
        Assert.IsNotNull(after);
        Assert.AreEqual(0, after.Sources.Single(source => source.ProviderId == "activity-source").Activities.Count);
    }

    [TestMethod]
    [DataRow(ProviderAcquisitionJobLifecycleState.Queued, null, false, "Queued")]
    [DataRow(ProviderAcquisitionJobLifecycleState.Running, "downloading", false, "Downloading")]
    [DataRow(ProviderAcquisitionJobLifecycleState.Running, "extracting", false, "Working")]
    [DataRow(ProviderAcquisitionJobLifecycleState.Waiting, "paused", false, "Waiting")]
    [DataRow(ProviderAcquisitionJobLifecycleState.Waiting, "user-interaction", true, "WaitingForAction")]
    [DataRow(ProviderAcquisitionJobLifecycleState.Completed, null, false, null)]
    [DataRow(ProviderAcquisitionJobLifecycleState.Failed, null, false, null)]
    [DataRow(ProviderAcquisitionJobLifecycleState.Cancelled, null, false, null)]
    public async Task CurrentJobStatesAreShownEvenIfSourceIsDisabled(
        ProviderAcquisitionJobLifecycleState state, string? phase, bool interaction, string? expectedStage)
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var admin = await fixture.CreateAdminClientAsync();
        admin.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName,
            await WebTestFixture.GetAntiforgeryTokenAsync(admin));
        var workId = await ResolveWorkAsync(admin, "the-hobbit");
        var created = await admin.PostAsJsonAsync("/api/v1/requests/",
            new CreateBookRequestRequest(workId, ["Ebook"], null, false, false));
        created.EnsureSuccessStatusCode();
        var request = await created.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(request);
        var now = DateTimeOffset.UtcNow;
        var id = $"status-{Guid.NewGuid():N}";
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var source = new ExternalProvider(id, "State test source", "https://source.example", now);
            source.SetEnabled(false, null, now);
            database.ExternalProviders.Add(source);
            var job = new ProviderAcquisitionJob(request.Id, request.Formats[0].FormatId, source.Id, source.ProviderId,
                null, $"job-{id}", "candidate", null, null, now);
            job.ApplyStatus(state, phase, interaction ? "browser" : null, null,
                null, null, null, null, null, null, null, now, now);
            database.ProviderAcquisitionJobs.Add(job);
            await database.SaveChangesAsync();
        }
        var result = await admin.GetFromJsonAsync<AdminSourcesResponse>("/api/v1/admin/tasks/sources");
        Assert.IsNotNull(result);
        var status = result.Sources.Single(source => source.ProviderId == id);
        Assert.AreEqual("Disabled", status.Availability);
        if (expectedStage is null) Assert.AreEqual(0, status.Activities.Count);
        else
        {
            Assert.AreEqual(expectedStage, status.Activities.Single().Stage);
            Assert.AreEqual(1, status.Activities.Single().Count);
        }
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
}
