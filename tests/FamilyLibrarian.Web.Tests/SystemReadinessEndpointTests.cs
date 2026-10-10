using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Operations;
using FamilyLibrarian.Contracts.Providers;
using FamilyLibrarian.Domain.Providers;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// Covers the status-footer readiness signal for a registered external
/// provider, which regressed silently: <c>SystemReadinessService</c> checked
/// Gutenberg, CWA and Audiobookshelf for a failing last test but never
/// external providers, so the footer stayed "healthy" even while the
/// External Providers admin panel showed a provider as degraded.
/// </summary>
[TestClass]
public sealed class SystemReadinessEndpointTests
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
    public async Task AnEnabledExternalProviderWithAFailedTestDegradesSystemReadiness()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateAdminClientWithTokenAsync(fixture);

        var create = await client.PostAsJsonAsync(
            "/api/v1/admin/external-providers/",
            new CreateExternalProviderRequest(
                "readiness-provider", "Readiness Provider", "http://provider.test"));
        Assert.AreEqual(HttpStatusCode.OK, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<ExternalProviderResponse>();
        Assert.IsNotNull(created);

        var enable = await client.PutAsJsonAsync(
            $"/api/v1/admin/external-providers/{created.Id}/enabled",
            new SetExternalProviderEnabledRequest(true));
        Assert.AreEqual(HttpStatusCode.OK, enable.StatusCode);

        // The fixture's freshly migrated database still has Gutenberg's own
        // default-enabled local catalogue unsynced, so Healthy can already be
        // false here for reasons unrelated to this provider -- the assertion
        // that matters is that THIS provider is not (yet) among the degraded
        // components.
        var beforeTest = await client.GetFromJsonAsync<SystemReadinessResponse>("/api/v1/system/readiness");
        Assert.IsNotNull(beforeTest);
        Assert.IsFalse(
            beforeTest.DegradedComponents.Any(c => c.Name == "Readiness Provider"),
            "A newly enabled provider that has never been tested must not itself count as degraded.");

        // Records the same shape of result TestConnectionAsync persists for a
        // provider whose manifest was reached but whose health check failed,
        // without depending on a live external HTTP server in this test.
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var provider = await database.ExternalProviders.SingleAsync(p => p.Id == created.Id);
            provider.RecordTestResult(
                succeeded: false,
                message: "The manifest was reachable, but Readiness Provider reported its search or acquire capability as unavailable.",
                protocolVersion: "2",
                capabilities: "operations:search",
                actorUserId: null,
                testedAtUtc: DateTimeOffset.UtcNow,
                instanceId: "instance-1",
                healthStatus: "Degraded",
                searchOperationStatus: "Unavailable",
                acquireOperationStatus: "Available",
                manifestReached: true);
            await database.SaveChangesAsync();
        }

        var afterTest = await client.GetFromJsonAsync<SystemReadinessResponse>("/api/v1/system/readiness");
        Assert.IsNotNull(afterTest);
        Assert.IsFalse(afterTest.Healthy);
        var component = afterTest.DegradedComponents.SingleOrDefault(c => c.Name == "Readiness Provider");
        Assert.IsNotNull(component, "The degraded provider must appear in the readiness breakdown.");
        Assert.AreEqual(SystemReadinessCategories.Source, component.Category);
    }

    [TestMethod]
    public async Task AnEnabledExternalProviderWithADegradedOperationDegradesReadinessAndNamesWhy()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateAdminClientWithTokenAsync(fixture);

        var create = await client.PostAsJsonAsync(
            "/api/v1/admin/external-providers/",
            new CreateExternalProviderRequest(
                "degraded-ops-provider", "Degraded Ops Provider", "http://degraded-ops.test"));
        Assert.AreEqual(HttpStatusCode.OK, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<ExternalProviderResponse>();
        Assert.IsNotNull(created);
        var enable = await client.PutAsJsonAsync(
            $"/api/v1/admin/external-providers/{created.Id}/enabled",
            new SetExternalProviderEnabledRequest(true));
        Assert.AreEqual(HttpStatusCode.OK, enable.StatusCode);

        // Same shape the background poll records for a provider that answers
        // but has nothing usable to search: the test "succeeded" because
        // search is Degraded rather than Unavailable.
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var provider = await database.ExternalProviders.SingleAsync(p => p.Id == created.Id);
            provider.RecordHealthCheck(
                succeeded: true,
                message: "Reachable on periodic background check.",
                healthStatus: "Degraded",
                searchOperationStatus: "Degraded",
                acquireOperationStatus: "Available",
                checkedAtUtc: DateTimeOffset.UtcNow);
            await database.SaveChangesAsync();
        }

        var readiness = await client.GetFromJsonAsync<SystemReadinessResponse>("/api/v1/system/readiness");
        Assert.IsNotNull(readiness);
        Assert.IsFalse(readiness.Healthy);
        var component = readiness.DegradedComponents.SingleOrDefault(c => c.Name == "Degraded Ops Provider");
        Assert.IsNotNull(component, "A provider reporting a degraded operation must appear in the breakdown.");
        StringAssert.Contains(component.Detail, "Search is degraded.");
        Assert.IsFalse(component.Detail!.Contains("Acquire", StringComparison.Ordinal));
    }

    private static async Task<ExternalProviderResponse> CreateEnabledProviderAsync(
        HttpClient client, string id)
    {
        var create = await client.PostAsJsonAsync(
            "/api/v1/admin/external-providers/",
            new CreateExternalProviderRequest(id, id, $"http://{id}.test"));
        Assert.AreEqual(HttpStatusCode.OK, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<ExternalProviderResponse>();
        Assert.IsNotNull(created);
        var enable = await client.PutAsJsonAsync(
            $"/api/v1/admin/external-providers/{created.Id}/enabled",
            new SetExternalProviderEnabledRequest(true));
        Assert.AreEqual(HttpStatusCode.OK, enable.StatusCode);
        return created;
    }

    private static async Task RecordHealthAsync(
        WebTestFixture fixture, Guid id, IReadOnlyList<ProviderHealthIssue>? issues)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var provider = await database.ExternalProviders.SingleAsync(p => p.Id == id);
        provider.RecordHealthCheck(
            succeeded: true, message: "Reachable on periodic background check.",
            healthStatus: "Degraded", searchOperationStatus: "Degraded",
            acquireOperationStatus: "Available", checkedAtUtc: DateTimeOffset.UtcNow, issues);
        await database.SaveChangesAsync();
    }

    [TestMethod]
    public async Task ReadinessDetailUsesTheProvidersOwnReasonAndFallsBackWhenAbsent()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateAdminClientWithTokenAsync(fixture);
        var created = await CreateEnabledProviderAsync(client, "reasoned-provider");

        await RecordHealthAsync(fixture, created.Id,
            [new ProviderHealthIssue("search", "no-indexers", "No enabled indexer supports search.")]);
        var withReason = await client.GetFromJsonAsync<SystemReadinessResponse>("/api/v1/system/readiness");
        var component = withReason!.DegradedComponents.Single(c => c.Name == "reasoned-provider");
        Assert.AreEqual("Search: No enabled indexer supports search.", component.Detail);

        // Persistence round-trip through PostgreSQL, and the admin-only response contract.
        var listed = await client.GetFromJsonAsync<List<ExternalProviderResponse>>("/api/v1/admin/external-providers/");
        var issue = listed!.Single(p => p.Id == created.Id).CachedHealthIssues.Single();
        Assert.AreEqual("no-indexers", issue.Code);

        // A later probe reporting none clears it, and the generic text returns.
        await RecordHealthAsync(fixture, created.Id, null);
        var fallback = await client.GetFromJsonAsync<SystemReadinessResponse>("/api/v1/system/readiness");
        StringAssert.Contains(
            fallback!.DegradedComponents.Single(c => c.Name == "reasoned-provider").Detail, "Search is degraded.");
        listed = await client.GetFromJsonAsync<List<ExternalProviderResponse>>("/api/v1/admin/external-providers/");
        Assert.AreEqual(0, listed!.Single(p => p.Id == created.Id).CachedHealthIssues.Count);
    }

    private static async Task<HttpClient> CreateAdminClientWithTokenAsync(WebTestFixture fixture)
    {
        var client = await fixture.CreateAdminClientAsync();
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(client);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);
        return client;
    }
}
