using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Domain.Catalog;
using FamilyLibrarian.Domain.Providers;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// Proof that the catalog write lock coordinates separate OS processes. A second process (the CatalogConcurrencyHost helper) runs
/// the production resolver, write scope, repository and orphan retirement against the same disposable PostgreSQL database as
/// the in-process host. The test itself, never a participant, holds the catalog lock so that every participant is observed
/// waiting before any is released; nothing here reimplements the lock or the write algorithm.
/// </summary>
[TestClass]
public sealed class CatalogCrossProcessTests
{
    private const string FirstProvider = "demo";
    private const string SecondProvider = "openlibrary";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

    private static readonly DateTimeOffset Past = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static string? connectionString;
    private static string inProcessApplication = string.Empty;
    private static FamilyLibrarianAppFactory? factory;
    private static CatalogHostProcess? hostA;
    private static CatalogHostProcess? hostB;
    private static readonly ConcurrentDictionary<(string Provider, string ExternalId), BookCandidate> Candidates = new();

    /// <summary>Echoes whatever candidate the test registered for the reference, so the in-process side is as deterministic as the helper.</summary>
    private sealed class EchoProvider(string id) : IBookMetadataProvider
    {
        public string Id => id;

        public string DisplayName => $"Echo {id}";

        public Task<BookCandidateSearchPage> SearchAsync(BookSearchQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new BookCandidateSearchPage([], false));

        public Task<BookCandidate?> GetDetailsAsync(string externalId, CancellationToken cancellationToken) =>
            Task.FromResult(Candidates.TryGetValue((id, externalId), out var candidate) ? candidate : null);
    }

    [ClassInitialize]
    public static async Task InitializeAsync(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (PostgresFixture.UnavailableReason is not null)
        {
            return;
        }

        var nonce = Guid.NewGuid().ToString("N")[..12];
        connectionString = await PostgresFixture.CreateSeededDatabaseAsync();
        inProcessApplication = $"fl-inproc-{nonce}";
        factory = new FamilyLibrarianAppFactory(
            new NpgsqlConnectionStringBuilder(connectionString) { ApplicationName = inProcessApplication }.ConnectionString,
            services =>
            {
                services.RemoveAll<IBookMetadataProvider>();
                services.AddSingleton<IBookMetadataProvider>(new EchoProvider(FirstProvider));
                services.AddSingleton<IBookMetadataProvider>(new EchoProvider(SecondProvider));
            });
        // The helper's second provider is enabled by its own configuration; enable it for the in-process host the way an administrator would.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var setting = new ProviderSetting(SecondProvider, Past);
            setting.SetEnabled(true, null, Past);
            database.Add(setting);
            await database.SaveChangesAsync();
        }

        hostA = await CatalogHostProcess.StartAsync(connectionString, $"{nonce}-a", Wait);
        hostB = await CatalogHostProcess.StartAsync(connectionString, $"{nonce}-b", Wait);
    }

    [ClassCleanup]
    public static async Task CleanupAsync()
    {
        if (hostA is not null) await hostA.DisposeAsync();
        if (hostB is not null) await hostB.DisposeAsync();
        if (factory is not null) await factory.DisposeAsync();
    }

    private static string Conn
    {
        get
        {
            if (connectionString is null) Assert.Inconclusive(PostgresFixture.UnavailableReason ?? "No PostgreSQL test container is available.");
            return connectionString;
        }
    }

    private static FamilyLibrarianAppFactory Host
    {
        get
        {
            if (factory is null) Assert.Inconclusive(PostgresFixture.UnavailableReason ?? "No PostgreSQL test container is available.");
            return factory;
        }
    }

    private static readonly string[] RefreshedAndUnchanged = ["Refreshed", "Unchanged"];

    private static readonly string[] ExpectedCurrentPositions = ["demo:3", "openlibrary:2"];

    private static string Unique(string stem) => $"{stem} {Guid.NewGuid():N}";

    private static BookCandidate Candidate(
        string provider, string externalId, string title, string author, string? isbn, string? series = null, string? position = null) =>
        new(provider, $"Echo {provider}", externalId, title, [author], "Fictional.", null, new DateOnly(2020, 1, 1),
            isbn is null ? [] : [new BookEditionCandidate(title, isbn, "Ebook", new DateOnly(2020, 1, 1))],
            series is null ? [] : [new BookSeriesCandidate(series, position, true)]);

    private static Task<CatalogWorkResolution> ResolveInProcessAsync(BookCandidate candidate)
    {
        Candidates[(candidate.ProviderId, candidate.ExternalId)] = candidate;
        return Task.Run(async () =>
        {
            await using var scope = Host.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<CatalogWorkResolver>()
                .ResolveAsync(candidate.ProviderId, candidate.ExternalId, CancellationToken.None);
        });
    }

    private static async Task<T> WithDatabaseAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        await using var scope = Host.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var result = await action(database);
        await database.SaveChangesAsync();
        return result;
    }

    private static bool Ok(JsonElement result) => result.GetProperty("ok").GetBoolean();

    private static async Task<JsonElement> Completed(CatalogHostProcess.Command command) =>
        await command.Result.WaitAsync(Wait);

    private static async Task<(Guid Work, bool Created)> OutcomeOf(CatalogHostProcess.Command command)
    {
        var result = await Completed(command);
        Assert.IsTrue(Ok(result), result.ToString());
        return (result.GetProperty("workId").GetGuid(), result.GetProperty("wasCreated").GetBoolean());
    }

    // ---------------------------------------------------------------- insert races

    [TestMethod]
    public async Task TheSameNewReferenceInTwoProcessesCreatesOneWorkAndOneCreation()
    {
        _ = Conn;
        var title = Unique("Cross process same reference");
        var author = Unique("Cross Author");
        var candidate = Candidate(FirstProvider, "xp-same-1", title, author, "9780000710010");
        await using var probe = new CatalogLockProbe(Conn);
        await probe.HoldAsync(CancellationToken.None);

        var inHelper = hostA!.Resolve(candidate.ProviderId, candidate.ExternalId, candidate);
        var inProcess = ResolveInProcessAsync(candidate);
        await probe.WaitUntilWaitingAsync(hostA.ApplicationName, TimeSpan.FromSeconds(15));
        await probe.WaitUntilWaitingAsync(inProcessApplication, TimeSpan.FromSeconds(15));

        // Both are provably waiting, and neither has written anything.
        Assert.AreEqual(0, await WithDatabaseAsync(db => db.Works.CountAsync(work => work.CanonicalTitle == title)));
        Assert.IsFalse(inProcess.IsCompleted);
        await probe.ReleaseAsync(CancellationToken.None);

        var helperResult = await OutcomeOf(inHelper);
        var processResult = await inProcess.WaitAsync(Wait);

        Assert.AreEqual(helperResult.Work, processResult.Work.Id, "Both processes got the same Work.");
        Assert.AreEqual(1, new[] { helperResult.Created, processResult.WasCreated }.Count(created => created), "Exactly one creation.");
        await WithDatabaseAsync(async db =>
        {
            Assert.AreEqual(1, await db.Works.CountAsync(work => work.CanonicalTitle == title));
            Assert.AreEqual(1, await db.ExternalReferences.CountAsync(reference => reference.ExternalId == "xp-same-1"));
            Assert.AreEqual(1, await db.Authors.CountAsync(item => item.CanonicalName == author));
            return 0;
        });
    }

    [TestMethod]
    public async Task TwoProvidersReportingOneIsbnInTwoProcessesShareOneWork()
    {
        _ = Conn;
        const string isbn = "9780000710027";
        var title = Unique("Cross process shared isbn");
        var author = Unique("Isbn Author");
        var fromHelper = Candidate(FirstProvider, "xp-isbn-demo", title, author, isbn);
        var fromProcess = Candidate(SecondProvider, "xp-isbn-ol", title, author, isbn);
        await using var probe = new CatalogLockProbe(Conn);
        await probe.HoldAsync(CancellationToken.None);

        var inHelper = hostA!.Resolve(fromHelper.ProviderId, fromHelper.ExternalId, fromHelper);
        var inProcess = ResolveInProcessAsync(fromProcess);
        await probe.WaitUntilWaitingAsync(hostA.ApplicationName, TimeSpan.FromSeconds(15));
        await probe.WaitUntilWaitingAsync(inProcessApplication, TimeSpan.FromSeconds(15));
        await probe.ReleaseAsync(CancellationToken.None);

        var helperResult = await OutcomeOf(inHelper);
        var processResult = await inProcess.WaitAsync(Wait);

        Assert.AreEqual(helperResult.Work, processResult.Work.Id, "The one ISBN is one Work whichever provider and process reported it.");
        Assert.AreEqual(1, new[] { helperResult.Created, processResult.WasCreated }.Count(created => created), "One creation, one attach.");
        await WithDatabaseAsync(async db =>
        {
            Assert.AreEqual(1, await db.Works.CountAsync(work => work.CanonicalTitle == title));
            Assert.AreEqual(2, await db.ExternalReferences.CountAsync(reference =>
                reference.ExternalId == "xp-isbn-demo" || reference.ExternalId == "xp-isbn-ol"));
            Assert.AreEqual(1, await db.Editions.CountAsync(edition => edition.Isbn13 == isbn));
            return 0;
        });
    }

    [TestMethod]
    public async Task DifferentBooksSharingAnAbsentAuthorAndSeriesInTwoProcessesCreateThemOnce()
    {
        _ = Conn;
        var author = Unique("Shared New Author");
        var series = Unique("Shared New Series");
        var first = Candidate(FirstProvider, "xp-new-1", Unique("Series book one"), author, "9780000710034", series, "1");
        var second = Candidate(FirstProvider, "xp-new-2", Unique("Series book two"), author, "9780000710041", series, "2");
        await using var probe = new CatalogLockProbe(Conn);
        await probe.HoldAsync(CancellationToken.None);

        var inA = hostA!.Resolve(first.ProviderId, first.ExternalId, first);
        var inB = hostB!.Resolve(second.ProviderId, second.ExternalId, second);
        await probe.WaitUntilWaitingAsync(hostA.ApplicationName, TimeSpan.FromSeconds(15));
        await probe.WaitUntilWaitingAsync(hostB.ApplicationName, TimeSpan.FromSeconds(15));
        await probe.ReleaseAsync(CancellationToken.None);

        var a = await OutcomeOf(inA);
        var b = await OutcomeOf(inB);

        Assert.AreNotEqual(a.Work, b.Work);
        Assert.IsTrue(a.Created && b.Created, "Each distinct book is created exactly once.");
        await WithDatabaseAsync(async db =>
        {
            Assert.AreEqual(1, await db.Authors.CountAsync(item => item.CanonicalName == author), "One shared Author, not a duplicate per process.");
            Assert.AreEqual(1, await db.Series.CountAsync(item => item.Name == series), "One shared Series.");
            var seriesRow = await db.Series.Include(item => item.Entries).SingleAsync(item => item.Name == series);
            Assert.AreEqual(2, seriesRow.Entries.Count, "Both books joined the one Series.");
            return 0;
        });
    }

    // ---------------------------------------------------------------- retirement versus resolution

    private static async Task<Guid> SeedOrphanAsync(string title, string isbn, string reference) =>
        await WithDatabaseAsync(async db =>
        {
            var reader = await db.Users.Where(user => user.Email == WebTestFixture.UserEmail).Select(user => user.Id).SingleAsync();
            var work = new Work(title, null, null, null, PublicationStatus.Published, Past);
            work.AddEdition(new Edition(work.Id, title, EditionFormat.Ebook, isbn, null, Past));
            db.Works.Add(work);
            db.ExternalReferences.Add(new ExternalReference(FirstProvider, ExternalReferenceEntityType.Work, work.Id, reference, Past));
            var request = new BookRequest(reader, work.Id, [RequestMediaType.Ebook], null, Past);
            request.Withdraw(reader, Past);
            db.BookRequests.Add(request);
            return work.Id;
        });

    [TestMethod]
    public async Task RetirementThatWinsTheLockFirstLeavesResolveToCreateAFreshWorkNotAttachToTheRetiredOne()
    {
        _ = Conn;
        const string isbn = "9780000710058";
        var title = Unique("Orphan retired first");
        var orphan = await SeedOrphanAsync(title, isbn, "xp-orphan-1");
        var candidate = Candidate(SecondProvider, "xp-orphan-new-1", title, Unique("Orphan Author"), isbn);
        await using var probe = new CatalogLockProbe(Conn);
        await probe.HoldAsync(CancellationToken.None);

        // Retirement queues for the lock first, then the resolve whose provider fetch already happened.
        var retire = hostA!.Retire();
        await probe.WaitUntilWaitingAsync(hostA.ApplicationName, TimeSpan.FromSeconds(15));
        var resolve = ResolveInProcessAsync(candidate);
        await probe.WaitUntilWaitingAsync(inProcessApplication, TimeSpan.FromSeconds(15));
        await probe.ReleaseAsync(CancellationToken.None);

        var retired = await Completed(retire);
        var resolved = await resolve.WaitAsync(Wait);

        Assert.IsTrue(Ok(retired), retired.ToString());
        Assert.IsTrue(retired.GetProperty("retired").GetInt32() >= 1, "Retirement ran first and retired the orphan.");
        Assert.AreNotEqual(orphan, resolved.Work.Id, "A retired Work is never attached to, even though the provider fetch began earlier.");
        Assert.IsTrue(resolved.WasCreated);
        await WithDatabaseAsync(async db =>
        {
            Assert.IsTrue((await db.Works.SingleAsync(work => work.Id == orphan)).IsRetired);
            Assert.AreEqual(0, await db.Editions.CountAsync(edition => edition.WorkId == orphan), "Retirement released the unique ISBN.");
            Assert.AreEqual(0, await db.ExternalReferences.CountAsync(reference => reference.EntityId == orphan));
            Assert.AreEqual(1, await db.Editions.CountAsync(edition => edition.Isbn13 == isbn && edition.WorkId == resolved.Work.Id));
            return 0;
        });
    }

    [TestMethod]
    public async Task ResolveThatWinsTheLockFirstIsFollowedByAConsistentRetirement()
    {
        _ = Conn;
        const string isbn = "9780000710065";
        var title = Unique("Orphan resolved first");
        var orphan = await SeedOrphanAsync(title, isbn, "xp-orphan-2");
        var candidate = Candidate(SecondProvider, "xp-orphan-new-2", title, Unique("Orphan Author"), isbn);
        await using var probe = new CatalogLockProbe(Conn);
        await probe.HoldAsync(CancellationToken.None);

        var resolve = ResolveInProcessAsync(candidate);
        await probe.WaitUntilWaitingAsync(inProcessApplication, TimeSpan.FromSeconds(15));
        var retire = hostA!.Retire();
        await probe.WaitUntilWaitingAsync(hostA.ApplicationName, TimeSpan.FromSeconds(15));
        await probe.ReleaseAsync(CancellationToken.None);

        var resolved = await resolve.WaitAsync(Wait);
        var retired = await Completed(retire);

        Assert.IsTrue(Ok(retired), retired.ToString());
        Assert.AreEqual(orphan, resolved.Work.Id, "Resolve ran first and attached through the shared ISBN to the not-yet-retired Work.");
        Assert.AreEqual(CatalogResolutionOutcome.Attached, resolved.Outcome);
        await WithDatabaseAsync(async db =>
        {
            // Retirement then ran on a coherent snapshot: it does not depend on references, so it retired the Work and released
            // everything unique together, leaving no half-state. (Request creation versus retirement is a separate, unchanged race.)
            Assert.IsTrue((await db.Works.SingleAsync(work => work.Id == orphan)).IsRetired);
            Assert.AreEqual(0, await db.Editions.CountAsync(edition => edition.WorkId == orphan));
            Assert.AreEqual(0, await db.ExternalReferences.CountAsync(reference => reference.EntityId == orphan));
            return 0;
        });
    }

    // ---------------------------------------------------------------- bounded waits and release

    [TestMethod]
    public async Task AWaitingResolveTimesOutAsBusyAfterTheProductionBoundAndWritesNothing()
    {
        _ = Conn;
        var title = Unique("Busy resolve");
        var candidate = Candidate(FirstProvider, "xp-busy-1", title, Unique("Busy Author"), "9780000710072");
        await using var probe = new CatalogLockProbe(Conn);
        await probe.HoldAsync(CancellationToken.None);
        var timer = Stopwatch.StartNew();

        var resolve = ResolveInProcessAsync(candidate);
        var thrown = await Assert.ThrowsExactlyAsync<CatalogResolutionException>(async () => await resolve.WaitAsync(Wait));
        timer.Stop();

        Assert.AreEqual(CatalogResolutionFault.Busy, thrown.Fault);
        Assert.IsTrue(timer.Elapsed > TimeSpan.FromSeconds(4) && timer.Elapsed < TimeSpan.FromSeconds(30), $"Waited {timer.Elapsed}: the 5 s lock_timeout bounds the wait.");
        Assert.AreEqual(0, await WithDatabaseAsync(db => db.Works.CountAsync(work => work.CanonicalTitle == title)));
        await probe.ReleaseAsync(CancellationToken.None);
        Assert.AreEqual(0, await probe.CountHoldingAsync(inProcessApplication, CancellationToken.None), "No backend of the timed-out attempt holds the lock.");

        // The same operation then succeeds immediately: nothing was stranded.
        var again = await ResolveInProcessAsync(candidate).WaitAsync(Wait);
        Assert.IsTrue(again.WasCreated);
    }

    [TestMethod]
    public async Task ACancelledWaitPropagatesAsCancellationNotAFaultAndReleasesTheWait()
    {
        _ = Conn;
        var title = Unique("Cancelled resolve");
        var candidate = Candidate(FirstProvider, "xp-cancel-1", title, Unique("Cancel Author"), "9780000710089");
        Candidates[(candidate.ProviderId, candidate.ExternalId)] = candidate;
        await using var probe = new CatalogLockProbe(Conn);
        await probe.HoldAsync(CancellationToken.None);
        using var cancel = new CancellationTokenSource();

        await using var scope = Host.Services.CreateAsyncScope();
        var resolve = Task.Run(() => scope.ServiceProvider.GetRequiredService<CatalogWorkResolver>()
            .ResolveAsync(candidate.ProviderId, candidate.ExternalId, cancel.Token));
        await probe.WaitUntilWaitingAsync(inProcessApplication, TimeSpan.FromSeconds(15));
        await cancel.CancelAsync();

        var thrown = await Assert.ThrowsAsync<OperationCanceledException>(async () => await resolve.WaitAsync(Wait));
        Assert.IsNotInstanceOfType<CatalogResolutionException>(thrown, "The caller's own cancellation is not masked as a catalog fault.");
        Assert.AreEqual(0, await WithDatabaseAsync(db => db.Works.CountAsync(work => work.CanonicalTitle == title)));
        await probe.ReleaseAsync(CancellationToken.None);
        Assert.AreEqual(0, await probe.CountHoldingAsync(inProcessApplication, CancellationToken.None));
    }

    [TestMethod]
    public async Task TheLockIsReleasedWhenAnAttemptEndsSoTheNextWriterIsNotBlocked()
    {
        _ = Conn;
        var candidate = Candidate(FirstProvider, "xp-release-1", Unique("Release"), Unique("Release Author"), "9780000710096");
        var created = await ResolveInProcessAsync(candidate).WaitAsync(Wait);
        Assert.IsTrue(created.WasCreated);

        Assert.AreEqual(0, await new CatalogLockProbe(Conn).CountHoldingAsync(inProcessApplication, CancellationToken.None),
            "Disposing the attempt's scope released the transaction-scoped lock.");
        await using var probe = new CatalogLockProbe(Conn);
        var timer = Stopwatch.StartNew();
        await probe.HoldAsync(CancellationToken.None);
        Assert.IsTrue(timer.Elapsed < TimeSpan.FromSeconds(4), "A new holder acquires the lock at once.");
    }

    // ---------------------------------------------------------------- update races (activated observation evidence)

    [TestMethod]
    public async Task ContradictoryPositionRefreshesInTwoProcessesKeepBothClaimsAndTheDeterministicSelection()
    {
        _ = Conn;
        const string isbn = "9780000710102";
        var title = Unique("Cross process positions");
        var author = Unique("Position Author");
        var series = Unique("Position Series");
        var demo = Candidate(FirstProvider, "xp-pos-demo", title, author, isbn, series, "1");
        var openLibrary = Candidate(SecondProvider, "xp-pos-ol", title, author, isbn, series, "1");
        var created = await ResolveInProcessAsync(demo).WaitAsync(Wait);
        var attached = await ResolveInProcessAsync(openLibrary).WaitAsync(Wait);
        Assert.AreEqual(created.Work.Id, attached.Work.Id);
        var workId = created.Work.Id;

        // Both sources now say something new, and the two sources contradict each other: demo -> 3, Open Library -> 2. Both
        // processes hold the same, current view of both sources and refresh the same Work at the same moment, each starting from its
        // own stale read of the claims.
        await Completed(hostA!.Record(FirstProvider, "xp-pos-demo", Candidate(FirstProvider, "xp-pos-demo", title, author, isbn, series, "3")));
        await Completed(hostA.Record(SecondProvider, "xp-pos-ol", Candidate(SecondProvider, "xp-pos-ol", title, author, isbn, series, "2")));
        Candidates[(FirstProvider, "xp-pos-demo")] = Candidate(FirstProvider, "xp-pos-demo", title, author, isbn, series, "3");
        Candidates[(SecondProvider, "xp-pos-ol")] = Candidate(SecondProvider, "xp-pos-ol", title, author, isbn, series, "2");

        await using var probe = new CatalogLockProbe(Conn);
        await probe.HoldAsync(CancellationToken.None);
        var inHelper = hostA.Refresh(workId);
        var inProcess = Task.Run(async () =>
        {
            await using var scope = Host.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<FamilyLibrarian.Application.Catalog.Observation.CatalogObservationService>()
                .RefreshWorkAsync(workId, CancellationToken.None);
        });
        await probe.WaitUntilWaitingAsync(hostA.ApplicationName, TimeSpan.FromSeconds(15));
        await probe.WaitUntilWaitingAsync(inProcessApplication, TimeSpan.FromSeconds(15));
        await probe.ReleaseAsync(CancellationToken.None);

        var helperReport = await Completed(inHelper);
        var processReport = await inProcess.WaitAsync(Wait);

        Assert.IsTrue(Ok(helperReport), helperReport.ToString());
        Assert.AreEqual(FamilyLibrarian.Application.Catalog.Observation.WorkRefreshStatus.Completed, processReport.Status);

        // For each source exactly one process wrote the new statement; the other, running after it under the lock, found it already
        // there and changed nothing: concurrent refreshes never duplicate an effect.
        var helperSteps = helperReport.GetProperty("steps").EnumerateArray()
            .Select(step => (Provider: step.GetProperty("providerId").GetString()!, Status: step.GetProperty("status").GetString()!)).ToArray();
        foreach (var source in new[] { FirstProvider, SecondProvider })
        {
            var statuses = helperSteps.Where(step => step.Provider == source).Select(step => step.Status)
                .Concat(processReport.Providers.Where(step => step.ProviderId == source).Select(step => step.Status.ToString())).Order(StringComparer.Ordinal).ToArray();
            CollectionAssert.AreEqual(RefreshedAndUnchanged, statuses, $"{source}: {string.Join(',', statuses)}");
        }

        await WithDatabaseAsync(async db =>
        {
            var claims = await db.MetadataClaims.AsNoTracking()
                .Where(claim => claim.WorkId == workId && claim.Field == MetadataClaimField.SeriesPosition).ToListAsync();
            Assert.AreEqual(4, claims.Count, "Both contradictory statements and both earlier ones survive; none was lost to the race.");
            var current = claims.Where(claim => claim.IsCurrent).Select(claim => $"{claim.ProviderId}:{PositionOf(claim)}").Order(StringComparer.Ordinal).ToArray();
            CollectionAssert.AreEqual(ExpectedCurrentPositions, current);
            var selected = claims.Single(claim => claim.IsSelected);
            Assert.AreEqual("openlibrary", selected.ProviderId, "Equal support falls to the lowest number, whichever process committed last.");
            var entry = await db.SeriesEntries.AsNoTracking().SingleAsync(item => item.WorkId == workId);
            Assert.AreEqual(SeriesPositionState.Conflicted, entry.PositionState);
            Assert.AreEqual(2m, entry.PositionSort);
            Assert.AreEqual(1, await db.Works.CountAsync(work => work.CanonicalTitle == title));
            return 0;
        });
    }

    private static string? PositionOf(MetadataClaim claim)
    {
        using var document = JsonDocument.Parse(claim.ValueJson);
        return document.RootElement.GetProperty("label").GetString();
    }
}
