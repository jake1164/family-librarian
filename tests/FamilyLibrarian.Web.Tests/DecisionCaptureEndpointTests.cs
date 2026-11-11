using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FamilyLibrarian.Contracts.Authentication;
using FamilyLibrarian.Contracts.ModelGateway;
using FamilyLibrarian.Domain.DecisionCapture;
using FamilyLibrarian.Domain.ModelGateway;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyLibrarian.Web.Tests;

/// <summary>A6-TUNER-C1-a over the real host: administrator-only, anti-forgery protected, range-checked, audited, and counts only.</summary>
[TestClass]
public sealed class DecisionCaptureEndpointTests
{
    private const string BasePath = "/api/v1/admin/model-gateway/capture";
    private const string Sentinel = "SENTINEL-AUTHOR-must-never-leave-the-database";
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static WebTestFixture? fixture;

    [ClassInitialize]
    public static async Task InitializeAsync(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        fixture = await WebTestFixture.CreateAsync();
    }

    [ClassCleanup]
    public static async Task CleanupAsync()
    {
        if (fixture is not null) await fixture.DisposeAsync();
    }

    private static FamilyLibrarianAppFactory Factory() => new(WebTestFixture.Require(fixture).ConnectionString);

    private static async Task<HttpClient> SignedInAsync(FamilyLibrarianAppFactory factory, string email, string password, bool token = true)
    {
        var http = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var login = await http.PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = email, Password = password });
        Assert.AreEqual(HttpStatusCode.NoContent, login.StatusCode);
        if (token) http.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(http));
        return http;
    }

    private static Task<HttpClient> AdminAsync(FamilyLibrarianAppFactory factory, bool token = true) =>
        SignedInAsync(factory, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword, token);

    [TestMethod]
    public async Task EveryRouteChallengesAnonymousForbidsMembersAndRequiresTheAntiforgeryTokenForStateChanges()
    {
        await using var factory = Factory();
        using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var member = await SignedInAsync(factory, WebTestFixture.UserEmail, WebTestFixture.UserPassword);
        using var noToken = await AdminAsync(factory, token: false);
        var save = new SaveDecisionCaptureSettingsRequest(false, true, new DecisionCaptureLimitsDto(100, 90, null, 5, 60));
        var limits = save.Limits!;

        foreach (var path in new[] { $"{BasePath}/", $"{BasePath}/insight" })
        {
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode, path);
            Assert.AreEqual(HttpStatusCode.Forbidden, (await member.GetAsync(path)).StatusCode, path);
        }

        Assert.AreEqual(HttpStatusCode.Forbidden, (await member.PutAsJsonAsync($"{BasePath}/", save)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync($"{BasePath}/preview", limits)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync($"{BasePath}/purge", new PurgeDecisionCaptureRequest(null))).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync($"{BasePath}/", save)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync($"{BasePath}/purge", new PurgeDecisionCaptureRequest(null))).StatusCode);

        Assert.AreEqual(HttpStatusCode.BadRequest, (await noToken.PutAsJsonAsync($"{BasePath}/", save)).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await noToken.PostAsJsonAsync($"{BasePath}/preview", limits)).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await noToken.PostAsJsonAsync($"{BasePath}/purge", new PurgeDecisionCaptureRequest(null))).StatusCode);
    }

    [TestMethod]
    public async Task ADefaultInstallationReportsDecisionsOnChatOffTheOwnersLimitsAndTheFixedRangesWithAiOff()
    {
        await using var factory = Factory();
        using var admin = await AdminAsync(factory);

        var settings = (await admin.GetFromJsonAsync<DecisionCaptureSettingsResponse>($"{BasePath}/"))!;

        Assert.IsTrue(settings.DecisionCaptureEnabled);
        Assert.IsFalse(settings.ChatCaptureEnabled);
        Assert.AreEqual(new DecisionCaptureLimitsDto(200, 180, null, 10, 180), settings.Limits);
        Assert.AreEqual(settings.Limits, settings.Defaults);
        Assert.AreEqual(50, settings.Ranges.MinSnapshotsPerRole);
        Assert.AreEqual(1_000, settings.Ranges.MaxSnapshotsPerRole);
        Assert.AreEqual(1_825, settings.Ranges.MaxLabeledSnapshotDays);
        Assert.AreEqual(32_768, settings.Ranges.SnapshotSizeGuardBytes);
        var gateway = (await admin.GetFromJsonAsync<ModelGatewaySettingsResponse>("/api/v1/admin/model-gateway/"))!;
        Assert.IsFalse(gateway.IsEnabled, "Capture settings are reachable while the AI master switch is off.");
    }

    [TestMethod]
    public async Task ACraftedOutOfRangeSaveOrPreviewIsRefusedAndNothingChanges()
    {
        await using var factory = Factory();
        using var admin = await AdminAsync(factory);
        var before = (await admin.GetFromJsonAsync<DecisionCaptureSettingsResponse>($"{BasePath}/"))!;

        foreach (var bad in new[]
        {
            before.Limits with { SnapshotsPerRole = 1_001 }, before.Limits with { SnapshotsPerRole = 49 },
            before.Limits with { UnlabeledSnapshotDays = 5 }, before.Limits with { LabeledSnapshotDays = 10 },
            before.Limits with { CallsPerSnapshot = 21 }, before.Limits with { FindingsDays = 29 }, before.Limits with { FindingsDays = int.MaxValue }
        })
        {
            using var save = await admin.PutAsJsonAsync($"{BasePath}/", new SaveDecisionCaptureSettingsRequest(false, true, bad));
            Assert.AreEqual(HttpStatusCode.BadRequest, save.StatusCode, JsonSerializer.Serialize(bad));
            using var preview = await admin.PostAsJsonAsync($"{BasePath}/preview", bad);
            Assert.AreEqual(HttpStatusCode.BadRequest, preview.StatusCode, JsonSerializer.Serialize(bad));
        }

        Assert.AreEqual(HttpStatusCode.BadRequest,
            (await admin.PutAsJsonAsync($"{BasePath}/", new SaveDecisionCaptureSettingsRequest(false, true, null))).StatusCode);
        var after = (await admin.GetFromJsonAsync<DecisionCaptureSettingsResponse>($"{BasePath}/"))!;
        Assert.AreEqual(before, after);
    }

    [TestMethod]
    public async Task SavingPersistsTheSwitchesAndLimitsAndAuditsOldAndNewValues()
    {
        await using var factory = Factory();
        using var admin = await AdminAsync(factory);
        var limits = new DecisionCaptureLimitsDto(120, 100, 400, 7, 45);

        using var saved = await admin.PutAsJsonAsync($"{BasePath}/", new SaveDecisionCaptureSettingsRequest(false, true, limits));

        Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
        var reloaded = (await admin.GetFromJsonAsync<DecisionCaptureSettingsResponse>($"{BasePath}/"))!;
        Assert.IsFalse(reloaded.DecisionCaptureEnabled);
        Assert.IsTrue(reloaded.ChatCaptureEnabled);
        Assert.AreEqual(limits, reloaded.Limits);
        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var all = database.AuditEvents.Where(a => a.Action == "decision_capture.settings_changed").AsEnumerable().Select(a => a.Detail!).ToList();
        var changed = all.Select(d => JsonDocument.Parse(d)).Last(d => d.RootElement.GetProperty("After").GetProperty("Limits").GetProperty("SnapshotsPerRole").GetInt32() == 120);
        Assert.AreEqual(200, changed.RootElement.GetProperty("Before").GetProperty("Limits").GetProperty("SnapshotsPerRole").GetInt32(), "The old value is recorded.");
        Assert.IsTrue(changed.RootElement.GetProperty("Before").GetProperty("DecisionCaptureEnabled").GetBoolean());
        Assert.IsFalse(changed.RootElement.GetProperty("After").GetProperty("DecisionCaptureEnabled").GetBoolean());

        // Put it back so other tests in this class see the defaults.
        await admin.PutAsJsonAsync($"{BasePath}/", new SaveDecisionCaptureSettingsRequest(true, false, reloaded.Defaults));
    }

    [TestMethod]
    public async Task InsightAndPreviewReturnNumbersOnlyAndNeverTheSavedContent()
    {
        await using var factory = Factory();
        using var admin = await AdminAsync(factory);
        Guid snapshotId;
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var snapshot = new DecisionSnapshot(ModelRole.CandidateTriage, 1, $"request:{Guid.NewGuid():N}", $"digest-{Guid.NewGuid():N}",
                $"{{\"author\":\"{Sentinel}\"}}", null, null, null, false, DateTimeOffset.UtcNow.AddDays(-400));
            database.DecisionSnapshots.Add(snapshot);
            await database.SaveChangesAsync();
            snapshotId = snapshot.Id;
        }

        var insightText = await admin.GetStringAsync($"{BasePath}/insight");
        using var preview = await admin.PostAsJsonAsync($"{BasePath}/preview", DecisionCaptureLimits.Default.ToDtoForTest());
        var previewText = await preview.Content.ReadAsStringAsync();

        Assert.IsFalse(insightText.Contains(Sentinel, StringComparison.Ordinal));
        Assert.IsFalse(previewText.Contains(Sentinel, StringComparison.Ordinal));
        Assert.IsFalse(insightText.Contains("snapshotJson", StringComparison.OrdinalIgnoreCase), "No field named like snapshot content exists in the response.");
        var insight = JsonSerializer.Deserialize<DecisionCaptureInsightResponse>(insightText, WebJson)!;
        Assert.IsTrue(insight.Roles.Single(role => role.Role == "CandidateTriage").Snapshots >= 1);
        Assert.IsTrue(insight.SizesAvailable);
        var counts = (await preview.Content.ReadFromJsonAsync<DecisionCaptureRetentionPreviewResponse>())!;
        Assert.IsTrue(counts.Snapshots >= 1, "The 400-day-old unlabeled snapshot is past the 180-day default.");

        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await database.DecisionSnapshots.Where(s => s.Id == snapshotId).ExecuteDeleteAsync();
        }
    }

    [TestMethod]
    public async Task PurgeIsScopedValidatedAuditedWithCountsAndWorksWithAiOff()
    {
        await using var factory = Factory();
        using var admin = await AdminAsync(factory);
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await database.DecisionSnapshots.ExecuteDeleteAsync();
            database.DecisionSnapshots.AddRange(
                new DecisionSnapshot(ModelRole.CandidateTriage, 1, "request:a", "d1", $"{{\"t\":\"{Sentinel}\"}}", null, null, null, false, DateTimeOffset.UtcNow),
                new DecisionSnapshot(ModelRole.SearchGuidance, 1, "request:b", "d2", "{}", null, null, null, false, DateTimeOffset.UtcNow));
            await database.SaveChangesAsync();
        }

        using var invalid = await admin.PostAsJsonAsync($"{BasePath}/purge", new PurgeDecisionCaptureRequest("VisionCoverIdentification"));
        using var unknown = await admin.PostAsJsonAsync($"{BasePath}/purge", new PurgeDecisionCaptureRequest("NotARole"));
        Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, unknown.StatusCode);

        using var one = await admin.PostAsJsonAsync($"{BasePath}/purge", new PurgeDecisionCaptureRequest("CandidateTriage"));
        Assert.AreEqual(HttpStatusCode.OK, one.StatusCode, await one.Content.ReadAsStringAsync());
        Assert.AreEqual(1, (await one.Content.ReadFromJsonAsync<DecisionCapturePurgeResponse>())!.Snapshots);
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.AreEqual(ModelRole.SearchGuidance, (await database.DecisionSnapshots.SingleAsync()).Role);
            var audits = database.AuditEvents.Where(a => a.Action == "decision_capture.purged").AsEnumerable().Select(a => a.Detail!).ToList();
            Assert.IsTrue(audits.Select(d => JsonDocument.Parse(d).RootElement).Any(d =>
                d.GetProperty("Role").GetString() == "CandidateTriage" && d.GetProperty("Snapshots").GetInt32() == 1), string.Join(" | ", audits));
            Assert.IsFalse(audits.Any(d => d.Contains(Sentinel, StringComparison.Ordinal)), "The audit holds counts, never content.");
        }

        using var all = await admin.PostAsJsonAsync($"{BasePath}/purge", new PurgeDecisionCaptureRequest(null));
        Assert.AreEqual(1, (await all.Content.ReadFromJsonAsync<DecisionCapturePurgeResponse>())!.Snapshots);
    }
}

internal static class DecisionCaptureLimitsTestExtensions
{
    public static DecisionCaptureLimitsDto ToDtoForTest(this DecisionCaptureLimits limits) =>
        new(limits.SnapshotsPerRole, limits.UnlabeledSnapshotDays, limits.LabeledSnapshotDays, limits.CallsPerSnapshot, limits.FindingsDays);
}
