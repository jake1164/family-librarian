using System.Net;
using System.Net.Http.Json;
using System.Text;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Contracts.Authentication;
using FamilyLibrarian.Infrastructure.Gutenberg;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Gutenberg;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FamilyLibrarian.Web.Tests;

[TestClass]
public sealed class GutenbergRefreshTests
{
    private static WebTestFixture? _fixture;
    private static readonly string[] IncrementalRequests = ["primary.example/feed", "fallback.example/feed", "primary.example/rdf/1/pg1.rdf", "fallback.example/rdf/1/pg1.rdf"];
    private static readonly string[] ArchiveRequests = ["primary.example/archive", "fallback.example/archive"];
    // One RDF book in a deterministic V7 tar, compressed with bzip2.
    private static readonly byte[] Archive = Convert.FromBase64String(
        "QlpoOTFBWSZTWaWGeLcAAPJfgsiAWAv/twUAFCA/79/wEAAACDABSMGEkqGmmjIbUeoDIZNAANGglCmUMQGgDRp6gNMhoAEkhAU/IFD00jNQaABjUxRYQMGi4IHOhMsWO6gYGp4tITYsnh9qZFumvTmhrHz01q98ZAIMPJF3IvZ8RgVDoyZ0RKdCEbnXlYknZ7mL/HYszyiVcES3bCZcwDIIoQoUR3lkStRrRaajQQiUjgMqkThkqNYVErCzDFnsHMUSeyUl+WWB/cOwHJZm5mZrdAD1IjaxnEDGi0bf7jxL2yDhYkqgkKV7CrIhZtNJI5VA48IIf2kCvuUQhMfaYSoRkWaWLlsnRUjUigLxxaoUHPX3szY9eFNIXE8bnFSbaMOK9lBcGYPcPSWGJuLnvEwwoBHVSXk7iWaIpNNWEi8wDmHVAmMgmdeREAd09lIo3p2GiMs4JfxdyRThQkKWGeLc");
    private const string Rdf = """
        <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#" xmlns:dcterms="http://purl.org/dc/terms/" xmlns:pgterms="http://www.gutenberg.org/2009/pgterms/">
          <pgterms:ebook rdf:about="ebooks/1"><dcterms:title>Updated fixture book</dcterms:title>
          <pgterms:file rdf:about="https://www.gutenberg.org/ebooks/1.epub.noimages"><dcterms:format><rdf:Description><rdf:value>application/epub+zip</rdf:value></rdf:Description></dcterms:format></pgterms:file></pgterms:ebook>
        </rdf:RDF>
        """;

    [ClassInitialize]
    public static async Task InitializeAsync(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _fixture = await WebTestFixture.CreateAsync();
    }

    [ClassCleanup]
    public static async Task CleanupAsync()
    {
        if (_fixture is not null) await _fixture.DisposeAsync();
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task ManualAndScheduledChecksUseFallbackForBothFullArchivesAndIncrementalRecords(bool scheduled, bool incremental)
    {
        var handler = new CatalogHandler();
        await using var factory = CreateFactory(handler);
        await SeedAsync(factory, incremental ? "Completed" : "NeverSynced", recent: incremental);
        var coordinator = factory.Services.GetRequiredService<GutenbergCatalogRefreshCoordinator>();
        if (scheduled)
        {
            using var worker = Worker(factory);
            await worker.CheckForUpdatesAsync(CancellationToken.None);
        }
        else
        {
            using var client = await AdminAsync(factory);
            using var response = await client.PostAsync("/api/v1/admin/gutenberg/sync", null);
            Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
            var queued = await response.Content.ReadFromJsonAsync<GutenbergCatalogStatus>();
            Assert.AreEqual("Queued", queued?.Status);
            await coordinator.RunQueuedAsync(CancellationToken.None);
        }

        var status = await StatusAsync(factory);
        Assert.AreEqual("Completed", status.Status, status.FailureMessage);
        Assert.AreEqual(1, status.BookCount);
        Assert.AreEqual(1, status.FormatCount);
        Assert.AreEqual("fallback.example", status.SourceHost);
        Assert.IsNotNull(status.LastSuccessfulCheckUtc);
        if (incremental)
        {
            CollectionAssert.AreEqual(IncrementalRequests, handler.Requests);
        }
        else
        {
            Assert.AreEqual((long)Archive.Length, status.DownloadedBytes);
            CollectionAssert.AreEqual(ArchiveRequests, handler.Requests);
        }
    }

    [TestMethod]
    public async Task RefreshReturnsImmediatelySurvivesClientDisposalAndReportsBytesWhileRunning()
    {
        var clock = new SteppedClock();
        await using var body = new PausedStream(Archive);
        await using var factory = CreateFactory(new CatalogHandler { Body = body }, clock);
        await SeedAsync(factory, "NeverSynced");
        using (var client = await AdminAsync(factory))
        {
            using var requestLimit = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var response = await client.PostAsync("/api/v1/admin/gutenberg/sync", null, requestLimit.Token);
            Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
        }

        var coordinator = factory.Services.GetRequiredService<GutenbergCatalogRefreshCoordinator>();
        var running = coordinator.RunQueuedAsync(CancellationToken.None);
        try
        {
            await body.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var status = await StatusAsync(factory);
            Assert.AreEqual("Downloading", status.Status);
            Assert.IsTrue(status.DownloadedBytes > 0 && status.DownloadedBytes < Archive.Length);
            Assert.AreEqual((long)Archive.Length, status.DownloadSizeBytes);
            Assert.AreEqual("fallback.example", status.SourceHost);
            Assert.IsFalse((await coordinator.QueueAsync(false, CancellationToken.None)).Accepted);
            Assert.IsNull(await coordinator.TryPurgeAsync(CancellationToken.None));
        }
        finally { body.Release.TrySetResult(); }
        await running.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual("Completed", (await StatusAsync(factory)).Status);
    }

    [TestMethod]
    public async Task WorkerShutdownLeavesAnActionableFailureAndKeepsTheActiveCatalogue()
    {
        await using var body = new PausedStream(Archive);
        await using var factory = CreateFactory(new CatalogHandler { Body = body });
        var generation = await SeedAsync(factory, "Completed", recent: false);
        var coordinator = factory.Services.GetRequiredService<GutenbergCatalogRefreshCoordinator>();
        Assert.IsTrue((await coordinator.QueueAsync(false, CancellationToken.None)).Accepted);
        using var stopping = new CancellationTokenSource();
        var running = coordinator.RunQueuedAsync(stopping.Token);
        await body.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        stopping.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => running);
        var status = await StatusAsync(factory);
        Assert.AreEqual("Failed", status.Status);
        Assert.IsTrue(status.IsReady);
        StringAssert.Contains(status.FailureMessage!, "interrupted");
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.AreEqual(generation, (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Set<GutenbergCatalogSyncStateEntity>().SingleAsync()).ActiveGenerationId);
        Assert.IsTrue((await coordinator.QueueAsync(false, CancellationToken.None)).Accepted, "Cancellation must not block the next refresh.");
    }

    [TestMethod]
    [DataRow("Downloading")]
    [DataRow("Importing")]
    [DataRow("Purging")]
    public async Task StartupRecoversInterruptedOperationsWithoutRemovingTheSearchableSnapshot(string phase)
    {
        await using var factory = CreateFactory(new CatalogHandler());
        await SeedAsync(factory, phase, recent: true);
        var coordinator = factory.Services.GetRequiredService<GutenbergCatalogRefreshCoordinator>();
        await coordinator.RecoverInterruptedAsync(CancellationToken.None);
        var status = await StatusAsync(factory);
        Assert.AreEqual("Failed", status.Status);
        Assert.IsTrue(status.IsReady);
        StringAssert.Contains(status.FailureMessage!, "interrupted");
        Assert.IsTrue((await coordinator.QueueAsync(false, CancellationToken.None)).Accepted);
    }

    [TestMethod]
    public async Task AQueuedRefreshSurvivesHostRestartAndOnlyOneConcurrentRequestIsAccepted()
    {
        var handler = new CatalogHandler();
        await using (var original = CreateFactory(handler))
        {
            await SeedAsync(original, "Completed", recent: true);
            var coordinator = original.Services.GetRequiredService<GutenbergCatalogRefreshCoordinator>();
            var requests = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => coordinator.QueueAsync(false, CancellationToken.None)));
            Assert.AreEqual(1, requests.Count(result => result.Accepted));
        }
        await using var restarted = CreateFactory(handler);
        var recovered = restarted.Services.GetRequiredService<GutenbergCatalogRefreshCoordinator>();
        await recovered.RecoverInterruptedAsync(CancellationToken.None);
        Assert.AreEqual("Queued", (await StatusAsync(restarted)).Status);
        await recovered.RunQueuedAsync(CancellationToken.None);
        Assert.AreEqual("Completed", (await StatusAsync(restarted)).Status);
    }

    [TestMethod]
    public async Task ARecentFullImportTakesPrecedenceOverAnOlderIncrementalCheck()
    {
        var handler = new CatalogHandler();
        await using var factory = CreateFactory(handler);
        await SeedAsync(factory, "Completed", recent: true);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var state = await database.Set<GutenbergCatalogSyncStateEntity>().SingleAsync();
            state.LastSuccessfulIncrementalSyncUtc = DateTimeOffset.UtcNow.AddMonths(-2);
            await database.SaveChangesAsync();
        }
        using var worker = Worker(factory);
        await worker.CheckForUpdatesAsync(CancellationToken.None);
        Assert.IsTrue(handler.Requests.All(request => !request.EndsWith("/archive", StringComparison.Ordinal)));
        Assert.AreEqual("Completed", (await StatusAsync(factory)).Status);
    }

    [TestMethod]
    public async Task QueueDoesNotWakeTheWorkerUntilTheReservationIsReleased()
    {
        var reply = new BlockedStatusCatalog();
        await using var factory = CreateFactory(new CatalogHandler(), responseCatalog: reply);
        await SeedAsync(factory, "Completed", recent: true);
        var coordinator = factory.Services.GetRequiredService<GutenbergCatalogRefreshCoordinator>();
        var queuing = coordinator.QueueAsync(false, CancellationToken.None);
        await reply.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var wakeup = coordinator.WaitForWorkAsync(TimeSpan.FromSeconds(10), limit.Token);
        try { Assert.IsFalse(wakeup.IsCompleted, "Waking before releasing the reservation can lose the only queue signal."); }
        finally { reply.Release.TrySetResult(new(false, null, null, null, 0, 0, 0, 0, null, "Queued", null)); }
        Assert.IsTrue((await queuing).Accepted);
        Assert.IsTrue(await wakeup);
    }

    [TestMethod]
    public async Task HostedWorkerPicksUpQueuedWorkAndUsesTheFallbackWithoutAConnectedBrowser()
    {
        var handler = new CatalogHandler();
        await using var factory = CreateFactory(handler);
        await SeedAsync(factory, "Completed", recent: true);
        Assert.IsTrue((await factory.Services.GetRequiredService<GutenbergCatalogRefreshCoordinator>().QueueAsync(false, CancellationToken.None)).Accepted);
        using var worker = Worker(factory);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while ((await StatusAsync(factory)).Status != "Completed")
                await Task.Delay(10, limit.Token);
            CollectionAssert.AreEqual(IncrementalRequests, handler.Requests);
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

    [TestMethod]
    public async Task ExhaustedSourcesSaveAnErrorAndPermitRetry()
    {
        await using var factory = CreateFactory(new CatalogHandler { AllUnavailable = true });
        await SeedAsync(factory, "Completed", recent: true);
        using var worker = Worker(factory);
        await worker.CheckForUpdatesAsync(CancellationToken.None);
        var status = await StatusAsync(factory);
        Assert.AreEqual("Failed", status.Status);
        StringAssert.Contains(status.FailureMessage!, "All 2");
        Assert.IsTrue((await factory.Services.GetRequiredService<GutenbergCatalogRefreshCoordinator>().QueueAsync(false, CancellationToken.None)).Accepted);
    }

    [TestMethod]
    [TestCategory("Migration")]
    public async Task ProgressMigrationPreservesTheExistingCatalogueAndDefaultsNewFields()
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        await using var scope = fixture.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var migrations = database.Database.GetMigrations().ToArray();
        var index = Array.FindIndex(
            migrations, name => name.EndsWith("_AddGutenbergRefreshProgress", StringComparison.Ordinal));
        Assert.IsTrue(index > 0);
        await database.GetService<IMigrator>().MigrateAsync(migrations[index - 1]);
        await database.Database.ExecuteSqlRawAsync("""
            INSERT INTO gutenberg.catalog_sync_states (id,active_generation_id,book_count,format_count,parse_error_count,in_progress_book_count,in_progress_format_count,status)
            VALUES ('gutenberg','11111111-1111-1111-1111-111111111111',79824,2030793,0,0,0,'Downloading')
            """);
        await database.Database.MigrateAsync();
        var state = await database.Set<GutenbergCatalogSyncStateEntity>().SingleAsync();
        Assert.AreEqual(79824, state.BookCount);
        Assert.AreEqual(2030793, state.FormatCount);
        Assert.IsNotNull(state.ActiveGenerationId);
        Assert.AreEqual(0L, state.DownloadedBytes);
        Assert.IsNull(state.SourceHost);
        Assert.IsNull(state.ProgressMessage);
        Assert.IsFalse(database.Database.HasPendingModelChanges());
    }

    private static FamilyLibrarianAppFactory CreateFactory(CatalogHandler handler, TimeProvider? clock = null, IGutenbergCatalog? responseCatalog = null) =>
        new(WebTestFixture.Require(_fixture).ConnectionString, services =>
        {
            services.AddHttpClient("GutenbergCatalogSynchronizer").ConfigurePrimaryHttpMessageHandler(() => handler);
            if (clock is not null) { services.RemoveAll<TimeProvider>(); services.AddSingleton(clock); }
            if (responseCatalog is not null) { services.RemoveAll<IGutenbergCatalog>(); services.AddSingleton(responseCatalog); }
            services.PostConfigure<GutenbergCatalogOptions>(options =>
            {
                options.ArchiveUrl = "https://primary.example/archive";
                options.FallbackArchiveUrls = ["https://fallback.example/archive"];
                options.RecentUpdatesFeedUrl = "https://primary.example/feed";
                options.FallbackRecentUpdatesFeedUrls = ["https://fallback.example/feed"];
                options.EbookRdfBaseUrl = "https://primary.example/rdf/";
                options.FallbackEbookRdfBaseUrls = ["https://fallback.example/rdf/"];
                options.MinimumBookCount = 1;
                options.ImportMaxAttempts = 1;
            });
        });

    private static GutenbergCatalogHostedService Worker(FamilyLibrarianAppFactory factory) => new(
        factory.Services.GetRequiredService<GutenbergCatalogRefreshCoordinator>(),
        factory.Services.GetRequiredService<IOptions<GutenbergCatalogOptions>>(),
        factory.Services.GetRequiredService<TimeProvider>(), NullLogger<GutenbergCatalogHostedService>.Instance);

    private static async Task<Guid> SeedAsync(FamilyLibrarianAppFactory factory, string status, bool recent = false)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await database.Database.ExecuteSqlRawAsync("DELETE FROM gutenberg.catalog_books; DELETE FROM gutenberg.catalog_sync_states;");
        var generation = Guid.NewGuid();
        database.Add(new GutenbergCatalogSyncStateEntity
        {
            Status = status, ActiveGenerationId = status == "NeverSynced" ? null : generation,
            LastSuccessfulSyncUtc = DateTimeOffset.UtcNow.AddHours(recent ? -1 : -72)
        });
        await database.SaveChangesAsync();
        return generation;
    }

    private static async Task<GutenbergCatalogStatus> StatusAsync(FamilyLibrarianAppFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IGutenbergCatalog>().GetStatusAsync(CancellationToken.None);
    }

    private static async Task<HttpClient> AdminAsync(FamilyLibrarianAppFactory factory)
    {
        var client = factory.CreateClient();
        using var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = FamilyLibrarianAppFactory.AdminEmail, Password = FamilyLibrarianAppFactory.AdminPassword });
        Assert.AreEqual(HttpStatusCode.NoContent, login.StatusCode);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(client));
        return client;
    }

    private sealed class CatalogHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public Stream? Body { get; init; }
        public bool AllUnavailable { get; init; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri.Host + uri.AbsolutePath);
            if (uri.Host == "primary.example" || AllUnavailable)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            HttpContent content = uri.AbsolutePath switch
            {
                "/archive" => Body is not null ? new StreamContent(Body) : new ByteArrayContent(Archive),
                "/feed" => new StringContent("<rss><channel><item><link>https://www.gutenberg.org/ebooks/1</link></item></channel></rss>", Encoding.UTF8, "application/rss+xml"),
                _ => new StringContent(Rdf, Encoding.UTF8, "application/rdf+xml")
            };
            if (uri.AbsolutePath == "/archive") content.Headers.ContentLength = Archive.Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class BlockedStatusCatalog : IGutenbergCatalog
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<GutenbergCatalogStatus> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<GutenbergCatalogStatus> GetStatusAsync(CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            return Release.Task.WaitAsync(cancellationToken);
        }
        public Task<IReadOnlyList<GutenbergCatalogBook>> SearchAsync(GutenbergCatalogSearchQuery query, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class SteppedClock : TimeProvider
    {
        private long _seconds;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddSeconds(Interlocked.Increment(ref _seconds));
    }

    private sealed class PausedStream(byte[] bytes) : Stream
    {
        private int _position;
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position == bytes.Length) return 0;
            if (_position > 0)
            {
                Waiting.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            var count = _position == 0 ? bytes.Length / 2 : bytes.Length - _position;
            bytes.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
