using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Contracts.Providers;
using FamilyLibrarian.Contracts.Requests;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Catalog;
using FamilyLibrarian.Domain.Providers;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using FamilyLibrarian.Web.Client.Providers;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;

namespace FamilyLibrarian.Web.Tests;

[TestClass]
public sealed class ExternalProviderConfidenceLevelEndpointTests
{
    [TestMethod]
    public async Task NewSourcesAreStrictReviewedAndSavingLevelsIsAudited()
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        using var admin = await fixture.CreateAdminClientAsync();
        admin.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(admin));
        var response = await admin.PostAsJsonAsync("/api/v1/admin/external-providers/", new CreateExternalProviderRequest("example-source", "Example source", "http://example.test"));
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.IsTrue(created.TryGetProperty("confidenceLevel", out var level), "The admin status must report its confidence level.");
        Assert.AreEqual("Strict", level.GetString());
        Assert.IsTrue(created.GetProperty("confidenceReviewed").GetBoolean());
        var id = created.GetProperty("id").GetGuid();
        var saved = await admin.PutAsJsonAsync($"/api/v1/admin/external-providers/{id}/confidence-level", new { level = "Standard", ebook = "Liberal", audiobook = "Strict" });
        Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode);
        var status = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual("Standard", status.GetProperty("confidenceLevel").GetString());
        Assert.AreEqual("Liberal", status.GetProperty("ebookConfidenceLevel").GetString());
        Assert.AreEqual("Strict", status.GetProperty("audiobookConfidenceLevel").GetString());
        await using var scope = fixture.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var audit = await database.AuditEvents.SingleAsync(row => row.Action == "external_provider.confidence_levels_changed");
        Assert.AreEqual(id.ToString(), audit.SubjectId);
        Assert.IsNotNull(audit.ActorUserId);
        StringAssert.Contains(audit.Detail!, "Standard");
        StringAssert.Contains(audit.Detail!, "Liberal");
        StringAssert.Contains(audit.Detail!, "Strict");
    }

    [TestMethod]
    [DataRow("user", 403)]
    [DataRow("anonymous", 401)]
    [DataRow("no-token", 400)]
    [DataRow("invalid-level", 400)]
    [DataRow("invalid-ebook", 400)]
    [DataRow("invalid-audiobook", 400)]
    public async Task ConfidenceEndpointRequiresAdminAntiforgeryAndDefinedLevels(string scenario, int expected)
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        using var client = scenario == "user" ? await fixture.CreateUserClientAsync() :
            scenario == "anonymous" ? fixture.CreateAnonymousClient() : await fixture.CreateAdminClientAsync();
        if (scenario is not "no-token" and not "anonymous")
            client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(client));
        var response = await client.PutAsJsonAsync($"/api/v1/admin/external-providers/{Guid.NewGuid()}/confidence-level",
            new { level = scenario == "invalid-level" ? "0" : "Standard", ebook = scenario == "invalid-ebook" ? "Unknown" : null,
                audiobook = scenario == "invalid-audiobook" ? "99" : null });
        Assert.AreEqual((HttpStatusCode)expected, response.StatusCode);
    }

    [TestMethod]
    [DataRow("Rebeca Yaross", true, "Strict", "Ebook", null, true)]
    [DataRow("Rebeca Yaros", false, "Strict", "Ebook", null, false)]
    [DataRow("Rebeca Yaros", false, "Standard", "Ebook", null, true)]
    [DataRow(null, false, "Strict", "Ebook", null, false)]
    [DataRow(null, false, "Standard", "Ebook", null, false)]
    [DataRow(null, false, "Liberal", "Ebook", null, true)]
    [DataRow(null, true, "Strict", "Ebook", null, false)]
    [DataRow(null, true, "Standard", "Ebook", null, true)]
    [DataRow("Yarros", false, "Standard", "Ebook", null, false)]
    [DataRow("Yarros", false, "Liberal", "Ebook", null, true)]
    [DataRow("Yarros", true, "Standard", "Ebook", null, true)]
    [DataRow("Rebecca", false, "Standard", "Ebook", null, false)]
    [DataRow("Rebecca", false, "Liberal", "Ebook", null, true)]
    [DataRow("Rebecca", true, "Standard", "Ebook", null, true)]
    [DataRow("Rebeca Yaros", false, "Standard", "Audiobook", "Strict", false)]
    [DataRow("Rebeca Yaros", false, "Standard", "Audiobook", null, true)]
    [DataRow("Rebecca Yarros", false, "SuggestOnly", "Ebook", null, false)]
    [DataRow("Rebecca Yarros", false, "Strict", "Audiobook", "SuggestOnly", false)]
    [DataRow("Rebecca Yarros", false, "SuggestOnly", "Audiobook", "Strict", true)]
    public async Task ScheduledSelectionHonorsLevelAndFormatOverride(string? author, bool agreement, string level, string mediaType, string? audioOverride, bool fetched)
    {
        var title = author == "Rebeca Yaross" ? "Iron Flame" : "Fourth Wing";
        var position = title == "Iron Flame" ? "2" : "1";
        var candidate = ExternalProviderCandidate.FromSimple("example-record", title, author, mediaType == "Ebook" ? "epub" : "m4b", 1000);
        if (agreement) candidate = candidate with { Work = candidate.Work with { Series = [new("The Empyrean", position)] } };
        var submitted = await ConfidenceScenario.RunAsync([candidate], title, position, level, mediaType, audioOverride,
            inspect: async database => Assert.AreEqual(fetched ? ProviderAttemptOutcome.Submitted : ProviderAttemptOutcome.CandidatesFound,
                (await database.ProviderAttempts.SingleAsync()).Outcome,
                "An ineligible candidate must go directly to review, without spending an automatic attempt."));
        Assert.AreEqual(fetched ? 1 : 0, submitted.Count, "The provider level must gate the candidate tier on the scheduled path.");
        if (fetched) Assert.AreEqual("example-record", submitted.Single());
    }

    [TestMethod]
    public async Task SuggestOnlyPersistsAtEveryLevelAndALibrarianCanChooseAStrongCopy()
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        var client = new VariantSearchProviderClient(_ => [VariantSearchProviderClient.Hobbit("reviewed-copy", withAuthor: true)]);
        await VariantScenario.RunAsync(fixture, client, "suggest-only-source", providerConfidence: "SuggestOnly",
            afterwards: async (factory, admin, request) =>
            {
                Assert.IsEmpty(client.SubmittedCandidates, "Even a strong match waits for a librarian.");
                await using var scope = factory.Services.CreateAsyncScope();
                var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var provider = await database.ExternalProviders.SingleAsync();
                var saved = await admin.PutAsJsonAsync($"/api/v1/admin/external-providers/{provider.Id}/confidence-level",
                    new { level = "SuggestOnly", ebook = "SuggestOnly", audiobook = "SuggestOnly" });
                Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode);
                var status = await saved.Content.ReadFromJsonAsync<ExternalProviderResponse>();
                Assert.IsNotNull(status);
                Assert.AreEqual("SuggestOnly", status.ConfidenceLevel);
                Assert.AreEqual("SuggestOnly", status.EbookConfidenceLevel);
                Assert.AreEqual("SuggestOnly", status.AudiobookConfidenceLevel);
                var candidate = await database.RequestReviewCandidates.SingleAsync(row => row.RequestId == request.Id);
                var current = await database.BookRequests.Include(row => row.StatusHistory).SingleAsync(row => row.Id == request.Id);
                StringAssert.Contains(current.StatusHistory.Single(row => row.ToStatus == RequestStatus.NeedsReview).Reason!, "suggest only");
                var pick = await admin.PostAsJsonAsync($"/api/v1/admin/requests/{request.Id}/needs-review/resolve",
                    new ResolveNeedsReviewRequest(candidate.Id, current.Version));
                Assert.IsTrue(pick.IsSuccessStatusCode, pick.StatusCode.ToString());
            });
        CollectionAssert.AreEqual(new List<string> { "reviewed-copy" }, client.SubmittedCandidates);
    }

    [TestMethod]
    public async Task MigratedSourcesDefaultToStandardAndNoticeClearsOnlyAfterEveryEnabledSourceIsReviewed()
    {
        if (PostgresFixture.UnavailableReason is not null) Assert.Inconclusive(PostgresFixture.UnavailableReason);
        var connection = await PostgresFixture.CreateMigratedDatabaseAsync(targetMigration: "20261014230928_AddAssetRuledOut");
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection,
            npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "identity")).Options;
        await using (var database = new AppDbContext(options))
        {
            // Insert historical rows without referencing new model columns.
            await database.Database.ExecuteSqlRawAsync("""
                INSERT INTO providers.external_providers
                    (id, provider_id, display_name, base_url, is_enabled, recheck_schedule, auto_acquire_enabled,
                     automatic_attempt_limit, api_key_format_version, instance_replaced_since_previous_test, created_at_utc, updated_at_utc)
                VALUES ('10000000-0000-0000-0000-000000000001', 'example-source', 'Example source', 'http://example.test', true, 'Manual', false, 3, 0, false, now(), now()),
                       ('10000000-0000-0000-0000-000000000002', 'example-library', 'Example library', 'http://example.test', true, 'Manual', false, 3, 0, false, now(), now())
                """);
            await database.Database.MigrateAsync();
        }
        await using var factory = new FamilyLibrarianAppFactory(connection);
        using var admin = factory.CreateClient();
        await ConfidenceScenario.SignInAsync(admin);
        async Task<ExternalProviderResponse[]> ListAsync() => (await admin.GetFromJsonAsync<ExternalProviderResponse[]>("/api/v1/admin/external-providers/"))!;
        var providers = await ListAsync();
        Assert.IsTrue(providers.All(provider => provider.RecheckSchedule == "Manual"),
            "Removing the switch and changing the creation default must preserve existing saved schedules.");
        var raw = await admin.GetFromJsonAsync<JsonElement>("/api/v1/admin/external-providers/");
        Assert.IsTrue(raw[0].TryGetProperty("confidenceLevel", out var level), "Migrated sources must expose their default level.");
        Assert.AreEqual("Standard", level.GetString());
        Assert.IsFalse(raw[0].GetProperty("confidenceReviewed").GetBoolean());
        StringAssert.Contains(await RenderNoticeAsync(providers), "Review the download confidence for each source");
        foreach (var provider in providers)
        {
            (await admin.PutAsJsonAsync($"/api/v1/admin/external-providers/{provider.Id}/confidence-level",
                new { level = "Standard", ebook = (string?)null, audiobook = (string?)null })).EnsureSuccessStatusCode();
            var html = await RenderNoticeAsync(await ListAsync());
            if (provider == providers[0]) StringAssert.Contains(html, "Review the download confidence for each source");
            else Assert.IsFalse(html.Contains("Review the download confidence for each source", StringComparison.Ordinal));
        }
    }

    private static async Task<string> RenderNoticeAsync(ExternalProviderResponse[] providers)
    {
        await using var services = new ServiceCollection().AddLogging().AddMudServices().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);
        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<ProviderConfidenceReviewNotice>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Providers"] = providers }))).ToHtmlString());
    }
}

internal static class ConfidenceScenario
{
    internal static async Task SignInAsync(HttpClient admin)
    {
        (await admin.PostAsJsonAsync("/api/auth/login", new FamilyLibrarian.Contracts.Authentication.LoginRequest
        { Email = FamilyLibrarianAppFactory.AdminEmail, Password = FamilyLibrarianAppFactory.AdminPassword })).EnsureSuccessStatusCode();
        admin.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(admin));
    }

    internal static async Task<IReadOnlyList<string>> RunAsync(IReadOnlyList<ExternalProviderCandidate> candidates, string title, string position,
        string level, string mediaType = "Ebook", string? audioOverride = null,
        Func<AppDbContext, Task>? inspect = null,
        Func<int, ExternalProviderSearchRequest, IReadOnlyList<ExternalProviderCandidate>>? searchScript = null)
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        var client = new ConfidenceSearchClient(candidates, searchScript);
        await using var factory = new FamilyLibrarianAppFactory(fixture.ConnectionString, services =>
        { services.RemoveAll<IExternalProviderClient>(); services.AddSingleton<IExternalProviderClient>(client); });
        using var admin = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await SignInAsync(admin);
        var provider = await (await admin.PostAsJsonAsync("/api/v1/admin/external-providers/",
            new CreateExternalProviderRequest("example-source", "Example source", "http://example.test"))).Content.ReadFromJsonAsync<ExternalProviderResponse>();
        Assert.IsNotNull(provider);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var configured = await database.ExternalProviders.SingleAsync(row => row.Id == provider.Id);
            configured.SetEnabled(true, null, DateTimeOffset.UtcNow);
            configured.SetRecheckSchedule(ProviderRecheckSchedule.Daily, null, DateTimeOffset.UtcNow);
            configured.SetConfidenceLevels(Enum.Parse<ProviderConfidenceLevel>(level), null,
                audioOverride is null ? null : Enum.Parse<ProviderConfidenceLevel>(audioOverride), null, DateTimeOffset.UtcNow);
            var work = new Work(title, null, null, null, PublicationStatus.Published, DateTimeOffset.UtcNow);
            work.AddAuthor(new Author("Rebecca Yarros", null, DateTimeOffset.UtcNow), 0);
            work.AddSeriesEntry(new SeriesEntry(new Series("The Empyrean", SeriesStatus.Active, DateTimeOffset.UtcNow), work, position, null, true, DateTimeOffset.UtcNow));
            database.Works.Add(work);
            await database.SaveChangesAsync();
            (await admin.PostAsJsonAsync("/api/v1/requests/", new CreateBookRequestRequest(work.Id, [mediaType], null, false, false))).EnsureSuccessStatusCode();
        }
        await using (var scope = factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ExternalProviderRecheckService>().ProcessDueAsync(CancellationToken.None);
        if (inspect is not null)
        {
            await using var inspection = factory.Services.CreateAsyncScope();
            await inspect(inspection.ServiceProvider.GetRequiredService<AppDbContext>());
        }
        return client.SubmittedCandidates.ToArray();
    }
}

internal sealed class ConfidenceSearchClient(IReadOnlyList<ExternalProviderCandidate> candidates,
    Func<int, ExternalProviderSearchRequest, IReadOnlyList<ExternalProviderCandidate>>? searchScript = null) : IExternalProviderClient
{
    private readonly VariantSearchProviderClient _submission = new(_ => []);
    private int _searchCount;
    public IReadOnlyList<string> SubmittedCandidates => _submission.SubmittedCandidates;

    public Task<ExternalProviderManifest> GetManifestAsync(string baseUrl, string? apiKey, CancellationToken cancellationToken) =>
        _submission.GetManifestAsync(baseUrl, apiKey, cancellationToken);
    public Task<ExternalProviderHealth> GetHealthAsync(string baseUrl, string? apiKey, CancellationToken cancellationToken) =>
        _submission.GetHealthAsync(baseUrl, apiKey, cancellationToken);
    public Task<IReadOnlyList<ExternalProviderCandidate>> SearchAsync(string baseUrl, string? apiKey, ExternalProviderSearchRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(searchScript?.Invoke(Interlocked.Increment(ref _searchCount), request) ?? candidates);
    public Task<ExternalProviderAcquireSubmission> SubmitAcquireAsync(string baseUrl, string? apiKey, ExternalAcquireRequest request, string idempotencyKey, CancellationToken cancellationToken) =>
        _submission.SubmitAcquireAsync(baseUrl, apiKey, request, idempotencyKey, cancellationToken);
    public Task<ExternalProviderArtifact> AcquireAsync(string baseUrl, string? apiKey, string candidateReference, RequestMediaType mediaType, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<ExternalProviderJobStatus> GetAcquireStatusAsync(string baseUrl, string? apiKey, string jobId, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<IReadOnlyList<ExternalProviderOutput>> ListOutputsAsync(string baseUrl, string? apiKey, string jobId, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<ExternalProviderArtifact> GetOutputAsync(string baseUrl, string? apiKey, string jobId, string outputId, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task CancelAcquireAsync(string baseUrl, string? apiKey, string jobId, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task DeleteAcquireAsync(string baseUrl, string? apiKey, string jobId, CancellationToken cancellationToken) => Task.CompletedTask;
}
