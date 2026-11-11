using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Authentication;
using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Contracts.Requests;
using FamilyLibrarian.Contracts.Storage;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Infrastructure.Storage;
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

/// <summary>A6-STORAGE-1 S-1 over the real host and PostgreSQL: the numbers are right, only administrators see them, and no name leaks.</summary>
[TestClass]
public sealed class StorageInsightEndpointTests
{
    private const string Path = "/api/v1/admin/storage/";
    private const string SecretName = "my-private-reading-list-title.epub";
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

    private static async Task<HttpClient> SignedInAsync(FamilyLibrarianAppFactory factory, string email, string password)
    {
        var http = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var login = await http.PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = email, Password = password });
        Assert.AreEqual(HttpStatusCode.NoContent, login.StatusCode);
        return http;
    }

    private static async Task<(Guid WorkId, Guid FormatId)> SeedWorkAsync(FamilyLibrarianAppFactory factory)
    {
        using var admin = await SignedInAsync(factory, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword);
        admin.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(admin));
        var resolve = await admin.PostAsync("/api/v1/catalog/candidates/demo/the-hobbit/resolve", content: null);
        var work = (await resolve.Content.ReadFromJsonAsync<CatalogWorkResponse>())!;
        var created = await admin.PostAsJsonAsync("/api/v1/requests/",
            new CreateBookRequestRequest(await WebTestFixture.Require(fixture).CopyWorkForTestAsync(work.Id), ["Audiobook"], null, false, false));
        var request = (await created.Content.ReadFromJsonAsync<BookRequestResponse>())!;
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var workId = (await database.BookRequests.AsNoTracking().SingleAsync(item => item.Id == request.Id)).WorkId;
        return (workId, request.Formats.Single().FormatId);
    }

    private static MediaAsset Asset(Guid workId, Guid formatId, RequestMediaType type, long size, MediaAssetStorageState target, bool ruledOut = false, string? original = null)
    {
        var now = DateTimeOffset.UtcNow;
        var asset = new MediaAsset(workId, null, type, type == RequestMediaType.Audiobook ? ".mp3" : ".epub", original ?? "book.epub",
            "stored-" + Guid.NewGuid().ToString("N"), size, new string('b', 64), "application/octet-stream", formatId, null, now);
        if (target != MediaAssetStorageState.Quarantine)
        {
            asset.TransitionStorageState(MediaAssetStorageState.Processing, now);
            if (target == MediaAssetStorageState.Unmatched || target == MediaAssetStorageState.Rejected || target == MediaAssetStorageState.Trusted)
                asset.TransitionStorageState(target, now);
        }

        if (ruledOut) asset.MarkRuledOut(now);
        return asset;
    }

    [TestMethod]
    public async Task HeldFilesAreGroupedByStateKindAndRuledOutWithSizesAndAges()
    {
        await using var factory = Factory();
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await database.MediaAssets.ExecuteDeleteAsync();
        var (work, format) = await SeedWorkAsync(factory);
        var audiobookA = Asset(work, format, RequestMediaType.Audiobook, 600, MediaAssetStorageState.Unmatched);
        var audiobookB = Asset(work, format, RequestMediaType.Audiobook, 400, MediaAssetStorageState.Unmatched);
        var audiobookRuledOut = Asset(work, format, RequestMediaType.Audiobook, 50, MediaAssetStorageState.Unmatched, ruledOut: true);
        var ebook = Asset(work, format, RequestMediaType.Ebook, 7, MediaAssetStorageState.Unmatched);
        var rejected = Asset(work, format, RequestMediaType.Ebook, 9, MediaAssetStorageState.Rejected);
        var gone = Asset(work, format, RequestMediaType.Ebook, 1_000_000, MediaAssetStorageState.Quarantine);
        gone.TransitionStorageState(MediaAssetStorageState.Processing, DateTimeOffset.UtcNow);
        gone.TransitionStorageState(MediaAssetStorageState.Rejected, DateTimeOffset.UtcNow);
        gone.TransitionStorageState(MediaAssetStorageState.Destroyed, DateTimeOffset.UtcNow);
        database.MediaAssets.AddRange(audiobookA, audiobookB, audiobookRuledOut, ebook, rejected, gone);
        await database.SaveChangesAsync();
        var tenDaysAgo = DateTimeOffset.UtcNow.AddDays(-10);
        await database.MediaAssets.Where(a => a.Id == audiobookA.Id).ExecuteUpdateAsync(set => set.SetProperty(a => a.UpdatedAtUtc, tenDaysAgo));

        var held = await new StorageInsightStore(database).GetHeldFilesAsync(CancellationToken.None);

        var waiting = held.Single(g => g.State == MediaAssetStorageState.Unmatched && g.MediaType == RequestMediaType.Audiobook && !g.RuledOut);
        Assert.AreEqual(2, waiting.Files);
        Assert.AreEqual(1_000, waiting.Bytes);
        Assert.IsTrue((tenDaysAgo - waiting.OldestUpdatedAtUtc!.Value).Duration() < TimeSpan.FromSeconds(1));
        Assert.AreEqual(1, held.Single(g => g is { State: MediaAssetStorageState.Unmatched, MediaType: RequestMediaType.Audiobook, RuledOut: true }).Files);
        Assert.AreEqual(7, held.Single(g => g.State == MediaAssetStorageState.Unmatched && g.MediaType == RequestMediaType.Ebook).Bytes);
        Assert.AreEqual(9, held.Single(g => g.State == MediaAssetStorageState.Rejected).Bytes);
        Assert.IsFalse(held.Any(g => g.State == MediaAssetStorageState.Destroyed), "A destroyed asset has no file, so it is not held.");
        Assert.AreEqual(1_066, held.Sum(g => g.Bytes));
    }

    [TestMethod]
    public async Task TableInfoListsRealTablesWithPositiveSizesAndNoSystemSchemas()
    {
        await using var factory = Factory();
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var tables = await new StorageInsightStore(database).GetTableInfoAsync(CancellationToken.None);

        Assert.IsTrue(tables.Any(t => t is { Schema: "security", Table: "artifact_evidence" }));
        Assert.IsTrue(tables.Any(t => t is { Schema: "integrations", Table: "model_usage_ledger" }));
        Assert.IsTrue(tables.All(t => t.ApproximateBytes > 0), "Even an empty table has a size (its indexes).");
        Assert.IsFalse(tables.Any(t => t.Schema is "pg_catalog" or "information_schema" or "pg_toast"));
    }

    [TestMethod]
    public async Task OnlyAnAdministratorCanReadStorageAndTheResponseHasNoNamesOrPaths()
    {
        await using var factory = Factory();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var (work, format) = await SeedWorkAsync(factory);
            database.MediaAssets.Add(Asset(work, format, RequestMediaType.Ebook, 123, MediaAssetStorageState.Unmatched, original: SecretName));
            await database.SaveChangesAsync();
        }

        using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var member = await SignedInAsync(factory, WebTestFixture.UserEmail, WebTestFixture.UserPassword);
        using var admin = await SignedInAsync(factory, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword);

        Assert.AreEqual(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Path)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await member.GetAsync(Path)).StatusCode);
        var text = await admin.GetStringAsync(Path);
        var response = (await admin.GetFromJsonAsync<StorageInsightResponse>(Path))!;

        Assert.IsFalse(text.Contains(SecretName, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(text.Contains("stored-", StringComparison.Ordinal), "No stored file name either.");
        Assert.HasCount(5, response.Zones);
        Assert.IsTrue(response.HeldFiles.Any(g => g.State == "Unmatched" && g.MediaType == "Ebook"));
        Assert.IsTrue(response.Tables.Count > 20);
        Assert.AreEqual(response.Tables.Sum(t => t.ApproximateBytes), response.DatabaseApproximateBytes);
        Assert.IsNotNull(response.Volume.FreeBytes, "The test host's temp volume reports free space.");
    }

    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }

    private sealed class StubHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond());
    }

    private static async Task<string> RenderPageAsync(Func<HttpResponseMessage> respond)
    {
        var http = new HttpClient(new StubHandler(respond)) { BaseAddress = new Uri("http://localhost/") };
        await using var services = new ServiceCollection().AddLogging().AddMudServices().AddSingleton<IJSRuntime, NoJs>()
            .AddSingleton(new StorageApiClient(http)).AddSingleton(new AntiforgeryTokenProvider(http)).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);
        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<Storage>(ParameterView.Empty)).ToHtmlString());
    }

    [TestMethod]
    public async Task ThePageShowsDiskFilesHeldWaitsAndTablesInPlainWordsAndSaysItDeletesNothing()
    {
        var now = DateTimeOffset.UtcNow;
        var insight = new StorageInsightResponse(now, new StorageVolumeResponse(30L * 1024 * 1024 * 1024, 100L * 1024 * 1024 * 1024),
            [new("Quarantine", 2, 3_000_000, now.AddDays(-3), 1, 1_000), new("Unmatched", 0, 0, null, 0, 0)],
            [new("Unmatched", "Audiobook", false, 3, 2_500_000_000, now.AddDays(-12))],
            [new("security", "artifact_evidence", 1_200, 5_000_000)], 5_000_000);

        var html = await RenderPageAsync(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(insight) });

        StringAssert.Contains(html, "30 GB free of 100 GB (30% free)");
        StringAssert.Contains(html, "It does not");
        StringAssert.Contains(html, "delete anything");
        StringAssert.Contains(html, "Received, awaiting scan");
        StringAssert.Contains(html, "Held for identification");
        StringAssert.Contains(html, "12 d");
        StringAssert.Contains(html, "2.33 GB");
        StringAssert.Contains(html, "security.artifact_evidence");
        StringAssert.Contains(html, "Unrecognized");
    }

    [TestMethod]
    public async Task ThePageSaysSoWhenStorageCannotBeLoadedInsteadOfShowingEmptyNumbers()
    {
        var html = await RenderPageAsync(() => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        StringAssert.Contains(html, "could not be loaded");
        Assert.IsFalse(html.Contains("Files on disk", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(0L, "0 B")]
    [DataRow(2_048L, "2 KB")]
    [DataRow(5_242_880L, "5 MB")]
    [DataRow(3_221_225_472L, "3 GB")]
    public void SizesReadInPlainUnits(long bytes, string expected) => Assert.AreEqual(expected, StorageText.Size(bytes));

    [TestMethod]
    public void AgesReadAsDaysHoursOrUnderAnHour()
    {
        var now = new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

        Assert.AreEqual("—", StorageText.Age(null, now));
        Assert.AreEqual("under an hour", StorageText.Age(now.AddMinutes(-20), now));
        Assert.AreEqual("5 h", StorageText.Age(now.AddHours(-5), now));
        Assert.AreEqual("12 d", StorageText.Age(now.AddDays(-12), now));
    }
}
