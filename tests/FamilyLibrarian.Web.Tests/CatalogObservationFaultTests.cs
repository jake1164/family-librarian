using System.Data.Common;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Catalog.Observation;
using FamilyLibrarian.Domain.Catalog;
using FamilyLibrarian.Domain.Following;
using FamilyLibrarian.Domain.Notifications;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static FamilyLibrarian.Web.Tests.Harness.RigBooks;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// Failure and recovery of the activated observation effects, against real PostgreSQL and the production write scope. These cases
/// start where a reference already exists, so a verification that only looked for the reference would pass vacuously: the new
/// snapshot and claim values must be found (or proven absent) by their own natural keys. Every case asserts the rows PostgreSQL
/// actually holds, not only the reported outcome.
/// </summary>
[TestClass]
public sealed class CatalogObservationFaultTests
{
    private const string Isbn1 = "9784000000001";
    private const string Isbn2 = "9784000000018";

    /// <summary>Faults at commit and save boundaries, counted from the moment a test arms them (so earlier setup never matters).</summary>
    private sealed class Hooks : DbTransactionInterceptor
    {
        private int committing;
        private int committed;
        private int baselineCommitting;
        private int baselineCommitted;

        public Func<DbTransaction, int, Task>? BeforeCommit { get; set; }

        public Func<int, Task>? AfterCommit { get; set; }

        public void Arm()
        {
            baselineCommitting = Volatile.Read(ref committing);
            baselineCommitted = Volatile.Read(ref committed);
        }

        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            var ordinal = Interlocked.Increment(ref committing) - baselineCommitting;
            if (BeforeCommit is not null) await BeforeCommit(transaction, ordinal);
            return result;
        }

        public override async Task TransactionCommittedAsync(
            DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            var ordinal = Interlocked.Increment(ref committed) - baselineCommitted;
            if (AfterCommit is not null) await AfterCommit(ordinal);
        }
    }

    private sealed class SaveHooks : SaveChangesInterceptor
    {
        private int saves;
        private int baseline;

        public Func<int, Task>? BeforeSave { get; set; }

        public int SavesSinceArmed => Volatile.Read(ref saves) - baseline;

        public void Arm() => baseline = Volatile.Read(ref saves);

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var ordinal = Interlocked.Increment(ref saves) - baseline;
            if (BeforeSave is not null) await BeforeSave(ordinal);
            return result;
        }
    }

    private static async Task<(ObservationRig Rig, Hooks Commits, SaveHooks Saves)> NewRigAsync(bool enableOpenLibrary = false)
    {
        var commits = new Hooks();
        var saves = new SaveHooks();
        var rig = await ObservationRig.CreateAsync(
            enableOpenLibrary,
            configure: services => services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(commits, saves)));
        return (rig, commits, saves);
    }

    private static NpgsqlException TransportFailure() => new("Injected connection loss.", new IOException("Injected transport failure."));

    private static BookCandidate Base(string description = "First words.") =>
        Book("demo", "r1", "Fault Book", ["Fault Writer"], [Edition("Fault Book", Isbn1, "Ebook", "en")], [Series("Fault Cycle", "1")], description,
            new DateOnly(1990, 1, 1), DatePrecision.Year, DateBasis.Original, "en", null, 200);

    private static async Task<Guid> ResolvedAsync(ObservationRig rig)
    {
        rig.Demo.Records["r1"] = Base();
        return (await rig.ResolveAsync("demo", "r1")).Work.Id;
    }

    private static ProviderRefreshResult DemoStep(WorkRefreshReport report) => report.Providers.Single(step => step.ProviderId == "demo");

    // ---------------------------------------------------------------------------------------------------- refresh

    [TestMethod]
    public async Task ARefreshCommitThatNeverReachedTheServerIsVerifiedAbsentByItsNewClaimsThenRetriedOnce()
    {
        var (rig, commits, _) = await NewRigAsync();
        await using var disposableRig = rig;
        var workId = await ResolvedAsync(rig);
        rig.Demo.Records["r1"] = Base("Second words.");
        commits.Arm();
        commits.BeforeCommit = (_, ordinal) => ordinal == 1 ? throw TransportFailure() : Task.CompletedTask;

        var report = await rig.RefreshAsync(workId);

        // The reference already existed, so verification that stopped at "the reference is there" would have called the failed
        // attempt committed and silently lost the refresh. It looks for the new snapshot and claim values, finds none, and retries.
        Assert.AreEqual(ProviderRefreshStatus.Refreshed, DemoStep(report).Status);
        Assert.AreEqual(2, rig.Demo.DetailCalls, "One fetch for the resolve and one for the refresh: a retry never refetches.");
        Assert.AreEqual("Second words.", (await rig.LoadWorkAsync(workId)).Description);
        var descriptions = (await rig.ClaimsAsync(workId)).Where(claim => claim.Field == MetadataClaimField.Description).ToArray();
        Assert.HasCount(2, descriptions, "Applied exactly once: the old statement as history and the new one as current.");
        Assert.AreEqual(1, descriptions.Count(claim => claim.IsCurrent && claim.IsSelected));
        Assert.HasCount(2, await rig.ObservationsAsync(workId), "One new snapshot, not two.");
    }

    [TestMethod]
    public async Task ARefreshWhoseCommitSucceededButLostItsReplyIsRecoveredNeverReplayedAndNeverDuplicated()
    {
        var (rig, commits, _) = await NewRigAsync();
        await using var disposableRig = rig;
        var workId = await ResolvedAsync(rig);
        rig.Demo.Records["r1"] = Base("Second words.") with { Editions = [Edition("Fault Book", Isbn1, "Ebook", "en"), Edition("Fault Book", Isbn2, "Paperback", "en")] };
        var noticesBefore = await rig.DbAsync(database => database.NotificationEvents.CountAsync());
        commits.Arm();
        commits.AfterCommit = ordinal => ordinal == 1 ? throw TransportFailure() : Task.CompletedTask;

        var report = await rig.RefreshAsync(workId);

        Assert.AreEqual(ProviderRefreshStatus.Refreshed, DemoStep(report).Status, "The real outcome is reported, not a replay and not a failure.");
        Assert.AreEqual(2, rig.Demo.DetailCalls);
        var work = await rig.LoadWorkAsync(workId);
        Assert.HasCount(2, work.Editions, "The edition was added once.");
        Assert.AreEqual("Second words.", work.Description);
        Assert.HasCount(2, (await rig.ClaimsAsync(workId)).Where(claim => claim.Field == MetadataClaimField.Description).ToArray());
        Assert.HasCount(2, await rig.ObservationsAsync(workId));
        Assert.AreEqual(noticesBefore, await rig.DbAsync(database => database.NotificationEvents.CountAsync()), "A refresh never notifies, recovered or not.");
    }

    [TestMethod]
    public async Task AnUnverifiableRefreshCommitIsReportedUnknownAndALaterIdenticalRefreshIsUnchanged()
    {
        var (rig, commits, _) = await NewRigAsync();
        await using var disposableRig = rig;
        var workId = await ResolvedAsync(rig);
        rig.Demo.Records["r1"] = Base("Second words.");
        await using var probe = new CatalogLockProbe(rig.Connection);
        commits.Arm();
        commits.AfterCommit = async ordinal =>
        {
            if (ordinal != 1) return;
            // The commit really happened, the reply is lost, and the verification cannot get the lock to look.
            await probe.HoldAsync(CancellationToken.None);
            throw TransportFailure();
        };

        var report = await rig.RefreshAsync(workId);
        await probe.ReleaseAsync(CancellationToken.None);

        Assert.AreEqual(ProviderRefreshStatus.OutcomeUnknown, DemoStep(report).Status, "Neither 'refreshed' nor 'rolled back' is claimed.");
        Assert.AreEqual(WorkRefreshStatus.Partial, report.Status);
        Assert.AreEqual("Second words.", (await rig.LoadWorkAsync(workId)).Description, "The change was in fact committed.");

        commits.AfterCommit = null;
        var later = await rig.RefreshAsync(workId);
        Assert.AreEqual(ProviderRefreshStatus.Unchanged, DemoStep(later).Status, "Repeating it creates no duplicate effect: the evidence is already there.");
        Assert.HasCount(2, (await rig.ClaimsAsync(workId)).Where(claim => claim.Field == MetadataClaimField.Description).ToArray());
        Assert.HasCount(2, await rig.ObservationsAsync(workId));
    }

    [TestMethod]
    public async Task ARefreshThatLosesAnIsbnRaceIsRetriedAgainstFreshStateAndNeverAdoptsTheContestedIsbn()
    {
        var (rig, _, saves) = await NewRigAsync();
        await using var disposableRig = rig;
        var workId = await ResolvedAsync(rig);
        rig.Demo.Records["r1"] = Base("Second words.") with { Editions = [Edition("Fault Book", Isbn1, "Ebook", "en"), Edition("Fault Book", Isbn2, "Paperback", "en")] };
        saves.Arm();
        saves.BeforeSave = async ordinal =>
        {
            if (ordinal != 1) return;
            // A writer that ignores the catalog lock takes the ISBN this refresh was about to add, between its evaluation and its save.
            await using var other = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(rig.Connection, options => options.MigrationsHistoryTable("__EFMigrationsHistory", "identity")).Options);
            var work = new Work("Outsider", null, null, null, PublicationStatus.Published, ObservationRig.Start);
            work.AddEdition(new Edition(work.Id, "Outsider", EditionFormat.Ebook, Isbn2, null, ObservationRig.Start));
            other.Works.Add(work);
            await other.SaveChangesAsync();
        };

        var report = await rig.RefreshAsync(workId);

        // The retry re-reads: the ISBN now belongs to another Work, so it is excluded, and the rest of the refresh still lands.
        Assert.AreEqual(ProviderRefreshStatus.Refreshed, DemoStep(report).Status);
        Assert.AreEqual(2, rig.Demo.DetailCalls);
        var work2 = await rig.LoadWorkAsync(workId);
        CollectionAssert.AreEqual(new[] { Isbn1 }, work2.Editions.Select(edition => edition.Isbn13).ToArray());
        Assert.AreEqual("Second words.", work2.Description);
        Assert.AreEqual(1, await rig.DbAsync(database => database.Editions.CountAsync(edition => edition.Isbn13 == Isbn2)), "Only the winner holds the ISBN.");
    }

    // ---------------------------------------------------------------------------------------------------- V2: later cooperating writes

    [TestMethod]
    public async Task ALaterCooperatingRefreshThatSupersededTheCommittedClaimsDoesNotTurnRecoveryIntoAnError()
    {
        var (rig, commits, _) = await NewRigAsync();
        await using var disposableRig = rig;
        var workId = await ResolvedAsync(rig);
        rig.Demo.Records["r1"] = Base("Second words.");
        var noticesBefore = await rig.DbAsync(database => database.NotificationEvents.CountAsync());
        commits.Arm();
        commits.AfterCommit = async ordinal =>
        {
            if (ordinal != 1) return;

            // Refresh B has committed. Before its reply is lost, a real second production refresh C commits and legitimately
            // supersedes B's claims.
            rig.Clock.Advance(TimeSpan.FromMinutes(5));
            rig.Demo.Records["r1"] = Base("Third words.");
            Assert.AreEqual(ProviderRefreshStatus.Refreshed, DemoStep(await rig.RefreshAsync(workId)).Status);
            throw TransportFailure();
        };

        var report = await rig.RefreshAsync(workId);
        commits.AfterCommit = null;

        Assert.AreEqual(ProviderRefreshStatus.Refreshed, DemoStep(report).Status, "B's commit is proven by its own rows, which still exist as history.");
        Assert.AreEqual(3, rig.Demo.DetailCalls, "The resolve, B and C: recovery never refetches and never replays.");
        Assert.AreEqual("Third words.", (await rig.LoadWorkAsync(workId)).Description, "C is not overwritten by B.");
        var descriptions = (await rig.ClaimsAsync(workId)).Where(claim => claim.Field == MetadataClaimField.Description).ToArray();
        Assert.HasCount(3, descriptions);
        Assert.IsTrue(descriptions.Single(claim => claim.IsCurrent).IsSelected);
        Assert.IsTrue(descriptions.Single(claim => claim.IsCurrent).ValueJson.Contains("Third words.", StringComparison.Ordinal));
        Assert.AreEqual(noticesBefore, await rig.DbAsync(database => database.NotificationEvents.CountAsync()), "Recovery notifies nobody.");
    }

    [TestMethod]
    public async Task AReinstatingRefreshWhoseCommitNeverReachedTheServerIsNotProvedByTheHistoricalValueItReinstates()
    {
        var (rig, commits, _) = await NewRigAsync();
        await using var disposableRig = rig;
        var workId = await ResolvedAsync(rig);
        rig.Clock.Advance(TimeSpan.FromDays(1));
        rig.Demo.Records["r1"] = Base("Second words.");
        await rig.RefreshAsync(workId);
        rig.Clock.Advance(TimeSpan.FromDays(1));
        rig.Demo.Records["r1"] = Base("First words.");
        commits.Arm();
        commits.BeforeCommit = (_, ordinal) => ordinal == 1 ? throw TransportFailure() : Task.CompletedTask;

        var report = await rig.RefreshAsync(workId);

        // Statement A already exists as superseded history. If that alone proved the new attempt, the reinstatement would be lost.
        Assert.AreEqual(ProviderRefreshStatus.Refreshed, DemoStep(report).Status);
        Assert.AreEqual(3, rig.Demo.DetailCalls, "The one retry reuses the fetched record.");
        Assert.AreEqual("First words.", (await rig.LoadWorkAsync(workId)).Description);
        var descriptions = (await rig.ClaimsAsync(workId)).Where(claim => claim.Field == MetadataClaimField.Description).ToArray();
        Assert.HasCount(2, descriptions, "The revisited row, not a duplicate.");
        Assert.IsTrue(descriptions.Single(claim => claim.IsCurrent).ValueJson.Contains("First words.", StringComparison.Ordinal));
        Assert.IsTrue(descriptions.Single(claim => claim.IsCurrent).IsSelected);
    }

    [TestMethod]
    public async Task ARefreshWhoseOnlyEvidenceWasPrunedByLaterWritersIsStillRecoveredFromTheDatabasesRecordOfItsTransaction()
    {
        var (rig, commits, _) = await NewRigAsync();
        await using var disposableRig = rig;
        var workId = await ResolvedAsync(rig);
        rig.Clock.Advance(TimeSpan.FromDays(1));   // distinct times: which snapshot is "first" must never depend on a tie
        rig.Demo.Records["r1"] = Base() with { Authors = ["Contra 0"] };
        commits.Arm();
        commits.AfterCommit = async ordinal =>
        {
            if (ordinal != 1) return;

            // The contradiction snapshot B committed; four later refreshes then push it out of the retained snapshots.
            for (var later = 1; later <= 4; later++)
            {
                rig.Clock.Advance(TimeSpan.FromMinutes(1));
                rig.Demo.Records["r1"] = Base() with { Authors = [$"Contra {later}"] };
                Assert.AreEqual(ProviderRefreshStatus.Contradiction, DemoStep(await rig.RefreshAsync(workId)).Status);
            }

            throw TransportFailure();
        };

        var report = await rig.RefreshAsync(workId);
        commits.AfterCommit = null;

        // No row of B's survives, so a search of the rows could say nothing. The database's own record of B's transaction says it
        // committed: the real outcome is reported, and nothing is replayed over the four later writers.
        Assert.AreEqual(ProviderRefreshStatus.Contradiction, DemoStep(report).Status);
        Assert.AreEqual(6, rig.Demo.DetailCalls);
        var snapshots = await rig.ObservationsAsync(workId);
        Assert.HasCount(4, snapshots, "The first observation plus the latest three; the stale one was not written again.");
        Assert.IsFalse(snapshots.Any(snapshot => snapshot.Snapshot.Contains("Contra 0", StringComparison.Ordinal)));
    }

    // ---------------------------------------------------------------------------------------------------- W1: proof that does not depend on clocks

    /// <summary>How the rig's clock behaves between the steps of a W1 case: it stands still, it has finer ticks than PostgreSQL stores, or it runs backwards.</summary>
    private static void StepClock(ObservationRig rig, string mode)
    {
        switch (mode)
        {
            case "backwards":
                rig.Clock.Advance(TimeSpan.FromHours(-1));
                break;
            case "subMicrosecond":
                // Below PostgreSQL's microsecond resolution: rounding must not make two readings differ, or tie, differently.
                rig.Clock.Advance(TimeSpan.FromTicks(3));
                break;
        }
    }

    [TestMethod]
    [DataRow("tied")]
    [DataRow("subMicrosecond")]
    [DataRow("backwards")]
    public async Task AReinstatementThatRolledBackIsNotProvedByTheHistoricalRowItWouldReinstateWhateverTheClockDid(string mode)
    {
        var (rig, commits, _) = await NewRigAsync();
        await using var disposableRig = rig;
        var workId = await ResolvedAsync(rig);                 // statement A
        StepClock(rig, mode);
        rig.Demo.Records["r1"] = Base("Second words.");          // statement B, now current; A is history
        Assert.AreEqual(ProviderRefreshStatus.Refreshed, DemoStep(await rig.RefreshAsync(workId)).Status);
        StepClock(rig, mode);
        rig.Demo.Records["r1"] = Base();                        // A again, from a clock reading that ties with, or precedes, A's own
        var noticesBefore = await rig.DbAsync(database => database.NotificationEvents.CountAsync());
        commits.Arm();
        commits.BeforeCommit = (_, ordinal) => ordinal == 1 ? throw TransportFailure() : Task.CompletedTask;

        var report = await rig.RefreshAsync(workId);
        commits.BeforeCommit = null;

        // The attempt never reached the server, so its transaction is recorded as rolled back and nothing else touched the record:
        // the one retry applies A. What must never happen is "Refreshed" while B is still current.
        var status = DemoStep(report).Status;
        var description = (await rig.LoadWorkAsync(workId)).Description;
        if (status == ProviderRefreshStatus.Refreshed)
        {
            Assert.AreEqual("First words.", description, "A reported refresh is a real one.");
        }

        Assert.AreEqual(ProviderRefreshStatus.Refreshed, status, "Proven rolled back and untouched since: retried once.");
        Assert.AreEqual(3, rig.Demo.DetailCalls, "The retry reuses the fetched record.");
        var descriptions = (await rig.ClaimsAsync(workId)).Where(claim => claim.Field == MetadataClaimField.Description).ToArray();
        Assert.HasCount(2, descriptions, "The revisited row, not a duplicate.");
        Assert.IsTrue(descriptions.Single(claim => claim.IsCurrent).ValueJson.Contains("First words.", StringComparison.Ordinal));
        Assert.IsTrue(descriptions.Single(claim => claim.IsCurrent).IsSelected);
        Assert.AreEqual(noticesBefore, await rig.DbAsync(database => database.NotificationEvents.CountAsync()));
    }

    [TestMethod]
    [DataRow("tied")]
    [DataRow("subMicrosecond")]
    [DataRow("backwards")]
    public async Task ALaterCommittedRefreshIsNeverOverwrittenByAnAbandonedAttemptWhateverTheClockDid(string mode)
    {
        var (rig, commits, _) = await NewRigAsync();
        await using var disposableRig = rig;
        var workId = await ResolvedAsync(rig);                 // statement A
        rig.Demo.Records["r1"] = Base("Second words.");          // attempt B
        var noticesBefore = await rig.DbAsync(database => database.NotificationEvents.CountAsync());
        commits.Arm();
        commits.BeforeCommit = async (transaction, ordinal) =>
        {
            if (ordinal != 1) return;

            // B's real transaction ends (releasing the catalog lock, as a lost connection would), then a genuine production refresh
            // C commits from a clock reading that ties with, or precedes, B's. Only then does B's commit report its failure.
            await transaction.RollbackAsync();
            StepClock(rig, mode);
            rig.Demo.Records["r1"] = Base("Third words.");
            Assert.AreEqual(ProviderRefreshStatus.Refreshed, DemoStep(await rig.RefreshAsync(workId)).Status);
            throw TransportFailure();
        };

        var report = await rig.RefreshAsync(workId);
        commits.BeforeCommit = null;

        Assert.AreEqual(ProviderRefreshStatus.OutcomeUnknown, DemoStep(report).Status, "B did not commit, but replaying it would overwrite C: unknown, not Refreshed.");
        Assert.AreEqual(WorkRefreshStatus.Partial, report.Status);
        Assert.AreEqual(3, rig.Demo.DetailCalls, "The resolve, B and C: no replay, no extra fetch.");
        Assert.AreEqual("Third words.", (await rig.LoadWorkAsync(workId)).Description, "C survives.");
        var descriptions = (await rig.ClaimsAsync(workId)).Where(claim => claim.Field == MetadataClaimField.Description).ToArray();
        Assert.HasCount(2, descriptions, "A as history and C as current; B never existed.");
        Assert.IsTrue(descriptions.Single(claim => claim.IsCurrent).ValueJson.Contains("Third words.", StringComparison.Ordinal));
        Assert.IsFalse(descriptions.Any(claim => claim.ValueJson.Contains("Second words.", StringComparison.Ordinal)));
        Assert.AreEqual(noticesBefore, await rig.DbAsync(database => database.NotificationEvents.CountAsync()));
    }

    [TestMethod]
    [DataRow("tied")]
    [DataRow("subMicrosecond")]
    [DataRow("backwards")]
    public async Task ACommittedRefreshWhoseLaterSupersessionTiesOrPrecedesItIsStillRecoveredNotReplayed(string mode)
    {
        var (rig, commits, _) = await NewRigAsync();
        await using var disposableRig = rig;
        var workId = await ResolvedAsync(rig);
        rig.Demo.Records["r1"] = Base("Second words.");
        commits.Arm();
        commits.AfterCommit = async ordinal =>
        {
            if (ordinal != 1) return;

            // B committed; C then supersedes it from a reading that ties with or precedes B's. B's reply is lost afterwards.
            StepClock(rig, mode);
            rig.Demo.Records["r1"] = Base("Third words.");
            Assert.AreEqual(ProviderRefreshStatus.Refreshed, DemoStep(await rig.RefreshAsync(workId)).Status);
            throw TransportFailure();
        };

        var report = await rig.RefreshAsync(workId);
        commits.AfterCommit = null;

        Assert.AreEqual(ProviderRefreshStatus.Refreshed, DemoStep(report).Status);
        Assert.AreEqual(3, rig.Demo.DetailCalls);
        Assert.AreEqual("Third words.", (await rig.LoadWorkAsync(workId)).Description, "C is not overwritten by B.");
        Assert.HasCount(3, (await rig.ClaimsAsync(workId)).Where(claim => claim.Field == MetadataClaimField.Description).ToArray());
    }

    // ---------------------------------------------------------------------------------------------------- V3: complete scopes

    [TestMethod]
    public async Task AnUnknownCommitIsVerifiedAgainstTheCompleteClaimScopeNotItsFirstTwoThousandRows()
    {
        var (rig, commits, _) = await NewRigAsync();
        await using var disposableRig = rig;
        var workId = await ResolvedAsync(rig);
        await rig.SeedDescriptionHistoryAsync(workId, sources: 201, perSource: 10);
        rig.Clock.Advance(TimeSpan.FromDays(1));
        rig.Demo.Records["r1"] = Base("Second words.");
        commits.Arm();
        commits.AfterCommit = ordinal => ordinal == 1 ? throw TransportFailure() : Task.CompletedTask;

        var report = await rig.RefreshAsync(workId);
        commits.AfterCommit = null;

        Assert.AreEqual(ProviderRefreshStatus.Refreshed, DemoStep(report).Status, "The committed claims are found among 2,000+ rows, not mistaken for absent or partial.");
        Assert.AreEqual(2, rig.Demo.DetailCalls);
        Assert.AreEqual("Second words.", (await rig.LoadWorkAsync(workId)).Description);
        Assert.AreEqual(201 * 10 + 2, (await rig.ClaimsAsync(workId)).Count(claim => claim.Field == MetadataClaimField.Description));
    }

    [TestMethod]
    public async Task ABusyCatalogLeavesARefreshStepBusyAndWritesNothing()
    {
        var (rig, _, _) = await NewRigAsync();
        await using var disposableRig = rig;
        var workId = await ResolvedAsync(rig);
        rig.Demo.Records["r1"] = Base("Second words.");
        var before = await rig.CatalogRowsAsync();
        await using var probe = new CatalogLockProbe(rig.Connection);
        await probe.HoldAsync(CancellationToken.None);

        var report = await rig.RefreshAsync(workId);
        await probe.ReleaseAsync(CancellationToken.None);

        Assert.AreEqual(ProviderRefreshStatus.Busy, DemoStep(report).Status);
        Assert.AreEqual(WorkRefreshStatus.Partial, report.Status);
        CollectionAssert.AreEqual(before, await rig.CatalogRowsAsync());
    }

    // ---------------------------------------------------------------------------------------------------- attach and creation

    [TestMethod]
    public async Task AnAttachWhoseCommitLostItsReplyIsRecoveredWithItsReferenceSnapshotAndClaimsExactlyOnce()
    {
        var (rig, commits, _) = await NewRigAsync(enableOpenLibrary: true);
        await using var disposableRig = rig;
        var workId = await ResolvedAsync(rig);
        // A follower of the Work's own author, so a wrongly emitted notice on attach would be visible.
        var authorId = (await rig.LoadWorkAsync(workId)).Authors.Single().AuthorId;
        await rig.DbAsync(async database =>
        {
            var reader = await database.Users.Select(user => user.Id).FirstAsync();
            database.Follows.Add(new Follow(reader, FollowSubjectType.Author, authorId, ObservationRig.Start));
            return 0;
        });
        rig.OpenLibrary.Records["ol"] = Book("openlibrary", "ol", "Fault Book", ["Fault Writer"],
            [Edition("Fault Book", Isbn1, "Ebook", "en"), Edition("Fault Book", Isbn2, "Paperback", "en")], [Series("Fault Cycle", "1")], "Open words.");
        commits.Arm();
        commits.AfterCommit = ordinal => ordinal == 1 ? throw TransportFailure() : Task.CompletedTask;

        var result = await rig.ResolveAsync("openlibrary", "ol");

        Assert.AreEqual(workId, result.Work.Id);
        Assert.IsFalse(result.WasCreated);
        Assert.AreEqual(1, rig.OpenLibrary.DetailCalls, "Recovery never refetches.");
        Assert.AreEqual(1, await rig.DbAsync(database => database.ExternalReferences.CountAsync(reference => reference.ExternalId == "ol")));
        Assert.HasCount(2, (await rig.LoadWorkAsync(workId)).Editions);
        Assert.HasCount(2, await rig.ObservationsAsync(workId), "One snapshot per source record, the attach's recorded once.");
        Assert.AreEqual(1, (await rig.ClaimsAsync(workId)).Count(claim => claim.Field == MetadataClaimField.Description && claim.ProviderId == "openlibrary"));
        Assert.AreEqual(0, await rig.DbAsync(database => database.NotificationEvents.CountAsync(item => item.Category == NotificationCategories.AuthorNewWorkDetected)),
            "An attach never notifies.");
    }

    [TestMethod]
    public async Task ACreationCommitThatNeverReachedTheServerIsRetriedOnceAndLeavesOneWorkWithItsEvidenceAndOneNotice()
    {
        var (rig, commits, _) = await NewRigAsync();
        await using var disposableRig = rig;
        await rig.DbAsync(async database =>
        {
            var reader = await database.Users.Select(user => user.Id).FirstAsync();
            var author = new Author("Fault Writer", null, ObservationRig.Start);
            database.Authors.Add(author);
            database.Follows.Add(new Follow(reader, FollowSubjectType.Author, author.Id, ObservationRig.Start));
            return 0;
        });
        rig.Demo.Records["r1"] = Base();
        commits.Arm();
        commits.BeforeCommit = (_, ordinal) => ordinal == 1 ? throw TransportFailure() : Task.CompletedTask;

        var result = await rig.ResolveAsync("demo", "r1");

        Assert.IsTrue(result.WasCreated);
        Assert.AreEqual(1, rig.Demo.DetailCalls);
        Assert.AreEqual(1, await rig.DbAsync(database => database.Works.CountAsync()));
        Assert.AreEqual(4, (await rig.ClaimsAsync(result.Work.Id)).Count, "The date, description, page count and series position of the one committed attempt; none from the abandoned one.");
        Assert.HasCount(1, await rig.ObservationsAsync(result.Work.Id));
        Assert.AreEqual(1, await rig.DbAsync(database => database.NotificationEvents.CountAsync(item => item.Category == NotificationCategories.AuthorNewWorkDetected)));
    }
}
