using System.Net.Http.Json;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Notifications;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Application.Storage;
using FamilyLibrarian.Contracts.Authentication;
using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Contracts.Requests;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Audit;
using FamilyLibrarian.Domain.Notifications;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Acquisition;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FamilyLibrarian.Web.Tests;

/// <summary>A6-STORAGE-1 S-2 over real PostgreSQL and a real staging folder: unwanted files go, everything else stays, and it is safe to repeat.</summary>
[TestClass]
public sealed class StorageMaintenanceTests
{
    private const string SecretName = "my-private-title.epub";
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
            new CreateBookRequestRequest(await WebTestFixture.Require(fixture).CopyWorkForTestAsync(work.Id), ["Audiobook"], null, false, false));
        var request = (await created.Content.ReadFromJsonAsync<BookRequestResponse>())!;
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var workId = (await database.BookRequests.AsNoTracking().SingleAsync(item => item.Id == request.Id)).WorkId;
        return new Seed(workId, request.Formats.Single().FormatId, request.Id);
    }

    /// <summary>Writes a real file, creates its record in the wanted state with the wanted age, and puts the file in that zone's folder.</summary>
    private static async Task<(Guid Id, string Stored)> AddAssetAsync(
        FamilyLibrarianAppFactory factory, Seed seed, MediaAssetStorageState state, RequestMediaType type, double ageDays,
        bool ruledOut = false, Guid? bundle = null, int sequence = 1, bool checkFinished = false)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var staging = scope.ServiceProvider.GetRequiredService<IAssetStagingStore>();
        await using var content = new MemoryStream(System.Text.Encoding.ASCII.GetBytes("file-" + Guid.NewGuid()));
        var staged = await staging.WriteToQuarantineAsync(content, SecretName, 1_000_000, CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        var asset = new MediaAsset(seed.WorkId, null, type, type == RequestMediaType.Audiobook ? ".mp3" : ".epub", SecretName, staged.StoredFilename,
            staged.SizeBytes, staged.Sha256, staged.DetectedMimeType, seed.FormatId, null, now, bundleId: bundle, bundleSequence: bundle is null ? null : sequence,
            bundleTrackCount: bundle is null ? null : 2);
        var path = new List<MediaAssetStorageState>();
        if (state != MediaAssetStorageState.Quarantine) path.Add(MediaAssetStorageState.Processing);
        if (state is MediaAssetStorageState.Unmatched or MediaAssetStorageState.Rejected or MediaAssetStorageState.Trusted) path.Add(state);
        var current = MediaAssetStorageState.Quarantine;
        foreach (var next in path)
        {
            asset.TransitionStorageState(next, now);
            await staging.MoveAsync(current, next, staged.StoredFilename, CancellationToken.None);
            current = next;
        }

        if (ruledOut) asset.MarkRuledOut(now);
        database.MediaAssets.Add(asset);
        if (checkFinished)
        {
            // No scanner or validator result recorded, so the evaluation resolves to Passed: checked, waiting for an approval.
            var evaluation = new FamilyLibrarian.Domain.Security.SecurityEvaluation(asset.Id, "test", now);
            evaluation.Evaluate(now);
            database.SecurityEvaluations.Add(evaluation);
        }

        await database.SaveChangesAsync();
        var then = now.AddDays(-ageDays);
        await database.MediaAssets.Where(a => a.Id == asset.Id).ExecuteUpdateAsync(set => set.SetProperty(a => a.UpdatedAtUtc, then));
        return (asset.Id, staged.StoredFilename);
    }

    private static async Task<StorageMaintenanceReport> RunAsync(FamilyLibrarianAppFactory factory, StorageRetentionOptions? options = null, IAssetStagingStore? stagingOverride = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var service = new StorageMaintenanceService(sp.GetRequiredService<IStorageMaintenanceStore>(), sp.GetRequiredService<IStorageInsightStore>(),
            stagingOverride ?? sp.GetRequiredService<IAssetStagingStore>(), sp.GetRequiredService<IAuditWriter>(),
            sp.GetRequiredService<NotificationService>(), sp.GetRequiredService<IClock>(), options ?? new StorageRetentionOptions());
        return await service.RunAsync(CancellationToken.None);
    }

    private static async Task<MediaAssetStorageState> StateAsync(FamilyLibrarianAppFactory factory, Guid id)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<AppDbContext>().MediaAssets.AsNoTracking().SingleAsync(a => a.Id == id)).StorageState;
    }

    private static async Task<int> CountSummariesAsync(FamilyLibrarianAppFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().AuditEvents.CountAsync(a => a.Action == AuditActions.StorageMaintenanceRun);
    }

    private static string StorageRoot(FamilyLibrarianAppFactory factory) => factory.Services.GetRequiredService<IOptions<StorageOptions>>().Value.RootPath;

    private static bool FileExists(FamilyLibrarianAppFactory factory, string stored) =>
        Directory.Exists(StorageRoot(factory)) && Directory.EnumerateFiles(StorageRoot(factory), stored, SearchOption.AllDirectories).Any();

    [TestMethod]
    public async Task ARunRemovesUnwantedFilesAndKeepsEverythingElse()
    {
        await using var factory = new FamilyLibrarianAppFactory(WebTestFixture.Require(fixture).ConnectionString);
        var seed = await SeedRequestAsync(factory);
        var rejectedOld = await AddAssetAsync(factory, seed, MediaAssetStorageState.Rejected, RequestMediaType.Ebook, 20);
        var rejectedNew = await AddAssetAsync(factory, seed, MediaAssetStorageState.Rejected, RequestMediaType.Ebook, 2);
        var ruledOutOld = await AddAssetAsync(factory, seed, MediaAssetStorageState.Unmatched, RequestMediaType.Audiobook, 8, ruledOut: true);
        var waitingEbookOld = await AddAssetAsync(factory, seed, MediaAssetStorageState.Unmatched, RequestMediaType.Ebook, 31);
        var waitingEbookNew = await AddAssetAsync(factory, seed, MediaAssetStorageState.Unmatched, RequestMediaType.Ebook, 5);
        var waitingAudioOld = await AddAssetAsync(factory, seed, MediaAssetStorageState.Unmatched, RequestMediaType.Audiobook, 15);
        var stuck = await AddAssetAsync(factory, seed, MediaAssetStorageState.Processing, RequestMediaType.Ebook, 9);
        var awaitingLibrarian = await AddAssetAsync(factory, seed, MediaAssetStorageState.Processing, RequestMediaType.Ebook, 9, checkFinished: true);
        var trustedAncient = await AddAssetAsync(factory, seed, MediaAssetStorageState.Trusted, RequestMediaType.Ebook, 400);

        var report = await RunAsync(factory);

        Assert.AreEqual(1, report.RemovedRejected);
        Assert.AreEqual(1, report.RemovedRuledOut);
        Assert.AreEqual(2, report.RemovedWaiting);
        Assert.AreEqual(1, report.RemovedStuck);
        Assert.AreEqual(0, report.Failures);
        foreach (var gone in new[] { rejectedOld, ruledOutOld, waitingEbookOld, waitingAudioOld, stuck })
        {
            Assert.AreEqual(MediaAssetStorageState.Destroyed, await StateAsync(factory, gone.Id));
            Assert.IsFalse(FileExists(factory, gone.Stored), "The file is gone from disk, not only the record.");
        }

        Assert.AreEqual(MediaAssetStorageState.Processing, await StateAsync(factory, awaitingLibrarian.Id),
            "A checked file waiting for a librarian is past the stuck period but is not stuck.");
        foreach (var kept in new[] { rejectedNew, waitingEbookNew, trustedAncient, awaitingLibrarian })
        {
            Assert.AreNotEqual(MediaAssetStorageState.Destroyed, await StateAsync(factory, kept.Id));
            Assert.IsTrue(FileExists(factory, kept.Stored));
        }

        Assert.AreEqual(MediaAssetStorageState.Trusted, await StateAsync(factory, trustedAncient.Id), "A trusted file is never touched, however old.");

        await using var scope = factory.Services.CreateAsyncScope();
        var notes = await scope.ServiceProvider.GetRequiredService<AppDbContext>().RequestStatusHistory.AsNoTracking()
            .Where(history => history.RequestId == seed.RequestId && history.Reason != null && history.Reason.Contains("removed automatically")).ToListAsync();
        Assert.IsNotEmpty(notes, "The removal is noted on the request, whatever its status.");
        Assert.IsTrue(notes.All(note => note.FromStatus == note.ToStatus), "A note never changes the status.");
    }

    [TestMethod]
    public async Task ASecondRunDoesNothingAndAuditCarriesCountsAndIdsNeverNames()
    {
        await using var factory = new FamilyLibrarianAppFactory(WebTestFixture.Require(fixture).ConnectionString);
        var seed = await SeedRequestAsync(factory);
        var old = await AddAssetAsync(factory, seed, MediaAssetStorageState.Rejected, RequestMediaType.Ebook, 30);

        var first = await RunAsync(factory);
        var summariesAfterFirst = await CountSummariesAsync(factory);
        var second = await RunAsync(factory);

        Assert.AreEqual(1, first.TotalRemoved);
        Assert.AreEqual(0, second.TotalRemoved, "Idempotent: nothing left to do.");
        await using var scope = factory.Services.CreateAsyncScope();
        var audits = await scope.ServiceProvider.GetRequiredService<AppDbContext>().AuditEvents.AsNoTracking()
            .Where(a => a.SubjectId == old.Id.ToString() || a.Action == AuditActions.StorageMaintenanceRun).ToListAsync();
        Assert.IsTrue(audits.Any(a => a.Action == AuditActions.StorageMaintenanceRun), "The run that removed a file wrote a counts-only summary.");
        Assert.AreEqual(1, audits.Count(a => a.Action == AuditActions.AssetDestroyed && a.ActorUserId is null), "One automatic removal entry, not attributed to a person.");
        Assert.AreEqual(summariesAfterFirst, await CountSummariesAsync(factory), "The second run removed nothing so wrote no summary.");
        Assert.IsTrue(audits.All(a => a.Detail?.Contains(SecretName, StringComparison.OrdinalIgnoreCase) != true && a.Detail?.Contains(old.Stored, StringComparison.Ordinal) != true),
            "No original name or stored name in the audit trail.");
    }

    [TestMethod]
    public async Task ASetWithATrustedTrackIsNeverTouchedAndAHeldSetIsRemovedWhole()
    {
        await using var factory = new FamilyLibrarianAppFactory(WebTestFixture.Require(fixture).ConnectionString);
        var seed = await SeedRequestAsync(factory);
        var partlyDelivered = Guid.NewGuid();
        var heldA = await AddAssetAsync(factory, seed, MediaAssetStorageState.Unmatched, RequestMediaType.Audiobook, 90, bundle: partlyDelivered, sequence: 1);
        var trusted = await AddAssetAsync(factory, seed, MediaAssetStorageState.Trusted, RequestMediaType.Audiobook, 90, bundle: partlyDelivered, sequence: 2);
        var wholeSet = Guid.NewGuid();
        var setA = await AddAssetAsync(factory, seed, MediaAssetStorageState.Unmatched, RequestMediaType.Audiobook, 40, bundle: wholeSet, sequence: 1);
        var setB = await AddAssetAsync(factory, seed, MediaAssetStorageState.Unmatched, RequestMediaType.Audiobook, 40, bundle: wholeSet, sequence: 2);

        var report = await RunAsync(factory);

        Assert.AreEqual(2, report.RemovedWaiting);
        Assert.AreEqual(MediaAssetStorageState.Unmatched, await StateAsync(factory, heldA.Id));
        Assert.AreEqual(MediaAssetStorageState.Trusted, await StateAsync(factory, trusted.Id));
        Assert.IsTrue(FileExists(factory, heldA.Stored) && FileExists(factory, trusted.Stored));
        Assert.AreEqual(MediaAssetStorageState.Destroyed, await StateAsync(factory, setA.Id));
        Assert.AreEqual(MediaAssetStorageState.Destroyed, await StateAsync(factory, setB.Id));
    }

    [TestMethod]
    public async Task OrphanFilesGoOnlyWhenNoRecordWantsThemAndTheyAreOldEnough()
    {
        await using var factory = new FamilyLibrarianAppFactory(WebTestFixture.Require(fixture).ConnectionString);
        var seed = await SeedRequestAsync(factory);
        var backed = await AddAssetAsync(factory, seed, MediaAssetStorageState.Unmatched, RequestMediaType.Ebook, 1);
        // A real record whose file is in the wrong zone folder (a move interrupted by a crash): must survive.
        var misplaced = await AddAssetAsync(factory, seed, MediaAssetStorageState.Unmatched, RequestMediaType.Ebook, 1);
        var staging = factory.Services.GetRequiredService<IAssetStagingStore>();
        await staging.MoveAsync(MediaAssetStorageState.Unmatched, MediaAssetStorageState.Rejected, misplaced.Stored, CancellationToken.None);
        // Old enough that only the "a record still expects it" rule, not the grace period, can protect these two.
        foreach (var name in new[] { backed.Stored, misplaced.Stored })
        {
            File.SetLastWriteTimeUtc(Directory.EnumerateFiles(StorageRoot(factory), name, SearchOption.AllDirectories).Single(), DateTime.UtcNow.AddDays(-5));
        }

        async Task<string> Loose(MediaAssetStorageState zone, double ageHours)
        {
            await using var content = new MemoryStream(System.Text.Encoding.ASCII.GetBytes("loose-" + Guid.NewGuid()));
            var staged = await staging.WriteToQuarantineAsync(content, "loose.bin", 1_000, CancellationToken.None);
            if (zone != MediaAssetStorageState.Quarantine) await staging.MoveAsync(MediaAssetStorageState.Quarantine, zone, staged.StoredFilename, CancellationToken.None);
            var path = Directory.EnumerateFiles(StorageRoot(factory), staged.StoredFilename, SearchOption.AllDirectories).Single();
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-ageHours));
            return staged.StoredFilename;
        }

        var oldOrphan = await Loose(MediaAssetStorageState.Quarantine, 30);
        var freshOrphan = await Loose(MediaAssetStorageState.Quarantine, 2);
        var oldTrustedZoneOrphan = await Loose(MediaAssetStorageState.Trusted, 500);

        var report = await RunAsync(factory);

        Assert.AreEqual(1, report.RemovedOrphans);
        Assert.IsFalse(FileExists(factory, oldOrphan));
        Assert.IsTrue(FileExists(factory, freshOrphan), "A download in flight is inside the grace period.");
        Assert.IsTrue(FileExists(factory, misplaced.Stored), "A file a record still expects is never an orphan, even in another zone's folder.");
        Assert.IsTrue(FileExists(factory, backed.Stored));
        Assert.IsTrue(FileExists(factory, oldTrustedZoneOrphan), "The trusted zone is reported on the Storage page but never swept.");
    }

    [TestMethod]
    public async Task AFailureInOneStepOrOneFileDoesNotStopTheRest()
    {
        await using var factory = new FamilyLibrarianAppFactory(WebTestFixture.Require(fixture).ConnectionString);
        var seed = await SeedRequestAsync(factory);
        var a = await AddAssetAsync(factory, seed, MediaAssetStorageState.Rejected, RequestMediaType.Ebook, 20);
        var b = await AddAssetAsync(factory, seed, MediaAssetStorageState.Rejected, RequestMediaType.Ebook, 21);
        var real = factory.Services.GetRequiredService<IAssetStagingStore>();
        var failing = new ThrowOnceStore(real, a.Stored);

        var report = await RunAsync(factory, stagingOverride: failing);

        Assert.IsGreaterThanOrEqualTo(1, report.Failures);
        Assert.AreEqual(MediaAssetStorageState.Destroyed, await StateAsync(factory, b.Id), "The other file was still removed.");
        Assert.AreEqual(MediaAssetStorageState.Destroyed, await StateAsync(factory, a.Id), "The record changed first, so the failed delete cannot lose bytes a record still needs.");
        Assert.IsTrue(FileExists(factory, a.Stored), "The leftover file is an orphan for the next sweep.");
    }

    [TestMethod]
    public async Task ACopyReleasedAfterThePlanWasMadeIsNotRemoved()
    {
        await using var factory = new FamilyLibrarianAppFactory(WebTestFixture.Require(fixture).ConnectionString);
        var seed = await SeedRequestAsync(factory);
        var held = await AddAssetAsync(factory, seed, MediaAssetStorageState.Unmatched, RequestMediaType.Ebook, 40);
        var moved = await AddAssetAsync(factory, seed, MediaAssetStorageState.Unmatched, RequestMediaType.Ebook, 40);
        await using var scope = factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var staging = sp.GetRequiredService<IAssetStagingStore>();
        // Between planning and acting, a librarian sends `moved` back for checking.
        var racing = new RaceStore(sp.GetRequiredService<IStorageMaintenanceStore>(), async () =>
        {
            await using var inner = factory.Services.CreateAsyncScope();
            await inner.ServiceProvider.GetRequiredService<AppDbContext>().MediaAssets.Where(a => a.Id == moved.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(a => a.StorageState, MediaAssetStorageState.Processing));
        });
        var service = new StorageMaintenanceService(racing, sp.GetRequiredService<IStorageInsightStore>(), staging, sp.GetRequiredService<IAuditWriter>(),
            sp.GetRequiredService<NotificationService>(), sp.GetRequiredService<IClock>(), new StorageRetentionOptions());

        var report = await service.RunAsync(CancellationToken.None);

        Assert.AreEqual(1, report.RemovedWaiting);
        Assert.AreEqual(MediaAssetStorageState.Destroyed, await StateAsync(factory, held.Id));
        Assert.AreEqual(MediaAssetStorageState.Processing, await StateAsync(factory, moved.Id));
        Assert.IsTrue(FileExists(factory, moved.Stored), "Its file is untouched.");
    }

    [TestMethod]
    public async Task AHeldCopyIsRemovedAsSoonAsAnotherCopyOfItsFormatHasPassedItsChecks()
    {
        await using var factory = new FamilyLibrarianAppFactory(WebTestFixture.Require(fixture).ConnectionString);
        var withBetter = await SeedRequestAsync(factory);
        var withoutBetter = await SeedRequestAsync(factory);
        var setSeed = await SeedRequestAsync(factory);
        var heldReplaced = await AddAssetAsync(factory, withBetter, MediaAssetStorageState.Unmatched, RequestMediaType.Ebook, 0);
        var heldRuledOutReplaced = await AddAssetAsync(factory, withBetter, MediaAssetStorageState.Unmatched, RequestMediaType.Ebook, 0, ruledOut: true);
        var rejectedKept = await AddAssetAsync(factory, withBetter, MediaAssetStorageState.Rejected, RequestMediaType.Ebook, 0);
        var better = await AddAssetAsync(factory, withBetter, MediaAssetStorageState.Trusted, RequestMediaType.Ebook, 0);
        var heldAlone = await AddAssetAsync(factory, withoutBetter, MediaAssetStorageState.Unmatched, RequestMediaType.Ebook, 0);
        // A set whose own track is the trusted one is one delivery in progress, not a replacement.
        var set = Guid.NewGuid();
        var setHeld = await AddAssetAsync(factory, setSeed, MediaAssetStorageState.Unmatched, RequestMediaType.Audiobook, 0, bundle: set, sequence: 1);
        var setTrusted = await AddAssetAsync(factory, setSeed, MediaAssetStorageState.Trusted, RequestMediaType.Audiobook, 0, bundle: set, sequence: 2);

        int removed;
        await using (var scope = factory.Services.CreateAsyncScope())
            removed = await scope.ServiceProvider.GetRequiredService<AssetDiscardService>().DiscardSupersededHeldCopiesAsync(50, CancellationToken.None);

        Assert.IsGreaterThanOrEqualTo(2, removed, "Other tests share this database, so only the rows of this test are asserted below.");
        Assert.AreEqual(MediaAssetStorageState.Destroyed, await StateAsync(factory, heldReplaced.Id));
        Assert.AreEqual(MediaAssetStorageState.Destroyed, await StateAsync(factory, heldRuledOutReplaced.Id));
        Assert.IsFalse(FileExists(factory, heldReplaced.Stored), "The file goes with it.");
        Assert.AreEqual(MediaAssetStorageState.Trusted, await StateAsync(factory, better.Id), "The better copy is never touched.");
        Assert.AreEqual(MediaAssetStorageState.Rejected, await StateAsync(factory, rejectedKept.Id), "Rejected copies keep their own clock.");
        Assert.AreEqual(MediaAssetStorageState.Unmatched, await StateAsync(factory, heldAlone.Id), "Nothing replaced it: it may be the only copy.");
        Assert.AreEqual(MediaAssetStorageState.Unmatched, await StateAsync(factory, setHeld.Id), "A track of the same set being trusted is not a replacement.");
        Assert.AreEqual(MediaAssetStorageState.Trusted, await StateAsync(factory, setTrusted.Id));
    }

    [TestMethod]
    public async Task AReminderIsSentOnceHalfWayAndNotRepeatedOnLaterPasses()
    {
        await using var factory = new FamilyLibrarianAppFactory(WebTestFixture.Require(fixture).ConnectionString);
        var seed = await SeedRequestAsync(factory);
        await AddAssetAsync(factory, seed, MediaAssetStorageState.Unmatched, RequestMediaType.Ebook, 16);

        var first = await RunAsync(factory);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var events = await db.NotificationEvents.Where(e => e.Category == NotificationCategories.HeldCopyExpiring && e.SubjectId == seed.RequestId.ToString()).ToListAsync();
            Assert.HasCount(1, events);
            StringAssert.Contains(events[0].Detail, DateTimeOffset.UtcNow.AddDays(14).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
            Assert.IsFalse(events[0].Title.Contains(SecretName, StringComparison.OrdinalIgnoreCase));
        }

        await RunAsync(factory);

        Assert.AreEqual(1, first.RemindersSent);
        await using var check = factory.Services.CreateAsyncScope();
        Assert.AreEqual(1, await check.ServiceProvider.GetRequiredService<AppDbContext>().NotificationEvents
            .CountAsync(e => e.Category == NotificationCategories.HeldCopyExpiring && e.SubjectId == seed.RequestId.ToString()), "One reminder per request.");
    }

    [TestMethod]
    public async Task UnderDiskPressureAdminsAreAlertedOncePerDayAndOnlySafeCopiesAreHurried()
    {
        await using var factory = new FamilyLibrarianAppFactory(WebTestFixture.Require(fixture).ConnectionString);
        var seed = await SeedRequestAsync(factory);
        var rejected = await AddAssetAsync(factory, seed, MediaAssetStorageState.Rejected, RequestMediaType.Ebook, 2);
        var ruledOut = await AddAssetAsync(factory, seed, MediaAssetStorageState.Unmatched, RequestMediaType.Audiobook, 2, ruledOut: true);
        var waiting = await AddAssetAsync(factory, seed, MediaAssetStorageState.Unmatched, RequestMediaType.Audiobook, 6);
        var low = new LowDiskStore(factory.Services.GetRequiredService<IAssetStagingStore>());
        await using (var clean = factory.Services.CreateAsyncScope())
        {
            await clean.ServiceProvider.GetRequiredService<AppDbContext>().NotificationEvents.Where(e => e.Category == NotificationCategories.StorageLow).ExecuteDeleteAsync();
        }

        var first = await RunAsync(factory, stagingOverride: low);
        var second = await RunAsync(factory, stagingOverride: low);

        Assert.IsTrue(first.UnderDiskPressure);
        Assert.AreEqual(MediaAssetStorageState.Destroyed, await StateAsync(factory, rejected.Id), "Rejected copies are kept a day under pressure, not two weeks.");
        Assert.AreEqual(MediaAssetStorageState.Destroyed, await StateAsync(factory, ruledOut.Id));
        Assert.AreEqual(MediaAssetStorageState.Unmatched, await StateAsync(factory, waiting.Id), "A copy waiting for a person may be the only copy and is never hurried.");
        Assert.IsTrue(second.UnderDiskPressure);
        await using var scope = factory.Services.CreateAsyncScope();
        var alerts = await scope.ServiceProvider.GetRequiredService<AppDbContext>().NotificationEvents.AsNoTracking()
            .Where(e => e.Category == NotificationCategories.StorageLow).ToListAsync();
        Assert.HasCount(1, alerts);
        Assert.AreEqual(1, alerts[0].RepeatCount, "The second pass inside a day did not bring the alert back.");
        Assert.AreEqual(NotificationAudience.AdminBroadcast, alerts[0].Audience);
    }

    [TestMethod]
    public async Task EnoughFreeSpaceMeansNoAlertAndNoHurrying()
    {
        await using var factory = new FamilyLibrarianAppFactory(WebTestFixture.Require(fixture).ConnectionString);
        var seed = await SeedRequestAsync(factory);
        var rejected = await AddAssetAsync(factory, seed, MediaAssetStorageState.Rejected, RequestMediaType.Ebook, 2);
        var roomy = new LowDiskStore(factory.Services.GetRequiredService<IAssetStagingStore>(), free: 900L * StorageRetentionOptions.Gigabyte);

        var report = await RunAsync(factory, stagingOverride: roomy);

        Assert.IsFalse(report.UnderDiskPressure);
        Assert.AreEqual(MediaAssetStorageState.Rejected, await StateAsync(factory, rejected.Id));
    }

    private sealed class LowDiskStore(IAssetStagingStore inner, long free = 1L * StorageRetentionOptions.Gigabyte) : IAssetStagingStore
    {
        public Task<StagedFile> WriteToQuarantineAsync(Stream content, string originalFilename, long maxSizeBytes, CancellationToken cancellationToken) =>
            inner.WriteToQuarantineAsync(content, originalFilename, maxSizeBytes, cancellationToken);

        public Task<Stream> OpenAsync(MediaAssetStorageState zone, string storedFilename, CancellationToken cancellationToken) => inner.OpenAsync(zone, storedFilename, cancellationToken);

        public Task MoveAsync(MediaAssetStorageState fromZone, MediaAssetStorageState toZone, string storedFilename, CancellationToken cancellationToken) =>
            inner.MoveAsync(fromZone, toZone, storedFilename, cancellationToken);

        public Task DeleteAsync(MediaAssetStorageState zone, string storedFilename, CancellationToken cancellationToken) => inner.DeleteAsync(zone, storedFilename, cancellationToken);

        public IReadOnlyList<StoredFileInfo> ListStoredFiles(MediaAssetStorageState zone) => inner.ListStoredFiles(zone);

        public StorageVolumeInfo GetVolumeInfo() => new(free, 1000L * StorageRetentionOptions.Gigabyte);
    }

    private sealed class ThrowOnceStore(IAssetStagingStore inner, string failingName) : IAssetStagingStore
    {
        private bool thrown;

        public Task<StagedFile> WriteToQuarantineAsync(Stream content, string originalFilename, long maxSizeBytes, CancellationToken cancellationToken) =>
            inner.WriteToQuarantineAsync(content, originalFilename, maxSizeBytes, cancellationToken);

        public Task<Stream> OpenAsync(MediaAssetStorageState zone, string storedFilename, CancellationToken cancellationToken) => inner.OpenAsync(zone, storedFilename, cancellationToken);

        public Task MoveAsync(MediaAssetStorageState fromZone, MediaAssetStorageState toZone, string storedFilename, CancellationToken cancellationToken) =>
            inner.MoveAsync(fromZone, toZone, storedFilename, cancellationToken);

        public Task DeleteAsync(MediaAssetStorageState zone, string storedFilename, CancellationToken cancellationToken)
        {
            if (!thrown && storedFilename == failingName)
            {
                thrown = true;
                throw new IOException("disk error");
            }

            return inner.DeleteAsync(zone, storedFilename, cancellationToken);
        }

        public IReadOnlyList<StoredFileInfo> ListStoredFiles(MediaAssetStorageState zone) => inner.ListStoredFiles(zone);

        public StorageVolumeInfo GetVolumeInfo() => inner.GetVolumeInfo();
    }

    private sealed class RaceStore(IStorageMaintenanceStore inner, Func<Task> afterListing) : IStorageMaintenanceStore
    {
        public async Task<IReadOnlyList<RemovableCopy>> ListRemovableCopiesAsync(CancellationToken cancellationToken)
        {
            var listed = await inner.ListRemovableCopiesAsync(cancellationToken);
            await afterListing();
            return listed;
        }

        public Task<IReadOnlyList<MediaAsset>> LoadForRemovalAsync(IReadOnlyCollection<Guid> assetIds, MediaAssetStorageState expectedState, CancellationToken cancellationToken) =>
            inner.LoadForRemovalAsync(assetIds, expectedState, cancellationToken);

        public Task SaveAsync(CancellationToken cancellationToken) => inner.SaveAsync(cancellationToken);

        public Task NoteHeldCopyRemovedAsync(Guid requestId, string note, DateTimeOffset atUtc, CancellationToken cancellationToken) =>
            inner.NoteHeldCopyRemovedAsync(requestId, note, atUtc, cancellationToken);
    }
}
