using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Operations;
using FamilyLibrarian.Contracts.Providers;
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

    private static async Task<HttpClient> CreateAdminClientWithTokenAsync(WebTestFixture fixture)
    {
        var client = await fixture.CreateAdminClientAsync();
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(client);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);
        return client;
    }
}
