using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyLibrarian.Web.Tests;

/// <summary>Protects the persisted state used by the Sources page's catalogue-operation progress display.</summary>
[TestClass]
public sealed class GutenbergCatalogEndpointTests
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
    public async Task AnInProgressCatalogueOperationReportsProgressAndPreventsAnotherOperation()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await SeedPurgingStatusAsync(fixture);
        using var client = await CreateAdminClientWithTokenAsync(fixture);

        var status = await client.GetFromJsonAsync<GutenbergCatalogStatusResponse>("/api/v1/admin/gutenberg/status");
        Assert.IsNotNull(status);
        Assert.AreEqual("Purging", status.Status);
        Assert.AreEqual(1_000, status.InProgressBookCount);
        Assert.AreEqual(2_500, status.InProgressFormatCount);
        Assert.IsNotNull(status.LastProgressUtc);

        var refresh = await client.PostAsync("/api/v1/admin/gutenberg/sync", content: null);
        var purge = await client.DeleteAsync("/api/v1/admin/gutenberg/catalog");

        Assert.AreEqual(HttpStatusCode.Conflict, refresh.StatusCode);
        Assert.AreEqual(HttpStatusCode.Conflict, purge.StatusCode);
    }

    private static async Task<HttpClient> CreateAdminClientWithTokenAsync(WebTestFixture fixture)
    {
        var client = await fixture.CreateAdminClientAsync();
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(client);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);
        return client;
    }

    private static async Task SeedPurgingStatusAsync(WebTestFixture fixture)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await database.Database.ExecuteSqlAsync($"""
            INSERT INTO gutenberg.catalog_sync_states
                (id, book_count, format_count, in_progress_book_count, in_progress_format_count, last_progress_utc, parse_error_count, status)
            VALUES ('gutenberg', 0, 0, 1000, 2500, NOW(), 0, 'Purging')
            ON CONFLICT (id) DO UPDATE SET
                in_progress_book_count = 1000,
                in_progress_format_count = 2500,
                last_progress_utc = NOW(),
                status = 'Purging';
            """);
    }

    private sealed record GutenbergCatalogStatusResponse(
        string Status,
        int InProgressBookCount,
        int InProgressFormatCount,
        DateTimeOffset? LastProgressUtc);
}
