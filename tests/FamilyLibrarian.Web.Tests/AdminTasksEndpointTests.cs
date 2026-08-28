using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Contracts.Operations;
using FamilyLibrarian.Contracts.Requests;
using FamilyLibrarian.Domain.Acquisition;
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
            new CreateBookRequestRequest(workId, ["Ebook"], "For the dashboard.", false));
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
