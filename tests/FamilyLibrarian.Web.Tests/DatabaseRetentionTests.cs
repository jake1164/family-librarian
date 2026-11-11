using System.Net.Http.Json;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Storage;
using FamilyLibrarian.Contracts.Authentication;
using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Contracts.Requests;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Audit;
using FamilyLibrarian.Domain.Communications;
using FamilyLibrarian.Domain.Evidence;
using FamilyLibrarian.Domain.ModelGateway;
using FamilyLibrarian.Domain.Providers;
using FamilyLibrarian.Domain.Notifications;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyLibrarian.Web.Tests;

/// <summary>A6-STORAGE-1 S-3 over real PostgreSQL: old rows go, anything still in use stays, and a pass is bounded, isolated and repeatable.</summary>
[TestClass]
public sealed class DatabaseRetentionTests
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

    private sealed record Seed(Guid WorkId, Guid FormatId, Guid RequestId);

    private static async Task<Seed> SeedRequestAsync(FamilyLibrarianAppFactory factory)
    {
        using var admin = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (await admin.PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = FamilyLibrarianAppFactory.AdminEmail, Password = FamilyLibrarianAppFactory.AdminPassword })).EnsureSuccessStatusCode();
        admin.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(admin));
        var resolve = await admin.PostAsync("/api/v1/catalog/candidates/demo/the-hobbit/resolve", content: null);
        var work = (await resolve.Content.ReadFromJsonAsync<CatalogWorkResponse>())!;
        var created = await admin.PostAsJsonAsync("/api/v1/requests/",
            new CreateBookRequestRequest(await WebTestFixture.Require(fixture).CopyWorkForTestAsync(work.Id), ["Ebook"], null, false, false));
        var request = (await created.Content.ReadFromJsonAsync<BookRequestResponse>())!;
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var workId = (await database.BookRequests.AsNoTracking().SingleAsync(item => item.Id == request.Id)).WorkId;
        return new Seed(workId, request.Formats.Single().FormatId, request.Id);
    }

    private static FamilyLibrarianAppFactory Factory() => new(WebTestFixture.Require(fixture).ConnectionString);

    private static DateTimeOffset Ago(int days) => DateTimeOffset.UtcNow.AddDays(-days);

    private static async Task<DatabaseRetentionReport> RunAsync(FamilyLibrarianAppFactory factory, DatabaseRetentionOptions? options = null, IDatabaseRetentionStore? storeOverride = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        return await new DatabaseRetentionService(storeOverride ?? sp.GetRequiredService<IDatabaseRetentionStore>(), sp.GetRequiredService<IAuditWriter>(),
            sp.GetRequiredService<IClock>(), options ?? new DatabaseRetentionOptions()).RunAsync(CancellationToken.None);
    }

    private static async Task<bool> ExistsAsync<T>(FamilyLibrarianAppFactory factory, Func<AppDbContext, IQueryable<T>> query)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<AppDbContext>()).AnyAsync();
    }

    private static async Task<Guid> AddAssetAsync(FamilyLibrarianAppFactory factory, Seed seed, MediaAssetStorageState state, int ageDays)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;
        var asset = new MediaAsset(seed.WorkId, null, RequestMediaType.Ebook, ".epub", "book.epub", "stored-" + Guid.NewGuid().ToString("N"), 10,
            new string('c', 64), "application/epub+zip", seed.FormatId, null, now);
        if (state != MediaAssetStorageState.Quarantine)
        {
            asset.TransitionStorageState(MediaAssetStorageState.Processing, now);
            if (state != MediaAssetStorageState.Processing) asset.TransitionStorageState(state, now);
        }

        database.MediaAssets.Add(asset);
        await database.SaveChangesAsync();
        await database.MediaAssets.Where(a => a.Id == asset.Id).ExecuteUpdateAsync(set => set.SetProperty(a => a.UpdatedAtUtc, Ago(ageDays)));
        return asset.Id;
    }

    private static async Task<Guid> AddEvidenceAsync(FamilyLibrarianAppFactory factory, Guid assetId, string kind)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var item = new ArtifactEvidence(assetId, new string('c', 64), kind, Guid.NewGuid(), "finding-1", EvidenceDimension.IdentityAttestation,
            "code.x", EvidencePolarity.Positive, "test", null, null, Ago(200));
        database.ArtifactEvidenceItems.Add(item);
        await database.SaveChangesAsync();
        return item.Id;
    }

    [TestMethod]
    public async Task EvidenceGoesOnlyForFilesThatNoLongerExistAndOnlyAfterNinetyDays()
    {
        await using var factory = Factory();
        var seed = await SeedRequestAsync(factory);
        var oldDestroyed = await AddAssetAsync(factory, seed, MediaAssetStorageState.Destroyed, 100);
        var recentDestroyed = await AddAssetAsync(factory, seed, MediaAssetStorageState.Destroyed, 10);
        var oldTrusted = await AddAssetAsync(factory, seed, MediaAssetStorageState.Trusted, 400);
        var oldHeld = await AddAssetAsync(factory, seed, MediaAssetStorageState.Unmatched, 400);
        var overrideAsset = await AddAssetAsync(factory, seed, MediaAssetStorageState.Destroyed, 100);
        var goes = await AddEvidenceAsync(factory, oldDestroyed, ArtifactEvidenceRecordKinds.SecurityEvaluation);
        var tooRecent = await AddEvidenceAsync(factory, recentDestroyed, ArtifactEvidenceRecordKinds.SecurityEvaluation);
        var trusted = await AddEvidenceAsync(factory, oldTrusted, ArtifactEvidenceRecordKinds.SecurityEvaluation);
        var held = await AddEvidenceAsync(factory, oldHeld, ArtifactEvidenceRecordKinds.SecurityEvaluation);
        var overridden = await AddEvidenceAsync(factory, overrideAsset, ArtifactEvidenceRecordKinds.IdentityOverride);

        var report = await RunAsync(factory);

        Assert.IsGreaterThanOrEqualTo(1, report.Removed[RetentionTarget.ArtifactEvidence]);
        Assert.IsFalse(await ExistsAsync(factory, db => db.ArtifactEvidenceItems.Where(e => e.Id == goes)));
        foreach (var kept in new[] { tooRecent, trusted, held, overridden })
            Assert.IsTrue(await ExistsAsync(factory, db => db.ArtifactEvidenceItems.Where(e => e.Id == kept)), "Evidence still in use, too recent, or an override record stays.");
    }

    [TestMethod]
    public async Task EvidenceBehindAnOpenReviewStaysEvenWhenOld()
    {
        await using var factory = Factory();
        var seed = await SeedRequestAsync(factory);
        var asset = await AddAssetAsync(factory, seed, MediaAssetStorageState.Destroyed, 300);
        var evidence = await AddEvidenceAsync(factory, asset, ArtifactEvidenceRecordKinds.SecurityEvaluation);
        await using (var scope = factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().BookRequests.Where(r => r.Id == seed.RequestId)
                .ExecuteUpdateAsync(set => set.SetProperty(r => r.Status, RequestStatus.NeedsReview));

        await RunAsync(factory);
        Assert.IsTrue(await ExistsAsync(factory, db => db.ArtifactEvidenceItems.Where(e => e.Id == evidence)), "An unresolved review keeps its evidence.");

        await using (var scope = factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().BookRequests.Where(r => r.Id == seed.RequestId)
                .ExecuteUpdateAsync(set => set.SetProperty(r => r.Status, RequestStatus.Available));
        await RunAsync(factory);
        Assert.IsFalse(await ExistsAsync(factory, db => db.ArtifactEvidenceItems.Where(e => e.Id == evidence)), "Once the review is closed the evidence expires.");
    }

    [TestMethod]
    public async Task AttemptHistoryAndSearchMemoryGoOnlyForFinishedRequests()
    {
        await using var factory = Factory();
        var seed = await SeedRequestAsync(factory);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ProviderAttempts.Add(new ProviderAttempt(seed.RequestId, seed.FormatId, "p-old-open", ProviderAttemptOutcome.NoMatch, "old, open request", Ago(400), null));
            db.SearchVariantAttempts.Add(new SearchVariantAttempt(seed.FormatId, "p-old-open", "kind", "key", Ago(100), 0));
            await db.SaveChangesAsync();
        }

        await RunAsync(factory);
        Assert.IsTrue(await ExistsAsync(factory, db => db.ProviderAttempts.Where(a => a.ProviderId == "p-old-open")), "An open request keeps its history, however old.");
        Assert.IsTrue(await ExistsAsync(factory, db => db.SearchVariantAttempts.Where(a => a.ProviderId == "p-old-open")), "An open request keeps its search memory.");

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.BookRequests.Where(r => r.Id == seed.RequestId).ExecuteUpdateAsync(set => set.SetProperty(r => r.Status, RequestStatus.Available));
            await db.RequestFormats.Where(f => f.Id == seed.FormatId).ExecuteUpdateAsync(set => set.SetProperty(f => f.Status, RequestFormatStatus.Available));
            db.ProviderAttempts.Add(new ProviderAttempt(seed.RequestId, seed.FormatId, "p-recent", ProviderAttemptOutcome.NoMatch, "recent, finished", Ago(5), null));
            db.SearchVariantAttempts.Add(new SearchVariantAttempt(seed.FormatId, "p-recent", "kind", "key", Ago(5), 0));
            await db.SaveChangesAsync();
        }

        await RunAsync(factory);
        Assert.IsFalse(await ExistsAsync(factory, db => db.ProviderAttempts.Where(a => a.ProviderId == "p-old-open")), "Finished and older than 180 days: gone.");
        Assert.IsFalse(await ExistsAsync(factory, db => db.SearchVariantAttempts.Where(a => a.ProviderId == "p-old-open")), "Finished and older than 30 days: gone.");
        Assert.IsTrue(await ExistsAsync(factory, db => db.ProviderAttempts.Where(a => a.ProviderId == "p-recent")));
        Assert.IsTrue(await ExistsAsync(factory, db => db.SearchVariantAttempts.Where(a => a.ProviderId == "p-recent")));
    }

    [TestMethod]
    public async Task OldNotificationsAndTheirReceiptsGoAndRecentOnesStay()
    {
        await using var factory = Factory();
        var (oldId, newId) = (Guid.Empty, Guid.Empty);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await db.Users.FirstAsync();
            var old = new NotificationEvent(NotificationAudience.AdminBroadcast, null, "test.old", NotificationSeverity.Info, "old", null, null, null, Ago(120));
            var recent = new NotificationEvent(NotificationAudience.AdminBroadcast, null, "test.recent", NotificationSeverity.Info, "recent", null, null, null, Ago(5));
            db.NotificationEvents.AddRange(old, recent);
            await db.SaveChangesAsync();
            db.NotificationReceipts.Add(new NotificationReceipt(old.Id, user.Id));
            db.NotificationReceipts.Add(new NotificationReceipt(recent.Id, user.Id));
            await db.SaveChangesAsync();
            (oldId, newId) = (old.Id, recent.Id);
        }

        await RunAsync(factory);

        Assert.IsFalse(await ExistsAsync(factory, db => db.NotificationEvents.Where(e => e.Id == oldId)));
        Assert.IsFalse(await ExistsAsync(factory, db => db.NotificationReceipts.Where(r => r.NotificationEventId == oldId)), "Receipts go with their event.");
        Assert.IsTrue(await ExistsAsync(factory, db => db.NotificationEvents.Where(e => e.Id == newId)));
        Assert.IsTrue(await ExistsAsync(factory, db => db.NotificationReceipts.Where(r => r.NotificationEventId == newId)));
    }

    [TestMethod]
    public async Task SentMessagesAndFindingsAndClosedAlertsAgeOutButUnsentAndOpenOnesNever()
    {
        await using var factory = Factory();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await db.Users.FirstAsync();
            var sentOld = new OutboundCommunication(user.Id, "test.sent_old", "body", null, null, null, null, Ago(200));
            sentOld.MarkProcessed(Ago(190));
            var unsentOld = new OutboundCommunication(user.Id, "test.unsent_old", "body", null, null, null, null, Ago(200));
            db.OutboundCommunications.AddRange(sentOld, unsentOld);
            db.SemanticFindings.Add(new SemanticFinding(SemanticSubjectKind.SearchGuidance, "subject-old-finding", null, null, "schema/1", "digest", "d",
                ModelRole.SearchGuidance, "m", false, ModelCallOutcome.Succeeded, "Search", "{}", null, Ago(300), null));
            db.SemanticFindings.Add(new SemanticFinding(SemanticSubjectKind.SearchGuidance, "subject-new-finding", null, null, "schema/1", "digest2", "d",
                ModelRole.SearchGuidance, "m", false, ModelCallOutcome.Succeeded, "Search", "{}", null, Ago(3), null));
            var provider = new ExternalProvider("ret-" + Guid.NewGuid().ToString("N")[..8], "Retention Source", "https://example.test", Ago(400));
            db.ExternalProviders.Add(provider);
            await db.SaveChangesAsync();
            var resolved = new ProviderInteractionAlert(provider.Id, "p-alert-resolved", "Resolved", Ago(300));
            resolved.Resolve("done", Ago(290));
            db.ProviderInteractionAlerts.AddRange(resolved, new ProviderInteractionAlert(provider.Id, "p-alert-open", "Open", Ago(300)));
            await db.SaveChangesAsync();
        }

        await RunAsync(factory);

        Assert.IsFalse(await ExistsAsync(factory, db => db.OutboundCommunications.Where(m => m.CommunicationType == "test.sent_old")));
        Assert.IsTrue(await ExistsAsync(factory, db => db.OutboundCommunications.Where(m => m.CommunicationType == "test.unsent_old")), "A message not yet sent is never dropped.");
        Assert.IsFalse(await ExistsAsync(factory, db => db.SemanticFindings.Where(f => f.SubjectReference == "subject-old-finding")));
        Assert.IsTrue(await ExistsAsync(factory, db => db.SemanticFindings.Where(f => f.SubjectReference == "subject-new-finding")));
        Assert.IsFalse(await ExistsAsync(factory, db => db.ProviderInteractionAlerts.Where(a => a.ProviderId == "p-alert-resolved")));
        Assert.IsTrue(await ExistsAsync(factory, db => db.ProviderInteractionAlerts.Where(a => a.ProviderId == "p-alert-open")), "An alert that is still open is never removed.");
    }

    [TestMethod]
    public async Task AuditKeepsTwoYearsAndTheRunWritesCountsOnly()
    {
        await using var factory = Factory();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.AuditEvents.AddRange(
                new AuditEvent("test.ancient", "test", "a", null, null, null, Ago(800)),
                new AuditEvent("test.last_year", "test", "b", null, null, null, Ago(400)),
                new AuditEvent("test.today", "test", "c", null, null, null, Ago(0)));
            await db.SaveChangesAsync();
        }

        var report = await RunAsync(factory);

        Assert.IsFalse(await ExistsAsync(factory, db => db.AuditEvents.Where(a => a.Action == "test.ancient")), "Older than two years goes.");
        Assert.IsTrue(await ExistsAsync(factory, db => db.AuditEvents.Where(a => a.Action == "test.last_year")));
        Assert.IsTrue(await ExistsAsync(factory, db => db.AuditEvents.Where(a => a.Action == "test.today")));
        Assert.IsGreaterThanOrEqualTo(1, report.Removed[RetentionTarget.AuditEvents]);
        await using var check = factory.Services.CreateAsyncScope();
        var summary = await check.ServiceProvider.GetRequiredService<AppDbContext>().AuditEvents.AsNoTracking()
            .Where(a => a.Action == AuditActions.DatabaseRetentionRun).OrderByDescending(a => a.OccurredAtUtc).FirstAsync();
        StringAssert.Contains(summary.Detail, "AuditEvents");
        Assert.IsNull(summary.ActorUserId);
    }

    [TestMethod]
    public async Task APassIsBoundedAndALaterPassFinishesTheBacklog()
    {
        await using var factory = Factory();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.AuditEvents.Where(a => a.Action == "test.bulk").ExecuteDeleteAsync();
            for (var i = 0; i < 250; i++) db.AuditEvents.Add(new AuditEvent("test.bulk", "test", i.ToString(System.Globalization.CultureInfo.InvariantCulture), null, null, null, Ago(900)));
            await db.SaveChangesAsync();
        }

        var small = new DatabaseRetentionOptions { BatchSize = 100, MaxBatchesPerTable = 1 };
        var first = await RunAsync(factory, small);
        var firstRemaining = await CountBulkAsync(factory);

        Assert.AreEqual(100, first.Removed[RetentionTarget.AuditEvents], "One batch and no more.");
        Assert.AreEqual(150, firstRemaining);
        await RunAsync(factory, small);
        await RunAsync(factory, small);
        Assert.AreEqual(0, await CountBulkAsync(factory), "A few passes later the backlog is gone.");
        var idle = await RunAsync(factory);
        Assert.AreEqual(0, idle.Removed[RetentionTarget.AuditEvents], "Nothing left, nothing to do.");
    }

    private static async Task<int> CountBulkAsync(FamilyLibrarianAppFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().AuditEvents.CountAsync(a => a.Action == "test.bulk");
    }

    [TestMethod]
    public async Task OneTableFailingDoesNotStopTheOthers()
    {
        await using var factory = Factory();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.AuditEvents.Add(new AuditEvent("test.isolation", "test", "x", null, null, null, Ago(900)));
            await db.SaveChangesAsync();
        }

        await using var inner = factory.Services.CreateAsyncScope();
        var failing = new FailingFor(RetentionTarget.ArtifactEvidence, inner.ServiceProvider.GetRequiredService<IDatabaseRetentionStore>());

        var report = await RunAsync(factory, storeOverride: failing);

        Assert.AreEqual(1, report.Failures);
        Assert.IsFalse(await ExistsAsync(factory, db => db.AuditEvents.Where(a => a.Action == "test.isolation")), "Audit trimming still ran.");
    }

    [TestMethod]
    public void ConfiguredPeriodsCannotEraseHistoryEarlyOrDisableTrimming()
    {
        var wild = new DatabaseRetentionOptions { EvidenceDays = 0, AuditDays = 1, NotificationDays = -3, BatchSize = 1, MaxBatchesPerTable = 100000 }.Clamped();
        Assert.AreEqual(30, wild.EvidenceDays);
        Assert.AreEqual(365, wild.AuditDays, "The audit trail is never kept for less than a year.");
        Assert.AreEqual(14, wild.NotificationDays);
        Assert.AreEqual(100, wild.BatchSize);
        Assert.AreEqual(100, wild.MaxBatchesPerTable);
        var huge = new DatabaseRetentionOptions { AuditDays = int.MaxValue, EvidenceDays = int.MaxValue }.Clamped();
        Assert.AreEqual(3650, huge.AuditDays, "Nothing is kept forever by accident.");
        Assert.AreEqual(3650, huge.EvidenceDays);
        var defaults = new DatabaseRetentionOptions();
        Assert.AreEqual(90, defaults.EvidenceDays);
        Assert.AreEqual(730, defaults.AuditDays);
        foreach (var target in Enum.GetValues<RetentionTarget>()) Assert.IsGreaterThan(0, defaults.DaysFor(target));
    }

    private sealed class FailingFor(RetentionTarget failing, IDatabaseRetentionStore inner) : IDatabaseRetentionStore
    {
        public Task<int> PurgeAsync(RetentionTarget target, DateTimeOffset cutoffUtc, int batchSize, CancellationToken cancellationToken) =>
            target == failing ? throw new InvalidOperationException("lock timeout") : inner.PurgeAsync(target, cutoffUtc, batchSize, cancellationToken);
    }
}
