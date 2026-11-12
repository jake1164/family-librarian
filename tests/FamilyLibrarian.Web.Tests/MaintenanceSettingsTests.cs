using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Application.Storage;
using FamilyLibrarian.Contracts.Acquisition;
using FamilyLibrarian.Contracts.Authentication;
using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Contracts.Requests;
using FamilyLibrarian.Contracts.Storage;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Audit;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Client.Authentication;
using FamilyLibrarian.Web.Client.Pages.Admin;
using FamilyLibrarian.Web.Client.Storage;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using MudBlazor.Services;

namespace FamilyLibrarian.Web.Tests;

/// <summary>Clean-up settings by UI: saved values are enforced and used, bad ones are refused with reasons, defaults come back, and the page says what it means.</summary>
[TestClass]
public sealed class MaintenanceSettingsTests
{
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

    private const string Path = "/api/v1/admin/storage/settings";

    private static async Task<HttpClient> SignedInAsync(FamilyLibrarianAppFactory factory, string email, string password)
    {
        var http = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (await http.PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = email, Password = password })).EnsureSuccessStatusCode();
        http.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(http));
        return http;
    }

    private static FamilyLibrarianAppFactory NewFactory() => new(WebTestFixture.Require(fixture).ConnectionString);

    private static async Task ResetAsync(HttpClient admin) => (await admin.DeleteAsync(Path)).EnsureSuccessStatusCode();

    [TestMethod]
    public async Task OnlyAdministratorsSeeAndChangeTheSettingsAndTheyStartAtTheDefaultsWithTheirLimits()
    {
        await using var factory = NewFactory();
        using var admin = await SignedInAsync(factory, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword);
        using var member = await SignedInAsync(factory, WebTestFixture.UserEmail, WebTestFixture.UserPassword);
        using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await ResetAsync(admin);

        Assert.AreEqual(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Path)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await member.GetAsync(Path)).StatusCode);
        var defaults = (await admin.GetFromJsonAsync<MaintenanceSettingsResponse>(Path))!;
        Assert.AreEqual(HttpStatusCode.Forbidden, (await member.PutAsJsonAsync(Path, defaults.Values)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await member.DeleteAsync(Path)).StatusCode);

        Assert.IsFalse(defaults.IsCustomized);
        Assert.AreEqual(14, defaults.Values.RejectedDays);
        Assert.AreEqual(30, defaults.Values.WaitingEbookDays);
        Assert.AreEqual(14, defaults.Values.WaitingAudiobookDays);
        Assert.AreEqual(10, defaults.Values.HeldCapGb);
        Assert.AreEqual(730, defaults.Values.AuditDays);
        Assert.AreEqual(30, defaults.Values.UpgradeScanIntervalDays);
        Assert.AreEqual(defaults.Values, defaults.Defaults);
        foreach (var key in new[] { "rejectedDays", "waitingEbookDays", "heldCapGb", "auditDays", "upgradeScanIntervalDays", "orphanGraceHours", "pressureDays" })
            Assert.IsTrue(defaults.Limits.ContainsKey(key), "The screen is told every limit: " + key);
        Assert.AreEqual(365, defaults.Limits["auditDays"].Min, "The audit log is never kept for under a year.");
        Assert.AreEqual(7, defaults.Limits["waitingEbookDays"].Min);
    }

    [TestMethod]
    public async Task ValidValuesAreSavedAuditedUsedByTheWorkersAndRestoredByReset()
    {
        await using var factory = NewFactory();
        using var admin = await SignedInAsync(factory, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword);
        await ResetAsync(admin);
        var current = (await admin.GetFromJsonAsync<MaintenanceSettingsResponse>(Path))!.Values;

        var saved = await admin.PutAsJsonAsync(Path, current with { WaitingEbookDays = 45, WaitingAudiobookDays = 20, HeldCapGb = 25, AuditDays = 1000, UpgradeScanIntervalDays = 60, RejectedDays = 3 });

        Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode);
        var view = (await saved.Content.ReadFromJsonAsync<MaintenanceSettingsResponse>())!;
        Assert.IsTrue(view.IsCustomized);
        Assert.AreEqual(45, view.Values.WaitingEbookDays);
        Assert.AreEqual(14, view.Defaults.RejectedDays, "The defaults are still shown for comparison.");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var effective = await scope.ServiceProvider.GetRequiredService<MaintenanceSettingsService>().EffectiveAsync(CancellationToken.None);
            Assert.AreEqual(45, effective.Storage.WaitingEbookDays, "The background workers run with the saved values.");
            Assert.AreEqual(3, effective.Storage.RejectedDays);
            Assert.AreEqual(25L * StorageRetentionOptions.Gigabyte, effective.Storage.HeldBytesCap);
            Assert.AreEqual(1000, effective.Database.AuditDays);
            Assert.AreEqual(60, effective.Upgrades.ScanIntervalDays);
            var audits = await scope.ServiceProvider.GetRequiredService<AppDbContext>().AuditEvents.AsNoTracking().Where(a => a.Action == AuditActions.MaintenanceSettingsChanged).ToListAsync();
            Assert.IsTrue(audits.Any(a => a.Detail!.Contains("1000", StringComparison.Ordinal)), "The change is audited with the values.");
        }

        var reset = (await (await admin.DeleteAsync(Path)).Content.ReadFromJsonAsync<MaintenanceSettingsResponse>())!;
        Assert.IsFalse(reset.IsCustomized);
        Assert.AreEqual(30, reset.Values.WaitingEbookDays);
        await using var after = factory.Services.CreateAsyncScope();
        Assert.AreEqual(30, (await after.ServiceProvider.GetRequiredService<MaintenanceSettingsService>().EffectiveAsync(CancellationToken.None)).Storage.WaitingEbookDays, "Back to the defaults.");
    }

    [TestMethod]
    public async Task ValuesOutsideTheLimitsAreRefusedWithEveryReasonAndNothingIsSaved()
    {
        await using var factory = NewFactory();
        using var admin = await SignedInAsync(factory, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword);
        await ResetAsync(admin);
        var current = (await admin.GetFromJsonAsync<MaintenanceSettingsResponse>(Path))!.Values;

        var refused = await admin.PutAsJsonAsync(Path, current with { AuditDays = 30, WaitingEbookDays = 1, HeldCapGb = 5000, RejectedDays = 15 });

        Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode);
        var problems = (await refused.Content.ReadFromJsonAsync<MaintenanceProblemsResponse>())!.Problems;
        Assert.HasCount(3, problems, "Every bad value is listed, a good one is not.");
        Assert.IsTrue(problems.Any(p => p.Contains("Audit log", StringComparison.Ordinal) && p.Contains("365", StringComparison.Ordinal)));
        Assert.IsTrue(problems.Any(p => p.Contains("Held ebooks", StringComparison.Ordinal) && p.Contains('7')));
        Assert.IsTrue(problems.Any(p => p.Contains("size limit", StringComparison.Ordinal)));
        var unchanged = (await admin.GetFromJsonAsync<MaintenanceSettingsResponse>(Path))!;
        Assert.IsFalse(unchanged.IsCustomized, "Nothing was saved, not even the valid value.");
        Assert.AreEqual(14, unchanged.Values.RejectedDays);
    }

    [TestMethod]
    public async Task RecordingARunDoesNotFreezeTheDefaultsAndTheScreenShowsWhenItRan()
    {
        await using var factory = NewFactory();
        using var admin = await SignedInAsync(factory, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword);
        await ResetAsync(admin);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<MaintenanceSettingsService>();
            await service.RecordStorageRunAsync(7, 1, CancellationToken.None);
            await service.RecordDatabaseRunAsync(120, 0, CancellationToken.None);
            await service.RecordUpgradeRunAsync(2, 0, CancellationToken.None);
        }

        var view = (await admin.GetFromJsonAsync<MaintenanceSettingsResponse>(Path))!;
        Assert.IsFalse(view.IsCustomized, "Remembering a run is not customising.");
        Assert.AreEqual(7, view.Storage.Count);
        Assert.AreEqual(1, view.Storage.Failures);
        Assert.IsNotNull(view.Storage.LastRunAtUtc);
        Assert.AreEqual(view.Storage.LastRunAtUtc!.Value.AddHours(6), view.Storage.NextRunAtUtc);
        Assert.AreEqual(view.Database.LastRunAtUtc!.Value.AddHours(24), view.Database.NextRunAtUtc);
        Assert.AreEqual(120, view.Database.Count);
        await using var after = factory.Services.CreateAsyncScope();
        var effective = await after.ServiceProvider.GetRequiredService<MaintenanceSettingsService>().EffectiveAsync(CancellationToken.None);
        Assert.AreEqual(new StorageRetentionOptions().WaitingEbookDays, effective.Storage.WaitingEbookDays, "The configured defaults still apply.");
        // Later the operator changes the configured default: a row that only remembers when clean-up ran must not hold the old one.
        await using (var changed = factory.Services.CreateAsyncScope())
        {
            var service = ActivatorUtilities.CreateInstance<MaintenanceSettingsService>(changed.ServiceProvider, new StorageRetentionOptions { WaitingEbookDays = 40 });
            Assert.AreEqual(40, (await service.EffectiveAsync(CancellationToken.None)).Storage.WaitingEbookDays, "The new configured default applies, not the one in force when the row was created.");
        }

        // Saving then resetting keeps the memory of when clean-up last ran.
        (await admin.PutAsJsonAsync(Path, view.Values with { RejectedDays = 5 })).EnsureSuccessStatusCode();
        var afterReset = (await (await admin.DeleteAsync(Path)).Content.ReadFromJsonAsync<MaintenanceSettingsResponse>())!;
        Assert.AreEqual(7, afterReset.Storage.Count);
    }

    // ---- removal dates --------------------------------------------------------------------------------------------------------------

    private static async Task<(Guid WorkId, Guid FormatId)> SeedRequestAsync(FamilyLibrarianAppFactory factory, HttpClient admin)
    {
        var resolve = await admin.PostAsync("/api/v1/catalog/candidates/demo/the-hobbit/resolve", content: null);
        var work = (await resolve.Content.ReadFromJsonAsync<CatalogWorkResponse>())!;
        var created = await admin.PostAsJsonAsync("/api/v1/requests/", new CreateBookRequestRequest(await WebTestFixture.Require(fixture).CopyWorkForTestAsync(work.Id), ["Ebook"], null, false, false));
        var request = (await created.Content.ReadFromJsonAsync<BookRequestResponse>())!;
        await using var scope = factory.Services.CreateAsyncScope();
        var workId = (await scope.ServiceProvider.GetRequiredService<AppDbContext>().BookRequests.AsNoTracking().SingleAsync(r => r.Id == request.Id)).WorkId;
        return (workId, request.Formats.Single().FormatId);
    }

    private static async Task<Guid> AddAssetAsync(FamilyLibrarianAppFactory factory, Guid workId, Guid formatId, MediaAssetStorageState state, int ageDays)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;
        var asset = new MediaAsset(workId, null, RequestMediaType.Ebook, ".epub", "book.epub", "stored-" + Guid.NewGuid().ToString("N"), 10,
            new string('e', 64), "application/epub+zip", formatId, null, now);
        asset.TransitionStorageState(MediaAssetStorageState.Processing, now);
        if (state != MediaAssetStorageState.Processing) asset.TransitionStorageState(state, now);
        db.MediaAssets.Add(asset);
        await db.SaveChangesAsync();
        await db.MediaAssets.Where(a => a.Id == asset.Id).ExecuteUpdateAsync(set => set.SetProperty(a => a.UpdatedAtUtc, now.AddDays(-ageDays)));
        return asset.Id;
    }

    [TestMethod]
    public async Task TheSecurityQueueAndStoragePageSayWhenAHeldFileWillBeRemovedUsingTheSavedPeriods()
    {
        await using var factory = NewFactory();
        using var admin = await SignedInAsync(factory, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword);
        await ResetAsync(admin);
        var (workId, formatId) = await SeedRequestAsync(factory, admin);
        var held = await AddAssetAsync(factory, workId, formatId, MediaAssetStorageState.Unmatched, ageDays: 10);

        var listed = (await admin.GetFromJsonAsync<MediaAssetAdminListResponse>("/api/v1/admin/media-assets/"))!.Assets;

        var heldRow = listed.Single(a => a.AssetId == held);
        Assert.IsNotNull(heldRow.RemovalDateUtc);
        Assert.IsTrue(Math.Abs((heldRow.RemovalDateUtc!.Value - DateTimeOffset.UtcNow.AddDays(20)).TotalHours) < 2, "A held ebook waiting for a person: 30 days from when it was held.");

        var current = (await admin.GetFromJsonAsync<MaintenanceSettingsResponse>(Path))!.Values;
        (await admin.PutAsJsonAsync(Path, current with { WaitingEbookDays = 60 })).EnsureSuccessStatusCode();
        var longer = (await admin.GetFromJsonAsync<MediaAssetAdminListResponse>("/api/v1/admin/media-assets/"))!.Assets.Single(a => a.AssetId == held);
        Assert.IsTrue(Math.Abs((longer.RemovalDateUtc!.Value - DateTimeOffset.UtcNow.AddDays(50)).TotalHours) < 2, "The date follows the saved setting at once.");

        var storage = (await admin.GetFromJsonAsync<StorageInsightResponse>("/api/v1/admin/storage/"))!;
        Assert.IsTrue(storage.HeldFiles.Any(g => g is { State: "Unmatched", RuledOut: false } && g.OldestRemovalDateUtc is not null));
        await ResetAsync(admin);
    }

    [TestMethod]
    public async Task TheBackgroundCycleRunsWithTheSavedValuesAndRemembersWhenItRan()
    {
        await using var factory = NewFactory();
        using var admin = await SignedInAsync(factory, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword);
        await ResetAsync(admin);
        var (workId, formatId) = await SeedRequestAsync(factory, admin);
        var rejected = await AddAssetAsync(factory, workId, formatId, MediaAssetStorageState.Rejected, ageDays: 5);
        await using (var seed = factory.Services.CreateAsyncScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<AppDbContext>();
            db.AuditEvents.Add(new AuditEvent("test.cycle_old", "test", "x", null, null, null, DateTimeOffset.UtcNow.AddDays(-400)));
            await db.SaveChangesAsync();
        }

        async Task<MediaAssetStorageState> StateAsync()
        {
            await using var scope = factory.Services.CreateAsyncScope();
            return (await scope.ServiceProvider.GetRequiredService<AppDbContext>().MediaAssets.AsNoTracking().SingleAsync(a => a.Id == rejected)).StorageState;
        }

        async Task<bool> AuditExistsAsync()
        {
            await using var scope = factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<AppDbContext>().AuditEvents.AnyAsync(a => a.Action == "test.cycle_old");
        }

        await using (var defaults = factory.Services.CreateAsyncScope())
        {
            var cycle = defaults.ServiceProvider.GetRequiredService<MaintenanceCycle>();
            await cycle.RunStorageAsync(CancellationToken.None);
            await cycle.RunDatabaseAsync(CancellationToken.None);
        }

        Assert.AreEqual(MediaAssetStorageState.Rejected, await StateAsync(), "Default: a rejected file is kept 14 days, so a 5-day-old one stays.");
        Assert.IsTrue(await AuditExistsAsync(), "Default: the audit log keeps two years.");

        var current = (await admin.GetFromJsonAsync<MaintenanceSettingsResponse>(Path))!.Values;
        (await admin.PutAsJsonAsync(Path, current with { RejectedDays = 3, AuditDays = 365 })).EnsureSuccessStatusCode();
        await using (var custom = factory.Services.CreateAsyncScope())
        {
            var cycle = custom.ServiceProvider.GetRequiredService<MaintenanceCycle>();
            var storage = await cycle.RunStorageAsync(CancellationToken.None);
            var database = await cycle.RunDatabaseAsync(CancellationToken.None);
            Assert.IsGreaterThanOrEqualTo(1, storage.RemovedRejected, $"report: removed={storage.TotalRemoved} failures={storage.Failures} pressure={storage.UnderDiskPressure}");
            Assert.IsGreaterThanOrEqualTo(1, database.Removed[RetentionTarget.AuditEvents]);
        }

        Assert.AreEqual(MediaAssetStorageState.Destroyed, await StateAsync(), "The saved 3 days applied on the next pass.");
        Assert.IsFalse(await AuditExistsAsync(), "The saved audit period applied too.");
        var view = (await admin.GetFromJsonAsync<MaintenanceSettingsResponse>(Path))!;
        Assert.IsGreaterThanOrEqualTo(1, view.Storage.Count, "The screen now says what the last pass did.");
        Assert.IsNotNull(view.Database.LastRunAtUtc);
        await ResetAsync(admin);
    }

    // ---- the page and its client --------------------------------------------------------------------------------------------------------

    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }

    private sealed class Routing(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    private static StorageApiClient ClientFor(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var http = new HttpClient(new Routing(respond)) { BaseAddress = new Uri("http://localhost/") };
        return new StorageApiClient(http, new AntiforgeryTokenProvider(http));
    }

    private static MaintenanceValuesDto Values() => new(14, 7, 30, 14, 7, 10, 24, 1, 90, 180, 180, 30, 90, 90, 90, 730, 30);

    private static MaintenanceSettingsResponse Settings(bool customized, MaintenanceRunResponse? storageRun = null)
    {
        var limits = new Dictionary<string, MaintenanceLimitResponse>();
        foreach (var name in typeof(MaintenanceValuesDto).GetProperties().Select(p => char.ToLowerInvariant(p.Name[0]) + p.Name[1..])) limits[name] = new MaintenanceLimitResponse(1, 3650);
        return new MaintenanceSettingsResponse(Values(), Values(), customized, limits,
            storageRun ?? new MaintenanceRunResponse(null, 0, 0, null), new MaintenanceRunResponse(DateTimeOffset.UtcNow.AddHours(-5), 120, 0, DateTimeOffset.UtcNow.AddHours(19)),
            new MaintenanceRunResponse(DateTimeOffset.UtcNow.AddHours(-2), 3, 0, DateTimeOffset.UtcNow.AddHours(22)));
    }

    private static async Task<string> RenderPanelAsync(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        await using var services = new ServiceCollection().AddLogging().AddMudServices().AddSingleton<IJSRuntime, NoJs>().AddSingleton(ClientFor(respond)).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);
        return await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<MaintenanceSettingsPanel>(ParameterView.Empty)).ToHtmlString());
    }

    [TestMethod]
    public async Task ThePanelShowsEveryPeriodWithItsLimitsDefaultAndWhenCleanUpLastRan()
    {
        var html = await RenderPanelAsync(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Settings(customized: false)) });

        foreach (var label in new[] { "Failed a security check", "Held ebooks waiting for a person", "Held audiobooks waiting for a person", "Held files size limit",
                     "Files with no record, wait before removing", "File-check evidence for deleted files", "Audit log", "Messages already sent", "Search again after" })
            StringAssert.Contains(html, label);
        StringAssert.Contains(html, "Between 1 and 3650; default 14.", "Each field says what is allowed and the default.");
        StringAssert.Contains(html, "These are the default values.");
        StringAssert.Contains(html, "It has not run yet", "A clean-up that has not run says so.");
        StringAssert.Contains(html, "120 old rows removed");
        StringAssert.Contains(html, "3 better copies proposed");
        StringAssert.Contains(html, "Save clean-up settings");
        StringAssert.Contains(html, "Restore defaults");
        StringAssert.Contains(html, "never removed", "Says a trusted copy is never removed.");
    }

    [TestMethod]
    public async Task ThePanelSaysWhenValuesAreCustomisedAndSaysSoWhenItCannotLoad()
    {
        var custom = await RenderPanelAsync(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Settings(customized: true, new MaintenanceRunResponse(DateTimeOffset.UtcNow.AddHours(-1), 4, 1, DateTimeOffset.UtcNow.AddHours(5)))) });
        StringAssert.Contains(custom, "These values are customised.");
        StringAssert.Contains(custom, "4 files removed, with 1 step that failed and will be retried");

        var failed = await RenderPanelAsync(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        StringAssert.Contains(failed, "could not be loaded");
        Assert.IsFalse(failed.Contains("Save clean-up settings", StringComparison.Ordinal), "No save button over settings that were never loaded.");
    }

    [TestMethod]
    public async Task TheClientReturnsTheHostsReasonsWhenASaveIsRefusedAndAClearMessageWhenForbidden()
    {
        var refused = await ClientFor(_ => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new MaintenanceProblemsResponse(["Audit log must be between 365 and 3650."])) })
            .SaveSettingsAsync(Values());
        Assert.IsFalse(refused.Succeeded);
        Assert.AreEqual(1, refused.Problems.Count);
        Assert.AreEqual("Audit log must be between 365 and 3650.", refused.Problems[0]);

        var forbidden = await ClientFor(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)).SaveSettingsAsync(Values());
        Assert.IsFalse(forbidden.Succeeded);
        StringAssert.Contains(forbidden.Error, "administrator");

        var ok = await ClientFor(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Settings(true)) }).SaveSettingsAsync(Values());
        Assert.IsTrue(ok.Succeeded);
    }

    [TestMethod]
    public async Task AReplyThatIsNotTheSettingsIsAFailureToLoadNeverAScreenOfZeros()
    {
        var empty = ClientFor(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { }) });

        await Assert.ThrowsAsync<InvalidOperationException>(() => empty.GetSettingsAsync());
    }

    [TestMethod]
    public void RunSummariesReadInPlainWords()
    {
        var now = new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

        Assert.AreEqual("—", StorageText.Date(null));
        StringAssert.Contains(StorageText.RunSummary(new MaintenanceRunResponse(now.AddHours(-3), 2, 0, now.AddHours(3)), now, "files removed"), "Last ran 3 h ago: 2 files removed. Next pass in about 3 h.");
        StringAssert.Contains(StorageText.RunSummary(new MaintenanceRunResponse(now.AddDays(-1), 0, 0, now.AddMinutes(-5)), now, "files removed"), "Next pass is due now.");
        StringAssert.Contains(StorageText.RunSummary(new MaintenanceRunResponse(now.AddHours(-3), 2, 2, null), now, "files removed"), "2 steps that failed");
    }

    [TestMethod]
    public void TheRemovalDateIsExactlyWhenCleanUpRemovesTheCopy()
    {
        // The date shown to people and the rule that does the removing must never disagree.
        var now = new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
        var options = new StorageRetentionOptions();
        var cases = new (MediaAssetStorageState State, RequestMediaType Type, bool RuledOut, bool Awaiting)[]
        {
            (MediaAssetStorageState.Rejected, RequestMediaType.Ebook, false, false), (MediaAssetStorageState.Quarantine, RequestMediaType.Ebook, false, false),
            (MediaAssetStorageState.Processing, RequestMediaType.Audiobook, false, false), (MediaAssetStorageState.Unmatched, RequestMediaType.Audiobook, true, false),
            (MediaAssetStorageState.Unmatched, RequestMediaType.Ebook, false, false), (MediaAssetStorageState.Unmatched, RequestMediaType.Audiobook, false, false),
            (MediaAssetStorageState.Processing, RequestMediaType.Ebook, false, true), (MediaAssetStorageState.Processing, RequestMediaType.Audiobook, false, true),
            (MediaAssetStorageState.Processing, RequestMediaType.Ebook, true, true)
        };
        foreach (var (state, type, ruledOut, awaiting) in cases)
        {
            var since = now.AddDays(-100);
            var due = StorageRetentionPlanner.RemovalDate(state, type, ruledOut, awaiting, since, options)!.Value;
            RemovableCopy Copy() => new(Guid.NewGuid(), null, state, type, ruledOut, 10, since, Guid.NewGuid(), "t", false, awaiting);
            Assert.HasCount(1, StorageRetentionPlanner.Plan([Copy()], options, due, false).Removals, $"{state}/{type}/{ruledOut}/{awaiting}: removed on the date shown");
            Assert.IsEmpty(StorageRetentionPlanner.Plan([Copy()], options, due.AddMinutes(-1), false).Removals, $"{state}/{type}/{ruledOut}/{awaiting}: not before it");
        }

        Assert.AreEqual(now.AddDays(30), StorageRetentionPlanner.RemovalDate(MediaAssetStorageState.Processing, RequestMediaType.Ebook, false, true, now, options),
            "A checked file waiting for a librarian shows the waiting period, not the short stuck one.");

        Assert.IsNull(StorageRetentionPlanner.RemovalDate(MediaAssetStorageState.Trusted, RequestMediaType.Ebook, false, false, now, options), "A trusted file has no removal date.");
    }
}
